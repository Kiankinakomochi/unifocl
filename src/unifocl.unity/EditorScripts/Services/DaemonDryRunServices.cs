#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace UniFocl.EditorBridge
{
    internal static class DaemonDryRunContext
    {
        private static readonly AsyncLocal<int> Depth = new();

        public static bool IsActive => Depth.Value > 0;

        public static IDisposable Enter()
        {
            Depth.Value = Depth.Value + 1;
            return new ExitScope();
        }

        private sealed class ExitScope : IDisposable
        {
            private bool _disposed;

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                var current = Depth.Value;
                Depth.Value = current > 0 ? current - 1 : 0;
                _disposed = true;
            }
        }
    }

    internal static class DaemonDryRunDiffService
    {
        public static string BuildJsonDiffPayload(string summary, string beforeJson, string afterJson)
        {
            var lines = BuildUnifiedDiffLines(beforeJson, afterJson);
            var payload = new MutationDryRunDiffPayload
            {
                summary = summary,
                format = "unified",
                before = beforeJson,
                after = afterJson,
                lines = lines.ToArray(),
                changes = Array.Empty<MutationPathChange>()
            };
            return JsonUtility.ToJson(payload);
        }

        public static string BuildFileDiffPayload(string summary, IEnumerable<MutationPathChange> changes)
        {
            var buffered = changes.ToList();
            var lines = new List<string>
            {
                "--- before",
                "+++ after"
            };
            foreach (var change in buffered)
            {
                var path = change.path ?? string.Empty;
                var nextPath = string.IsNullOrWhiteSpace(change.nextPath) ? "-" : change.nextPath!;
                var metaPath = string.IsNullOrWhiteSpace(change.metaPath) ? "-" : change.metaPath!;
                lines.Add($"- {change.action}: {path}");
                lines.Add($"+ {change.action}: {nextPath}");
                lines.Add($"~ meta: {metaPath}");
            }

            var payload = new MutationDryRunDiffPayload
            {
                summary = summary,
                format = "unified",
                before = string.Empty,
                after = string.Empty,
                lines = lines.ToArray(),
                changes = buffered.ToArray()
            };
            return JsonUtility.ToJson(payload);
        }

        private static List<string> BuildUnifiedDiffLines(string beforeJson, string afterJson)
        {
            var before = SplitLines(beforeJson);
            var after = SplitLines(afterJson);
            var lines = new List<string>
            {
                "--- before",
                "+++ after"
            };

            var max = Math.Max(before.Length, after.Length);
            for (var i = 0; i < max; i++)
            {
                var hasBefore = i < before.Length;
                var hasAfter = i < after.Length;
                if (hasBefore && hasAfter)
                {
                    if (string.Equals(before[i], after[i], StringComparison.Ordinal))
                    {
                        lines.Add($" {before[i]}");
                    }
                    else
                    {
                        lines.Add($"-{before[i]}");
                        lines.Add($"+{after[i]}");
                    }

                    continue;
                }

                if (hasBefore)
                {
                    lines.Add($"-{before[i]}");
                }
                else if (hasAfter)
                {
                    lines.Add($"+{after[i]}");
                }
            }

            return lines;
        }

        private static string[] SplitLines(string value)
        {
            return (value ?? string.Empty)
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .Split('\n', StringSplitOptions.None);
        }

        public static string SnapshotObject(UnityEngine.Object target)
        {
            return target is null ? "{}" : EditorJsonUtility.ToJson(target, prettyPrint: true);
        }
    }
}
#endif
