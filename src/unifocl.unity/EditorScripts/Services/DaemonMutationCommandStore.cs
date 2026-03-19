#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace UniFocl.EditorBridge
{
    internal static class DaemonMutationCommandStore
    {
        private static readonly object IoSync = new();

        public static DurableMutationCommandEnvelope CreateEnvelope(ProjectCommandRequest request)
        {
            var requestId = NormalizeRequestId(request.requestId);
            return new DurableMutationCommandEnvelope
            {
                requestId = requestId,
                action = request.action ?? string.Empty,
                requestPayload = JsonUtility.ToJson(request),
                state = DurableMutationState.Queued,
                message = "queued",
                createdAtUtc = DateTime.UtcNow.ToString("O"),
                updatedAtUtc = DateTime.UtcNow.ToString("O"),
                checkpoints = Array.Empty<string>()
            };
        }

        public static DurableMutationCommandEnvelope UpsertQueued(ProjectCommandRequest request, out bool duplicated)
        {
            lock (IoSync)
            {
                var requestId = NormalizeRequestId(request.requestId);
                if (TryReadEnvelopeCore(requestId, out var existing))
                {
                    duplicated = true;
                    return existing;
                }

                var envelope = CreateEnvelope(request);
                WriteEnvelopeCore(envelope);
                duplicated = false;
                return envelope;
            }
        }

        public static bool TryReadEnvelope(string requestId, out DurableMutationCommandEnvelope envelope)
        {
            lock (IoSync)
            {
                return TryReadEnvelopeCore(requestId, out envelope);
            }
        }

        public static bool TryUpdateEnvelope(string requestId, Func<DurableMutationCommandEnvelope, DurableMutationCommandEnvelope> updater, out DurableMutationCommandEnvelope updated)
        {
            lock (IoSync)
            {
                if (!TryReadEnvelopeCore(requestId, out var current))
                {
                    updated = new DurableMutationCommandEnvelope();
                    return false;
                }

                updated = updater(current);
                updated.requestId = NormalizeRequestId(updated.requestId);
                if (string.IsNullOrWhiteSpace(updated.requestId))
                {
                    updated.requestId = current.requestId;
                }

                updated.updatedAtUtc = DateTime.UtcNow.ToString("O");
                WriteEnvelopeCore(updated);
                return true;
            }
        }

        public static void WriteEnvelope(DurableMutationCommandEnvelope envelope)
        {
            lock (IoSync)
            {
                envelope.requestId = NormalizeRequestId(envelope.requestId);
                envelope.updatedAtUtc = DateTime.UtcNow.ToString("O");
                WriteEnvelopeCore(envelope);
            }
        }

        public static List<DurableMutationCommandEnvelope> ReadAll()
        {
            lock (IoSync)
            {
                var root = GetStorageRoot();
                if (!Directory.Exists(root))
                {
                    return new List<DurableMutationCommandEnvelope>();
                }

                var entries = new List<DurableMutationCommandEnvelope>();
                foreach (var file in Directory.GetFiles(root, "*.json", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        var json = File.ReadAllText(file);
                        var parsed = JsonUtility.FromJson<DurableMutationCommandEnvelope>(json);
                        if (parsed is null || string.IsNullOrWhiteSpace(parsed.requestId))
                        {
                            continue;
                        }

                        entries.Add(parsed);
                    }
                    catch
                    {
                    }
                }

                return entries;
            }
        }

        public static string NormalizeRequestId(string? requestId)
        {
            if (string.IsNullOrWhiteSpace(requestId))
            {
                return Guid.NewGuid().ToString("N");
            }

            var sanitized = new string(requestId
                .Trim()
                .Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_')
                .Take(96)
                .ToArray());
            return string.IsNullOrWhiteSpace(sanitized) ? Guid.NewGuid().ToString("N") : sanitized;
        }

        private static bool TryReadEnvelopeCore(string requestId, out DurableMutationCommandEnvelope envelope)
        {
            envelope = new DurableMutationCommandEnvelope();
            var path = ResolvePath(requestId);
            if (!File.Exists(path))
            {
                return false;
            }

            try
            {
                var json = File.ReadAllText(path);
                var parsed = JsonUtility.FromJson<DurableMutationCommandEnvelope>(json);
                if (parsed is null || string.IsNullOrWhiteSpace(parsed.requestId))
                {
                    return false;
                }

                envelope = parsed;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void WriteEnvelopeCore(DurableMutationCommandEnvelope envelope)
        {
            var root = GetStorageRoot();
            Directory.CreateDirectory(root);
            var path = ResolvePath(envelope.requestId);
            var tempPath = path + ".tmp";
            var json = JsonUtility.ToJson(envelope, true);
            File.WriteAllText(tempPath, json);
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            File.Move(tempPath, path);
        }

        private static string GetStorageRoot()
        {
            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName
                              ?? throw new InvalidOperationException("failed to resolve Unity project root");
            return Path.Combine(projectRoot, ".unifocl", "command-state");
        }

        private static string ResolvePath(string requestId)
        {
            var safe = NormalizeRequestId(requestId);
            return Path.Combine(GetStorageRoot(), $"{safe}.json");
        }
    }

    internal static class DurableMutationState
    {
        public const string Queued = "queued";
        public const string Running = "running";
        public const string Succeeded = "succeeded";
        public const string Failed = "failed";
        public const string Canceled = "canceled";

        public static bool IsTerminal(string state)
        {
            return state.Equals(Succeeded, StringComparison.OrdinalIgnoreCase)
                   || state.Equals(Failed, StringComparison.OrdinalIgnoreCase)
                   || state.Equals(Canceled, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Serializable]
    internal sealed class DurableMutationCommandEnvelope
    {
        public string requestId = string.Empty;
        public string action = string.Empty;
        public string requestPayload = string.Empty;
        public string state = DurableMutationState.Queued;
        public string message = string.Empty;
        public bool success;
        public bool cancelRequested;
        public string responsePayload = string.Empty;
        public string error = string.Empty;
        public string createdAtUtc = string.Empty;
        public string startedAtUtc = string.Empty;
        public string updatedAtUtc = string.Empty;
        public string finishedAtUtc = string.Empty;
        public string[] checkpoints = Array.Empty<string>();
    }
}
#endif
