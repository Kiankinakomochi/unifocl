#if UNITY_EDITOR
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
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

        // ── S2: Manifest Discovery ──────────────────────────────────────────

        /// <summary>Cached manifest JSON from the last exchange.</summary>
        private static string _cachedManifest = string.Empty;

        /// <summary>Request the runtime manifest from the attached player.</summary>
        public static async Task<string> RequestManifestAsync(int timeoutMs = 10_000, CancellationToken cancellationToken = default)
        {
            var json = await SendRequestAsync(RuntimeMessageType.ManifestRequest, "{}", timeoutMs, cancellationToken);
            _cachedManifest = json ?? "{}";
            return _cachedManifest;
        }

        /// <summary>Return cached manifest without a round-trip (empty if never fetched).</summary>
        public static string GetCachedManifest() => _cachedManifest;

        // ── S3: Query + Command Execution ───────────────────────────────────

        /// <summary>Execute a runtime command on the attached player and return the response JSON.</summary>
        public static async Task<string> ExecuteCommandAsync(
            string command, string argsJson = "{}", int timeoutMs = 30_000,
            CancellationToken cancellationToken = default)
        {
            var requestId = Guid.NewGuid().ToString("N");
            var payload = JsonUtility.ToJson(new UniFocl.Runtime.RuntimeCommandRequest
            {
                command = command,
                argsJson = argsJson ?? "{}",
                requestId = requestId
            });
            return await SendRequestAsync(RuntimeMessageType.Request, payload, timeoutMs, cancellationToken);
        }

        // ── S4: Durable Jobs ────────────────────────────────────────────────

        private static readonly ConcurrentDictionary<string, RuntimeJobState> Jobs = new();

        /// <summary>Submit a durable job to the attached player. Returns a job ID for polling.</summary>
        public static async Task<string> SubmitJobAsync(
            string command, string argsJson = "{}", int timeoutMs = 60_000,
            CancellationToken cancellationToken = default)
        {
            var jobId = Guid.NewGuid().ToString("N")[..12];
            var job = new RuntimeJobState
            {
                jobId = jobId,
                command = command,
                state = "running",
                submittedUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
            Jobs[jobId] = job;

            // Fire-and-forget: execute the command and capture the result.
            _ = Task.Run(async () =>
            {
                try
                {
                    var resultJson = await ExecuteCommandAsync(command, argsJson, timeoutMs, cancellationToken);

                    // Parse success from RuntimeCommandResponse
                    bool success = false;
                    string message = "ok";
                    string resultPayload = "{}";
                    try
                    {
                        var resp = JsonUtility.FromJson<UniFocl.Runtime.RuntimeCommandResponse>(resultJson);
                        if (resp != null)
                        {
                            success = resp.success;
                            message = resp.message;
                            resultPayload = resp.resultJson;
                        }
                    }
                    catch
                    {
                        resultPayload = resultJson;
                        success = true;
                    }

                    if (Jobs.TryGetValue(jobId, out var j))
                    {
                        j.state = success ? "completed" : "failed";
                        j.message = message;
                        j.resultJson = resultPayload;
                        j.progress = 1f;
                    }
                }
                catch (Exception ex)
                {
                    if (Jobs.TryGetValue(jobId, out var j))
                    {
                        j.state = "failed";
                        j.message = ex.Message;
                    }
                }
            }, CancellationToken.None);

            return jobId;
        }

        public static RuntimeJobState GetJobStatus(string jobId)
        {
            return Jobs.TryGetValue(jobId, out var job) ? job : null;
        }

        public static bool CancelJob(string jobId)
        {
            if (!Jobs.TryGetValue(jobId, out var job)) return false;
            if (job.state == "completed" || job.state == "failed" || job.state == "cancelled") return false;
            job.state = "cancelled";
            job.message = "cancelled by user";
            return true;
        }

        public static List<RuntimeJobState> ListJobs()
        {
            return Jobs.Values.ToList();
        }

        // ── S5: Streams + Watches ───────────────────────────────────────────

        private static readonly ConcurrentDictionary<string, RuntimeStreamSubscription> Subscriptions = new();
        private static readonly ConcurrentDictionary<string, RuntimeWatchEntry> Watches = new();

        /// <summary>Subscribe to a named stream channel on the attached player.</summary>
        public static async Task<string> SubscribeStreamAsync(
            string channel, string filterJson = "{}", int timeoutMs = 10_000,
            CancellationToken cancellationToken = default)
        {
            var subId = Guid.NewGuid().ToString("N")[..12];
            var payload = $"{{\"subscriptionId\":\"{subId}\",\"channel\":\"{EscapeJson(channel)}\",\"filterJson\":{filterJson}}}";
            await SendRequestAsync(RuntimeMessageType.Request, JsonUtility.ToJson(
                new UniFocl.Runtime.RuntimeCommandRequest
                {
                    command = "__stream.subscribe",
                    argsJson = payload,
                    requestId = subId
                }), timeoutMs, cancellationToken);

            Subscriptions[subId] = new RuntimeStreamSubscription
            {
                subscriptionId = subId,
                channel = channel,
                filterJson = filterJson,
                active = true
            };
            return subId;
        }

        /// <summary>Unsubscribe from a stream channel.</summary>
        public static async Task<bool> UnsubscribeStreamAsync(
            string subscriptionId, int timeoutMs = 10_000,
            CancellationToken cancellationToken = default)
        {
            if (!Subscriptions.TryRemove(subscriptionId, out _)) return false;
            var payload = $"{{\"subscriptionId\":\"{EscapeJson(subscriptionId)}\"}}";
            await SendRequestAsync(RuntimeMessageType.Request, JsonUtility.ToJson(
                new UniFocl.Runtime.RuntimeCommandRequest
                {
                    command = "__stream.unsubscribe",
                    argsJson = payload,
                    requestId = subscriptionId
                }), timeoutMs, cancellationToken);
            return true;
        }

        /// <summary>Add a variable watch expression.</summary>
        public static string AddWatch(string expression, string target = "", int intervalMs = 1000)
        {
            var watchId = Guid.NewGuid().ToString("N")[..12];
            Watches[watchId] = new RuntimeWatchEntry
            {
                watchId = watchId,
                expression = expression,
                target = target,
                intervalMs = intervalMs,
                active = true,
                lastValueJson = "{}",
                lastUpdatedUtcMs = 0
            };
            return watchId;
        }

        /// <summary>Remove a variable watch.</summary>
        public static bool RemoveWatch(string watchId)
        {
            return Watches.TryRemove(watchId, out _);
        }

        /// <summary>List all active watches.</summary>
        public static List<RuntimeWatchEntry> ListWatches()
        {
            return Watches.Values.ToList();
        }

        /// <summary>Poll all active watches by executing their expressions on the attached player.</summary>
        public static async Task<List<RuntimeWatchSnapshot>> PollWatchesAsync(
            int timeoutMs = 10_000, CancellationToken cancellationToken = default)
        {
            var results = new List<RuntimeWatchSnapshot>();
            foreach (var w in Watches.Values.Where(w => w.active))
            {
                try
                {
                    var resultJson = await ExecuteCommandAsync(
                        "__watch.eval",
                        $"{{\"expression\":\"{EscapeJson(w.expression)}\",\"target\":\"{EscapeJson(w.target)}\"}}",
                        timeoutMs, cancellationToken);
                    w.lastValueJson = resultJson;
                    w.lastUpdatedUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    results.Add(new RuntimeWatchSnapshot
                    {
                        watchId = w.watchId,
                        expression = w.expression,
                        valueJson = resultJson,
                        timestampUtcMs = w.lastUpdatedUtcMs
                    });
                }
                catch
                {
                    // Watch eval failure is non-fatal
                }
            }
            return results;
        }

        // ── S5 helper types ─────────────────────────────────────────────────

        private static string EscapeJson(string value)
        {
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
        }

        /// <summary>Public accessor for JSON escaping used by daemon endpoints.</summary>
        public static string EscapeJsonPublic(string value) => EscapeJson(value);

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

        /// <summary>Editor-side state for a durable runtime job.</summary>
        public sealed class RuntimeJobState
        {
            public string jobId = string.Empty;
            public string command = string.Empty;
            public string state = "pending"; // pending, running, completed, failed, cancelled
            public float progress;
            public string message = string.Empty;
            public string resultJson = "{}";
            public long submittedUtcMs;
        }

        /// <summary>Editor-side state for a stream subscription.</summary>
        public sealed class RuntimeStreamSubscription
        {
            public string subscriptionId = string.Empty;
            public string channel = string.Empty;
            public string filterJson = "{}";
            public bool active;
        }

        /// <summary>Editor-side state for a variable watch.</summary>
        public sealed class RuntimeWatchEntry
        {
            public string watchId = string.Empty;
            public string expression = string.Empty;
            public string target = string.Empty;
            public int intervalMs = 1000;
            public bool active;
            public string lastValueJson = "{}";
            public long lastUpdatedUtcMs;
        }

        /// <summary>Snapshot of a watch poll result.</summary>
        public sealed class RuntimeWatchSnapshot
        {
            public string watchId = string.Empty;
            public string expression = string.Empty;
            public string valueJson = "{}";
            public long timestampUtcMs;
        }
    }
}
#endif
