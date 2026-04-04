#if UNITY_EDITOR
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UniFocl.Runtime;
using UnityEditor;
using UnityEditor.Networking.PlayerConnection;
using UnityEngine;
using UnityEngine.Networking.PlayerConnection;

namespace UniFocl.EditorBridge
{
    /// <summary>
    /// Editor-side bridge for runtime target communication.
    /// Wraps <see cref="EditorConnection"/> with chunked send/receive, correlation-based
    /// request/response matching, and target enumeration.
    /// </summary>
    internal static class DaemonRuntimeBridge
    {
        private static readonly ChunkAccumulator Accumulator = new();
        private static readonly ConcurrentDictionary<string, TaskCompletionSource<string>> PendingRequests = new();
        private static bool _registered;
        private static int _attachedPlayerId = -1;
        private static string _attachedTargetAddress = string.Empty;

        /// <summary>Current connection state.</summary>
        public static RuntimeConnectionState ConnectionState { get; private set; } = RuntimeConnectionState.Disconnected;

        /// <summary>Address of the currently attached target, or empty.</summary>
        public static string AttachedTargetAddress => _attachedTargetAddress;

        /// <summary>Player ID of the currently attached target, or -1.</summary>
        public static int AttachedPlayerId => _attachedPlayerId;

        public static void EnsureRegistered()
        {
            if (_registered) return;
            _registered = true;

            EditorConnection.instance.Register(RuntimeMessageGuids.PlayerToEditor, OnMessageFromPlayer);
        }

        /// <summary>List available runtime targets (profiler connection endpoints + connected players).</summary>
        public static List<RuntimeTargetInfo> ListTargets()
        {
            var result = new List<RuntimeTargetInfo>();

            // Editor PlayMode is always a target when playing
            if (EditorApplication.isPlaying)
            {
                result.Add(new RuntimeTargetInfo
                {
                    playerId = 0,
                    name = "playmode",
                    platform = "editor",
                    deviceId = "local",
                    isConnected = _attachedPlayerId == 0
                });
            }

            // Connected remote players
            var players = EditorConnection.instance.ConnectedPlayers;
            if (players != null)
            {
                foreach (var player in players)
                {
                    result.Add(new RuntimeTargetInfo
                    {
                        playerId = player.playerId,
                        name = player.name ?? $"player-{player.playerId}",
                        platform = ResolvePlatform(player.name),
                        deviceId = player.playerId.ToString(),
                        isConnected = _attachedPlayerId == player.playerId
                    });
                }
            }

            return result;
        }

        /// <summary>Attach to a target by address. Returns true on success.</summary>
        public static bool Attach(string targetAddress)
        {
            EnsureRegistered();

            var addr = ParseAddress(targetAddress);
            var targets = ListTargets();

            RuntimeTargetInfo? match = null;
            foreach (var t in targets)
            {
                if (t.platform.Equals(addr.platform, StringComparison.OrdinalIgnoreCase)
                    && (addr.name == "*" || t.name.Contains(addr.name, StringComparison.OrdinalIgnoreCase)))
                {
                    match = t;
                    break;
                }
            }

            if (match == null)
            {
                Debug.LogWarning($"[unifocl] no runtime target found matching '{targetAddress}'");
                ConnectionState = RuntimeConnectionState.Error;
                return false;
            }

            _attachedPlayerId = match.Value.playerId;
            _attachedTargetAddress = targetAddress;
            ConnectionState = RuntimeConnectionState.Connected;

            Debug.Log($"[unifocl] attached to runtime target: {match.Value.name} (playerId={match.Value.playerId})");
            return true;
        }

        /// <summary>Detach from the current target.</summary>
        public static void Detach()
        {
            _attachedPlayerId = -1;
            _attachedTargetAddress = string.Empty;
            ConnectionState = RuntimeConnectionState.Disconnected;
        }

        /// <summary>
        /// Send a command envelope to the attached player and await the response.
        /// Returns the response payload JSON, or throws on timeout/error.
        /// </summary>
        public static async Task<string> SendRequestAsync(
            RuntimeMessageType type,
            string payload,
            int timeoutMs = 30_000,
            CancellationToken cancellationToken = default)
        {
            if (_attachedPlayerId < 0)
            {
                throw new InvalidOperationException("no runtime target attached");
            }

            EnsureRegistered();

            var correlationId = Guid.NewGuid().ToString("N");
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            PendingRequests[correlationId] = tcs;

            try
            {
                var envelopes = ChunkPayload(correlationId, type, payload);
                foreach (var env in envelopes)
                {
                    var json = JsonUtility.ToJson(env);
                    var bytes = Encoding.UTF8.GetBytes(json);

                    if (_attachedPlayerId == 0)
                    {
                        // Editor PlayMode: route locally via PlayerConnection simulation
                        // For now, send through EditorConnection to all connected players
                        EditorConnection.instance.Send(RuntimeMessageGuids.EditorToPlayer, bytes);
                    }
                    else
                    {
                        EditorConnection.instance.Send(RuntimeMessageGuids.EditorToPlayer, bytes, _attachedPlayerId);
                    }
                }

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(timeoutMs);

                var registration = cts.Token.Register(() => tcs.TrySetCanceled());
                try
                {
                    return await tcs.Task;
                }
                finally
                {
                    registration.Dispose();
                }
            }
            finally
            {
                PendingRequests.TryRemove(correlationId, out _);
            }
        }

        /// <summary>Send a ping and await pong. Returns round-trip milliseconds.</summary>
        public static async Task<long> PingAsync(int timeoutMs = 5000, CancellationToken cancellationToken = default)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await SendRequestAsync(RuntimeMessageType.Ping, "{}", timeoutMs, cancellationToken);
            sw.Stop();
            return sw.ElapsedMilliseconds;
        }

        private static void OnMessageFromPlayer(MessageEventArgs args)
        {
            RuntimeEnvelope envelope;
            try
            {
                var json = Encoding.UTF8.GetString(args.data);
                envelope = JsonUtility.FromJson<RuntimeEnvelope>(json);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[unifocl] failed to deserialize runtime envelope: {ex.Message}");
                return;
            }

            if (envelope == null) return;

            var assembled = Accumulator.TryAccumulate(envelope);
            if (assembled == null) return; // waiting for more chunks

            if (PendingRequests.TryRemove(envelope.correlationId, out var tcs))
            {
                tcs.TrySetResult(assembled);
            }
        }

        private static List<RuntimeEnvelope> ChunkPayload(string correlationId, RuntimeMessageType type, string payload)
        {
            var result = new List<RuntimeEnvelope>();
            if (string.IsNullOrEmpty(payload)) payload = "{}";

            var payloadBytes = Encoding.UTF8.GetByteCount(payload);
            if (payloadBytes <= RuntimeTransportConstants.MaxChunkBytes)
            {
                result.Add(new RuntimeEnvelope
                {
                    correlationId = correlationId,
                    messageType = (int)type,
                    payload = payload,
                    isChunked = false,
                    chunkIndex = 0,
                    totalChunks = 1
                });
                return result;
            }

            var chunks = new List<string>();
            var sb = new StringBuilder();
            var currentBytes = 0;
            foreach (var ch in payload)
            {
                var charBytes = Encoding.UTF8.GetByteCount(new[] { ch });
                if (currentBytes + charBytes > RuntimeTransportConstants.MaxChunkBytes)
                {
                    chunks.Add(sb.ToString());
                    sb.Clear();
                    currentBytes = 0;
                }
                sb.Append(ch);
                currentBytes += charBytes;
            }
            if (sb.Length > 0) chunks.Add(sb.ToString());

            for (var i = 0; i < chunks.Count; i++)
            {
                result.Add(new RuntimeEnvelope
                {
                    correlationId = correlationId,
                    messageType = (int)type,
                    payload = chunks[i],
                    isChunked = true,
                    chunkIndex = i,
                    totalChunks = chunks.Count
                });
            }

            return result;
        }

        private static (string platform, string name) ParseAddress(string address)
        {
            if (string.IsNullOrWhiteSpace(address))
                return ("editor", "playmode");

            var colonIdx = address.IndexOf(':');
            if (colonIdx < 0)
                return (address.Trim().ToLowerInvariant(), "*");

            return (
                address[..colonIdx].Trim().ToLowerInvariant(),
                address[(colonIdx + 1)..].Trim()
            );
        }

        private static string ResolvePlatform(string playerName)
        {
            if (string.IsNullOrEmpty(playerName)) return "unknown";
            var lower = playerName.ToLowerInvariant();
            if (lower.Contains("android")) return "android";
            if (lower.Contains("ios") || lower.Contains("iphone") || lower.Contains("ipad")) return "ios";
            if (lower.Contains("windows") || lower.Contains("win")) return "windows";
            if (lower.Contains("mac") || lower.Contains("osx")) return "macos";
            if (lower.Contains("linux")) return "linux";
            if (lower.Contains("webgl")) return "webgl";
            if (lower.Contains("switch")) return "switch";
            if (lower.Contains("ps4") || lower.Contains("ps5") || lower.Contains("playstation")) return "playstation";
            if (lower.Contains("xbox")) return "xbox";
            return "standalone";
        }

        /// <summary>Lightweight struct for target enumeration results.</summary>
        public struct RuntimeTargetInfo
        {
            public int playerId;
            public string name;
            public string platform;
            public string deviceId;
            public bool isConnected;
        }
    }
}
#endif
