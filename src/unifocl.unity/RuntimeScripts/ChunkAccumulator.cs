using System;
using System.Collections.Generic;
using System.Text;

namespace UniFocl.Runtime
{
    /// <summary>
    /// Reassembles chunked <see cref="RuntimeEnvelope"/> messages into complete payloads.
    /// Thread-safe: accumulate from any callback thread, consume from any thread.
    /// </summary>
    public sealed class ChunkAccumulator
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, string[]> _pending = new();

        /// <summary>
        /// Feed a single envelope. Returns the reassembled payload when all chunks for
        /// the correlation ID have arrived, or null if chunks are still missing.
        /// </summary>
        public string TryAccumulate(RuntimeEnvelope envelope)
        {
            if (!envelope.isChunked)
            {
                return envelope.payload;
            }

            lock (_lock)
            {
                if (!_pending.TryGetValue(envelope.correlationId, out var chunks))
                {
                    chunks = new string[envelope.totalChunks];
                    _pending[envelope.correlationId] = chunks;
                }

                if (envelope.chunkIndex < 0 || envelope.chunkIndex >= chunks.Length)
                {
                    return null;
                }

                chunks[envelope.chunkIndex] = envelope.payload;

                for (var i = 0; i < chunks.Length; i++)
                {
                    if (chunks[i] == null) return null;
                }

                _pending.Remove(envelope.correlationId);
                var sb = new StringBuilder();
                foreach (var c in chunks)
                {
                    sb.Append(c);
                }

                return sb.ToString();
            }
        }

        /// <summary>Remove any stale pending entries for a given correlation ID.</summary>
        public void Discard(string correlationId)
        {
            lock (_lock)
            {
                _pending.Remove(correlationId);
            }
        }
    }
}
