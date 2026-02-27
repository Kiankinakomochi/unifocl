#if UNITY_EDITOR
using System;
using System.IO;
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
}
#endif
