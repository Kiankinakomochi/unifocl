using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking.PlayerConnection;

namespace UniFocl.Runtime
{
    /// <summary>
    /// Player-side runtime client that receives command envelopes from the Editor via
    /// <see cref="PlayerConnection"/>, dispatches them to registered handlers, and
    /// sends response envelopes back.
    ///
    /// Auto-initialises on player startup. Persists across scene loads.
    /// </summary>
    public sealed class UnifoclRuntimeClient : MonoBehaviour
    {
        private static UnifoclRuntimeClient _instance;
        private readonly ChunkAccumulator _accumulator = new();
        private readonly Dictionary<string, Func<string, string>> _handlers = new(StringComparer.OrdinalIgnoreCase);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoInitialize()
        {
            if (_instance != null) return;

            var go = new GameObject("[unifocl.runtime]");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<UnifoclRuntimeClient>();
        }

        /// <summary>
        /// Register a command handler. Called by <see cref="RuntimeCommandRegistry"/> during discovery.
        /// </summary>
        public static void RegisterHandler(string commandName, Func<string, string> handler)
        {
            if (_instance == null)
            {
                Debug.LogWarning($"[unifocl.runtime] cannot register handler '{commandName}': client not initialized");
                return;
            }

            _instance._handlers[commandName] = handler;
        }

        private void OnEnable()
        {
            PlayerConnection.instance.Register(RuntimeMessageGuids.EditorToPlayer, OnMessageFromEditor);
        }

        private void OnDisable()
        {
            PlayerConnection.instance.Unregister(RuntimeMessageGuids.EditorToPlayer, OnMessageFromEditor);
        }

        private void OnMessageFromEditor(MessageEventArgs args)
        {
            RuntimeEnvelope envelope;
            try
            {
                var json = Encoding.UTF8.GetString(args.data);
                envelope = JsonUtility.FromJson<RuntimeEnvelope>(json);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[unifocl.runtime] failed to deserialize envelope: {ex.Message}");
                return;
            }

            if (envelope == null) return;

            var messageType = (RuntimeMessageType)envelope.messageType;

            switch (messageType)
            {
                case RuntimeMessageType.Ping:
                    SendEnvelope(new RuntimeEnvelope
                    {
                        correlationId = envelope.correlationId,
                        messageType = (int)RuntimeMessageType.Pong,
                        payload = "{}"
                    });
                    break;

                case RuntimeMessageType.ManifestRequest:
                    HandleManifestRequest(envelope);
                    break;

                case RuntimeMessageType.Request:
                    var assembled = _accumulator.TryAccumulate(envelope);
                    if (assembled != null)
                    {
                        HandleCommandRequest(envelope.correlationId, assembled);
                    }
                    break;

                default:
                    Debug.LogWarning($"[unifocl.runtime] unexpected message type: {messageType}");
                    break;
            }
        }

        private void HandleManifestRequest(RuntimeEnvelope envelope)
        {
            var manifest = RuntimeCommandRegistry.BuildManifestJson();
            SendResponse(envelope.correlationId, RuntimeMessageType.ManifestResponse, manifest);
        }

        private void HandleCommandRequest(string correlationId, string payload)
        {
            RuntimeCommandRequest request;
            try
            {
                request = JsonUtility.FromJson<RuntimeCommandRequest>(payload);
            }
            catch (Exception ex)
            {
                SendCommandResponse(correlationId, new RuntimeCommandResponse
                {
                    requestId = string.Empty,
                    success = false,
                    message = $"failed to parse command request: {ex.Message}"
                });
                return;
            }

            if (request == null || string.IsNullOrEmpty(request.command))
            {
                SendCommandResponse(correlationId, new RuntimeCommandResponse
                {
                    requestId = request?.requestId ?? string.Empty,
                    success = false,
                    message = "command name is required"
                });
                return;
            }

#if !UNIFOCL_RUNTIME_ALLOW_MUTATIONS
            var info = RuntimeCommandRegistry.GetCommandInfo(request.command);
            if (info != null && info.Value.risk != RuntimeRiskLevel.SafeRead)
            {
                SendCommandResponse(correlationId, new RuntimeCommandResponse
                {
                    requestId = request.requestId,
                    success = false,
                    message = $"mutations are disabled on this build (missing UNIFOCL_RUNTIME_ALLOW_MUTATIONS define). Command '{request.command}' requires risk level '{info.Value.risk}'."
                });
                return;
            }
#endif

            if (!_handlers.TryGetValue(request.command, out var handler))
            {
                SendCommandResponse(correlationId, new RuntimeCommandResponse
                {
                    requestId = request.requestId,
                    success = false,
                    message = $"unknown command: {request.command}"
                });
                return;
            }

            try
            {
                var resultJson = handler(request.argsJson);
                SendCommandResponse(correlationId, new RuntimeCommandResponse
                {
                    requestId = request.requestId,
                    success = true,
                    message = "ok",
                    resultJson = resultJson ?? "{}"
                });
            }
            catch (Exception ex)
            {
                SendCommandResponse(correlationId, new RuntimeCommandResponse
                {
                    requestId = request.requestId,
                    success = false,
                    message = $"command execution failed: {ex.Message}"
                });
            }
        }

        private void SendCommandResponse(string correlationId, RuntimeCommandResponse response)
        {
            var payload = JsonUtility.ToJson(response);
            SendResponse(correlationId, RuntimeMessageType.Response, payload);
        }

        private void SendResponse(string correlationId, RuntimeMessageType type, string payload)
        {
            var envelopes = ChunkPayload(correlationId, type, payload);
            foreach (var env in envelopes)
            {
                SendEnvelope(env);
            }
        }

        private void SendEnvelope(RuntimeEnvelope envelope)
        {
            var json = JsonUtility.ToJson(envelope);
            var bytes = Encoding.UTF8.GetBytes(json);
            PlayerConnection.instance.Send(RuntimeMessageGuids.PlayerToEditor, bytes);
        }

        private static List<RuntimeEnvelope> ChunkPayload(string correlationId, RuntimeMessageType type, string payload)
        {
            var result = new List<RuntimeEnvelope>();
            if (string.IsNullOrEmpty(payload))
            {
                payload = "{}";
            }

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

            // Split payload into chunks by character boundaries that fit within byte limit
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

            if (sb.Length > 0)
            {
                chunks.Add(sb.ToString());
            }

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
    }
}
