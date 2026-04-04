using System;
using UnityEngine;

namespace UniFocl.Runtime
{
    /// <summary>Message type discriminator for the runtime transport protocol.</summary>
    public enum RuntimeMessageType : byte
    {
        Request = 0,
        Response = 1,
        StreamFrame = 2,
        ManifestRequest = 3,
        ManifestResponse = 4,
        Ping = 5,
        Pong = 6,
    }

    /// <summary>
    /// Envelope for all runtime transport messages between Editor and Player.
    /// Serialized as JSON over Unity's EditorConnection / PlayerConnection.
    /// Large payloads are chunked into 16 KB segments by the transport layer.
    /// </summary>
    [Serializable]
    public sealed class RuntimeEnvelope
    {
        /// <summary>Correlation ID for request/response matching.</summary>
        public string correlationId = string.Empty;

        /// <summary>Discriminator: request, response, stream-frame, manifest, ping/pong.</summary>
        public int messageType;

        /// <summary>UTF-8 JSON payload (command args, result, manifest, etc.).</summary>
        public string payload = string.Empty;

        /// <summary>When true, this is one chunk of a multi-part message.</summary>
        public bool isChunked;

        /// <summary>Zero-based index of this chunk within the chunked sequence.</summary>
        public int chunkIndex;

        /// <summary>Total number of chunks in the sequence (valid only when <see cref="isChunked"/> is true).</summary>
        public int totalChunks;
    }

    /// <summary>
    /// Payload carried inside a <see cref="RuntimeEnvelope"/> of type <see cref="RuntimeMessageType.Request"/>.
    /// </summary>
    [Serializable]
    public sealed class RuntimeCommandRequest
    {
        /// <summary>Fully qualified command name, e.g. "app.status" or "economy.grant".</summary>
        public string command = string.Empty;

        /// <summary>JSON-serialized arguments. Empty object "{}" when the command takes no args.</summary>
        public string argsJson = "{}";

        /// <summary>Unique ID for durable job tracking.</summary>
        public string requestId = string.Empty;
    }

    /// <summary>
    /// Payload carried inside a <see cref="RuntimeEnvelope"/> of type <see cref="RuntimeMessageType.Response"/>.
    /// </summary>
    [Serializable]
    public sealed class RuntimeCommandResponse
    {
        public string requestId = string.Empty;
        public bool success;
        public string message = string.Empty;

        /// <summary>JSON-serialized result object. Null/empty on failure.</summary>
        public string resultJson = string.Empty;
    }

    /// <summary>Well-known GUIDs for EditorConnection / PlayerConnection message registration.</summary>
    public static class RuntimeMessageGuids
    {
        /// <summary>Editor → Player: command/query/manifest request.</summary>
        public static readonly Guid EditorToPlayer = new("b8f3a1e0-7c4d-4f2e-9a6b-3d5e8f1c2a40");

        /// <summary>Player → Editor: command/query/manifest response and stream frames.</summary>
        public static readonly Guid PlayerToEditor = new("c9e4b2f1-8d5e-4a3f-ab7c-4e6f9a2d3b51");
    }

    /// <summary>
    /// Chunk-level transport constants.
    /// </summary>
    public static class RuntimeTransportConstants
    {
        /// <summary>Maximum payload bytes per chunk before splitting.</summary>
        public const int MaxChunkBytes = 16 * 1024;
    }

    /// <summary>Connection state for the editor-side runtime bridge.</summary>
    public enum RuntimeConnectionState
    {
        Disconnected,
        Connecting,
        Connected,
        Error
    }
}
