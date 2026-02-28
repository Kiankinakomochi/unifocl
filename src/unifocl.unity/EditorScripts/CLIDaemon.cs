#if UNITY_EDITOR
using System;
using System.IO;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
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
        private static TcpListener? _listener;
        private static CancellationTokenSource? _cts;
        private static DateTime _lastActivityUtc;
        private static bool _running;
        private static bool _autoExitEditor;
        private static int _inactivityTtlSeconds;
        private static bool _assetIndexDirty = true;
        private static int _assetIndexRevision = 1;
        private static readonly object AssetIndexSync = new();

        public static bool HasDaemonServiceArg()
        {
            var args = Environment.GetCommandLineArgs();
            return Array.IndexOf(args, "--daemon-service") >= 0;
        }

        // Entry point for -executeMethod CLIDaemon.StartServer
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
            _listener = new TcpListener(IPAddress.Loopback, options.port);
            _listener.Start();
            _running = true;

            EditorApplication.update -= OnEditorUpdate;
            EditorApplication.update += OnEditorUpdate;

            _ = Task.Run(() => AcceptLoopAsync(_cts.Token));

            Debug.Log($"[unifocl] CLIDaemon started on 127.0.0.1:{options.port} (batch={Application.isBatchMode}, autoExit={_autoExitEditor})");
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
                    var client = await _listener.AcceptTcpClientAsync();
                    _ = Task.Run(() => HandleClientAsync(client, cancellationToken), cancellationToken);
                }
            }
            catch (ObjectDisposedException)
            {
            }
            catch (SocketException)
            {
            }
        }

        private static async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
        {
            using (client)
            await using (var stream = client.GetStream())
            using (var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, true))
            using (var writer = new StreamWriter(stream, Encoding.UTF8, 1024, true) { AutoFlush = true })
            {
                var line = await reader.ReadLineAsync();
                var command = line?.Trim().ToUpperInvariant();

                switch (command)
                {
                    case "PING":
                        await writer.WriteLineAsync("PONG");
                        break;
                    case "TOUCH":
                        MarkActivity();
                        await writer.WriteLineAsync("OK");
                        break;
                    case "STOP":
                        await writer.WriteLineAsync("STOPPING");
                        StopInternal(quitEditor: Application.isBatchMode);
                        break;
                    default:
                        if (line is not null && TryHandleAssetIndexCommand(line, out var assetPayload))
                        {
                            MarkActivity();
                            await writer.WriteLineAsync(assetPayload);
                            break;
                        }

                        MarkActivity();
                        await writer.WriteLineAsync("ERR");
                        break;
                }
            }
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

        private static bool TryHandleAssetIndexCommand(string line, out string response)
        {
            response = string.Empty;
            if (line.Equals("ASSET_INDEX_GET", StringComparison.Ordinal))
            {
                response = BuildAssetIndexPayload(knownRevision: null);
                return true;
            }

            if (!line.StartsWith("ASSET_INDEX_SYNC ", StringComparison.Ordinal))
            {
                return false;
            }

            var knownRevisionRaw = line.Substring("ASSET_INDEX_SYNC ".Length);
            int? knownRevision = null;
            if (int.TryParse(knownRevisionRaw, out var parsed))
            {
                knownRevision = parsed;
            }

            response = BuildAssetIndexPayload(knownRevision);
            return true;
        }

        private static string BuildAssetIndexPayload(int? knownRevision)
        {
            lock (AssetIndexSync)
            {
                if (_assetIndexDirty)
                {
                    _assetIndexRevision++;
                }

                if (knownRevision.HasValue && knownRevision.Value == _assetIndexRevision && !_assetIndexDirty)
                {
                    return JsonUtility.ToJson(new AssetIndexSyncResponse
                    {
                        revision = _assetIndexRevision,
                        unchanged = true,
                        entries = Array.Empty<AssetIndexEntry>()
                    });
                }

                var paths = CollectPathsFromSearchService();
                if (paths.Count == 0)
                {
                    paths = FallbackEnumerateAssetPaths();
                }

                var entries = new AssetIndexEntry[paths.Count];
                for (var i = 0; i < paths.Count; i++)
                {
                    var path = paths[i];
                    entries[i] = new AssetIndexEntry
                    {
                        instanceId = StablePathId(path),
                        path = path
                    };
                }

                _assetIndexDirty = false;
                return JsonUtility.ToJson(new AssetIndexSyncResponse
                {
                    revision = _assetIndexRevision,
                    unchanged = false,
                    entries = entries
                });
            }
        }

        private static List<string> CollectPathsFromSearchService()
        {
            // Reflection keeps this package resilient across Unity versions where SearchService signatures differ.
            var paths = new List<string>();
            try
            {
                var editorAssembly = typeof(EditorApplication).Assembly;
                var searchServiceType = editorAssembly.GetType("UnityEditor.Search.SearchService");
                if (searchServiceType is null)
                {
                    return paths;
                }

                var requestMethod = searchServiceType
                    .GetMethods()
                    .FirstOrDefault(m => m.Name == "Request" && m.GetParameters().Length >= 1 && m.GetParameters()[0].ParameterType == typeof(string));
                if (requestMethod is null)
                {
                    return paths;
                }

                var requestResult = requestMethod.Invoke(null, new object[] { "p:" });
                if (requestResult is not IEnumerable enumerable)
                {
                    return paths;
                }

                foreach (var item in enumerable)
                {
                    if (item is null)
                    {
                        continue;
                    }

                    var itemType = item.GetType();
                    var idProp = itemType.GetProperty("id");
                    var value = idProp?.GetValue(item) as string;
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        continue;
                    }

                    if (!value.StartsWith("Assets/", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (value.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    paths.Add(value);
                }
            }
            catch
            {
            }

            return paths
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static List<string> FallbackEnumerateAssetPaths()
        {
            var list = new List<string>();
            var root = Application.dataPath;
            if (!Directory.Exists(root))
            {
                return list;
            }

            foreach (var absolutePath in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                if (absolutePath.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var relative = "Assets/" + Path.GetRelativePath(root, absolutePath).Replace('\\', '/');
                list.Add(relative);
            }

            return list
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static int StablePathId(string path)
        {
            unchecked
            {
                uint hash = 2166136261;
                foreach (var ch in path)
                {
                    hash ^= char.ToLowerInvariant(ch);
                    hash *= 16777619;
                }

                return (int)(hash & 0x7FFFFFFF);
            }
        }

        internal static void MarkAssetIndexDirty()
        {
            lock (AssetIndexSync)
            {
                _assetIndexDirty = true;
            }
        }

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

    [Serializable]
    internal sealed class AssetIndexEntry
    {
        public int instanceId;
        public string path = string.Empty;
    }

    [Serializable]
    internal sealed class AssetIndexSyncResponse
    {
        public int revision;
        public bool unchanged;
        public AssetIndexEntry[] entries = Array.Empty<AssetIndexEntry>();
    }
}
#endif
