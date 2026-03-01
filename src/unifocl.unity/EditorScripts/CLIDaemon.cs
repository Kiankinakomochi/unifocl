#if UNITY_EDITOR
using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UniFocl.SharedModels;
using UnityEditor;
using UnityEngine;

namespace UniFocl.EditorBridge
{
    [InitializeOnLoad]
    internal static class CLIDaemonInitializeOnLoad
    {
        static CLIDaemonInitializeOnLoad()
        {
            if (Application.isBatchMode)
            {
                return;
            }

            if (CLIDaemon.HasDaemonServiceArg())
            {
                return;
            }

            CLIDaemon.TryStartInitializeOnLoadBridge();
        }
    }

    public static class CLIDaemon
    {
        private static HttpListener? _listener;
        private static CancellationTokenSource? _cts;
        private static DateTime _lastActivityUtc;
        private static bool _running;
        private static bool _autoExitEditor;
        private static int _inactivityTtlSeconds;

        public static bool HasDaemonServiceArg()
        {
            var args = Environment.GetCommandLineArgs();
            return Array.IndexOf(args, "--daemon-service") >= 0;
        }

        public static void StartServer()
        {
            var options = ParseServiceArgs(Environment.GetCommandLineArgs());
            StartInternal(options, autoExitEditorOnInactivity: true);

            while (_running)
            {
                Thread.Sleep(200);
            }
        }

        public static void TryStartInitializeOnLoadBridge()
        {
            if (_running)
            {
                return;
            }

            var options = LoadBridgeOptionsFromProject();
            StartInternal(options, autoExitEditorOnInactivity: false);
        }

        private static void StartInternal(DaemonServiceArgs options, bool autoExitEditorOnInactivity)
        {
            if (_running)
            {
                return;
            }

            _autoExitEditor = autoExitEditorOnInactivity;
            _inactivityTtlSeconds = Math.Max(1, options.ttlSeconds);
            _lastActivityUtc = DateTime.UtcNow;

            _cts = new CancellationTokenSource();
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{options.port}/");
            _listener.Start();
            _running = true;

            EditorApplication.update -= OnEditorUpdate;
            EditorApplication.update += OnEditorUpdate;

            _ = Task.Run(() => AcceptLoopAsync(_cts.Token));

            Debug.Log($"[unifocl] CLIDaemon started on http://127.0.0.1:{options.port}/ (batch={Application.isBatchMode}, autoExit={_autoExitEditor})");
        }

        private static async Task AcceptLoopAsync(CancellationToken cancellationToken)
        {
            if (_listener is null)
            {
                return;
            }

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    HttpListenerContext context;
                    try
                    {
                        context = await _listener.GetContextAsync();
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }
                    catch (HttpListenerException)
                    {
                        break;
                    }

                    _ = Task.Run(() => HandleRequestAsync(context, cancellationToken), cancellationToken);
                }
            }
            catch (ObjectDisposedException)
            {
            }
            catch (HttpListenerException)
            {
            }
        }

        private static async Task HandleRequestAsync(HttpListenerContext context, CancellationToken cancellationToken)
        {
            _ = cancellationToken;

            var request = context.Request;
            var path = (request.Url?.AbsolutePath ?? "/").TrimEnd('/');
            if (path.Length == 0)
            {
                path = "/";
            }

            try
            {
                if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) && path.Equals("/ping", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteTextResponseAsync(context.Response, "PONG");
                    return;
                }

                if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) && path.Equals("/touch", StringComparison.OrdinalIgnoreCase))
                {
                    MarkActivity();
                    await WriteTextResponseAsync(context.Response, "OK");
                    return;
                }

                if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) && path.Equals("/stop", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteTextResponseAsync(context.Response, "STOPPING");
                    StopInternal(quitEditor: Application.isBatchMode);
                    return;
                }

                if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) && path.Equals("/asset-index", StringComparison.OrdinalIgnoreCase))
                {
                    var revisionRaw = request.QueryString["revision"];
                    int? knownRevision = null;
                    if (int.TryParse(revisionRaw, out var revision) && revision > 0)
                    {
                        knownRevision = revision;
                    }

                    MarkActivity();
                    await WriteJsonResponseAsync(context.Response, DaemonAssetIndexService.BuildPayload(knownRevision));
                    return;
                }

                if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) && path.Equals("/hierarchy/snapshot", StringComparison.OrdinalIgnoreCase))
                {
                    MarkActivity();
                    var response = await ExecuteOnMainThreadAsync(DaemonHierarchyService.BuildSnapshotPayload);
                    await WriteJsonResponseAsync(context.Response, response);
                    return;
                }

                if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) && path.Equals("/hierarchy/command", StringComparison.OrdinalIgnoreCase))
                {
                    var payload = await ReadRequestBodyAsync(request);
                    MarkActivity();
                    var response = await ExecuteOnMainThreadAsync(() => DaemonHierarchyService.ExecuteCommand(payload));
                    await WriteJsonResponseAsync(context.Response, response);
                    return;
                }

                if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) && path.Equals("/hierarchy/find", StringComparison.OrdinalIgnoreCase))
                {
                    var payload = await ReadRequestBodyAsync(request);
                    MarkActivity();
                    var response = await ExecuteOnMainThreadAsync(() => DaemonHierarchyService.ExecuteSearch(payload));
                    await WriteJsonResponseAsync(context.Response, response);
                    return;
                }

                if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) && path.Equals("/inspect", StringComparison.OrdinalIgnoreCase))
                {
                    var payload = await ReadRequestBodyAsync(request);
                    MarkActivity();
                    var response = await ExecuteOnMainThreadAsync(() => DaemonInspectorService.Execute(payload));
                    await WriteJsonResponseAsync(context.Response, response);
                    return;
                }

                if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) && path.Equals("/project/command", StringComparison.OrdinalIgnoreCase))
                {
                    var payload = await ReadRequestBodyAsync(request);
                    MarkActivity();
                    var response = await ExecuteOnMainThreadAsync(() => DaemonProjectService.Execute(payload));
                    await WriteJsonResponseAsync(context.Response, response);
                    return;
                }

                MarkActivity();
                await WriteTextResponseAsync(context.Response, "ERR", 404);
            }
            catch
            {
                try
                {
                    await WriteTextResponseAsync(context.Response, "ERR", 500);
                }
                catch
                {
                }
            }
        }

        private static async Task<string> ReadRequestBodyAsync(HttpListenerRequest request)
        {
            using var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8);
            return await reader.ReadToEndAsync();
        }

        private static async Task WriteTextResponseAsync(HttpListenerResponse response, string payload, int statusCode = 200)
        {
            var bytes = Encoding.UTF8.GetBytes(payload + Environment.NewLine);
            response.StatusCode = statusCode;
            response.ContentType = "text/plain; charset=utf-8";
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
            response.Close();
        }

        private static async Task WriteJsonResponseAsync(HttpListenerResponse response, string payload, int statusCode = 200)
        {
            var bytes = Encoding.UTF8.GetBytes(payload + Environment.NewLine);
            response.StatusCode = statusCode;
            response.ContentType = "application/json; charset=utf-8";
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
            response.Close();
        }

        private static void MarkActivity()
        {
            _lastActivityUtc = DateTime.UtcNow;
        }

        private static void OnEditorUpdate()
        {
            if (!_running || !_autoExitEditor)
            {
                return;
            }

            if (DateTime.UtcNow - _lastActivityUtc < TimeSpan.FromSeconds(_inactivityTtlSeconds))
            {
                return;
            }

            Debug.Log("[unifocl] CLIDaemon inactivity TTL reached; exiting editor process.");
            StopInternal(quitEditor: true);
        }

        private static void StopInternal(bool quitEditor)
        {
            if (!_running)
            {
                return;
            }

            _running = false;
            try
            {
                _cts?.Cancel();
            }
            catch
            {
            }

            try
            {
                _listener?.Stop();
            }
            catch
            {
            }

            _cts?.Dispose();
            _cts = null;
            _listener = null;

            EditorApplication.update -= OnEditorUpdate;

            if (quitEditor)
            {
                EditorApplication.Exit(0);
            }
        }

        private static Task<string> ExecuteOnMainThreadAsync(Func<string> work)
        {
            var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            EditorApplication.delayCall += Execute;
            return completion.Task;

            void Execute()
            {
                try
                {
                    completion.TrySetResult(work());
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            }
        }

        internal static void MarkAssetIndexDirty() => DaemonAssetIndexService.MarkDirty();

        private static DaemonServiceArgs ParseServiceArgs(string[] args)
        {
            var parsed = new DaemonServiceArgs();
            parsed.headless = true;

            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--port":
                        if (i + 1 < args.Length && int.TryParse(args[i + 1], out var port))
                        {
                            parsed.port = port;
                            i++;
                        }
                        break;
                    case "--project":
                        if (i + 1 < args.Length)
                        {
                            parsed.projectPath = args[i + 1];
                            i++;
                        }
                        break;
                    case "--ttl-seconds":
                        if (i + 1 < args.Length && int.TryParse(args[i + 1], out var ttl))
                        {
                            parsed.ttlSeconds = Math.Max(1, ttl);
                            i++;
                        }
                        break;
                }
            }

            if (string.IsNullOrWhiteSpace(parsed.projectPath))
            {
                parsed.projectPath = Directory.GetParent(Application.dataPath)?.FullName ?? Directory.GetCurrentDirectory();
            }

            return parsed;
        }

        private static DaemonServiceArgs LoadBridgeOptionsFromProject()
        {
            var projectPath = Directory.GetParent(Application.dataPath)?.FullName ?? Directory.GetCurrentDirectory();
            var bridgePath = Path.Combine(projectPath, ".unifocl", "bridge.json");
            var parsed = new DaemonServiceArgs
            {
                projectPath = projectPath,
                ttlSeconds = 600,
                headless = false
            };

            if (!File.Exists(bridgePath))
            {
                return parsed;
            }

            try
            {
                var json = File.ReadAllText(bridgePath);
                var bridge = JsonUtility.FromJson<BridgeConfig>(json);
                if (bridge?.daemon is not null && bridge.daemon.port > 0)
                {
                    parsed.port = bridge.daemon.port;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[unifocl] Failed to parse bridge config at {bridgePath}: {ex.Message}");
            }

            return parsed;
        }
    }

    internal sealed class CLIDaemonAssetPostprocessor : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(
            string[] importedAssets,
            string[] deletedAssets,
            string[] movedAssets,
            string[] movedFromAssetPaths)
        {
            _ = importedAssets;
            _ = deletedAssets;
            _ = movedAssets;
            _ = movedFromAssetPaths;
            CLIDaemon.MarkAssetIndexDirty();
        }
    }
}
#endif
