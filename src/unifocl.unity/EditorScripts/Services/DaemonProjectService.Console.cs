#if UNITY_EDITOR
#nullable enable
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace UniFocl.EditorBridge
{
    internal static partial class DaemonProjectService
    {
        // ── console-dump ───────────────────────────────────────────
        private static string ExecuteConsoleDump(ProjectCommandRequest request)
        {
            try
            {
                var payload = JsonUtility.FromJson<ConsoleDumpPayload>(request.content ?? "{}");
                var limit = payload.limit > 0 ? payload.limit : 100;
                var typeFilter = string.IsNullOrWhiteSpace(payload.type) ? null : payload.type.ToLowerInvariant();

                var logEntriesType = Type.GetType("UnityEditor.LogEntries, UnityEditor");
                if (logEntriesType is null)
                {
                    return JsonUtility.ToJson(new ProjectCommandResponse
                    {
                        ok = false,
                        message = "LogEntries API unavailable"
                    });
                }

                var startGettingEntries = logEntriesType.GetMethod("StartGettingEntries", BindingFlags.Public | BindingFlags.Static);
                var getCountMethod = logEntriesType.GetMethod("GetCount", BindingFlags.Public | BindingFlags.Static);
                var getEntryInternal = logEntriesType.GetMethod("GetEntryInternal", BindingFlags.Public | BindingFlags.Static);
                var endGettingEntries = logEntriesType.GetMethod("EndGettingEntries", BindingFlags.Public | BindingFlags.Static);

                if (startGettingEntries is null || getCountMethod is null || endGettingEntries is null)
                {
                    return JsonUtility.ToJson(new ProjectCommandResponse
                    {
                        ok = false,
                        message = "LogEntries entry enumeration API not found"
                    });
                }

                startGettingEntries.Invoke(null, null);
                var totalCount = (int)getCountMethod.Invoke(null, null)!;

                var entries = new List<ConsoleLogEntry>();

                var logEntryType = Type.GetType("UnityEditor.LogEntry, UnityEditor");
                if (logEntryType is not null && getEntryInternal is not null)
                {
                    var logEntry = Activator.CreateInstance(logEntryType);
                    var messageField = logEntryType.GetField("message", BindingFlags.Public | BindingFlags.Instance);
                    var modeField = logEntryType.GetField("mode", BindingFlags.Public | BindingFlags.Instance);

                    for (int i = 0; i < totalCount && entries.Count < limit; i++)
                    {
                        getEntryInternal.Invoke(null, new object[] { i, logEntry! });
                        var msg = messageField?.GetValue(logEntry) as string ?? "";
                        var mode = (int)(modeField?.GetValue(logEntry) ?? 0);

                        var entryType = ClassifyLogMode(mode);
                        if (typeFilter is not null && !entryType.Equals(typeFilter, StringComparison.OrdinalIgnoreCase))
                            continue;

                        entries.Add(new ConsoleLogEntry
                        {
                            index = i,
                            type = entryType,
                            message = msg
                        });
                    }
                }

                endGettingEntries.Invoke(null, null);

                var entriesJson = JsonUtility.ToJson(new ConsoleLogEntryList { entries = entries });

                return JsonUtility.ToJson(new ProjectCommandResponse
                {
                    ok = true,
                    message = $"{entries.Count} log entries (of {totalCount} total)",
                    kind = "console-dump",
                    content = entriesJson
                });
            }
            catch (Exception ex)
            {
                return JsonUtility.ToJson(new ProjectCommandResponse
                {
                    ok = false,
                    message = $"console dump failed: {ex.Message}"
                });
            }
        }

        // ── console-tail ───────────────────────────────────────────
        private static string ExecuteConsoleTail(ProjectCommandRequest request)
        {
            try
            {
                // Tail returns the most recent log entries (last 50 by default)
                // --follow is a TUI hint; for the daemon endpoint we return a snapshot
                var logEntriesType = Type.GetType("UnityEditor.LogEntries, UnityEditor");
                if (logEntriesType is null)
                {
                    return JsonUtility.ToJson(new ProjectCommandResponse
                    {
                        ok = false,
                        message = "LogEntries API unavailable"
                    });
                }

                var startGettingEntries = logEntriesType.GetMethod("StartGettingEntries", BindingFlags.Public | BindingFlags.Static);
                var getCountMethod = logEntriesType.GetMethod("GetCount", BindingFlags.Public | BindingFlags.Static);
                var getEntryInternal = logEntriesType.GetMethod("GetEntryInternal", BindingFlags.Public | BindingFlags.Static);
                var endGettingEntries = logEntriesType.GetMethod("EndGettingEntries", BindingFlags.Public | BindingFlags.Static);

                if (startGettingEntries is null || getCountMethod is null || endGettingEntries is null)
                {
                    return JsonUtility.ToJson(new ProjectCommandResponse
                    {
                        ok = false,
                        message = "LogEntries enumeration API not found"
                    });
                }

                startGettingEntries.Invoke(null, null);
                var totalCount = (int)getCountMethod.Invoke(null, null)!;

                const int tailSize = 50;
                var startIdx = Math.Max(0, totalCount - tailSize);
                var entries = new List<ConsoleLogEntry>();

                var logEntryType = Type.GetType("UnityEditor.LogEntry, UnityEditor");
                if (logEntryType is not null && getEntryInternal is not null)
                {
                    var logEntry = Activator.CreateInstance(logEntryType);
                    var messageField = logEntryType.GetField("message", BindingFlags.Public | BindingFlags.Instance);
                    var modeField = logEntryType.GetField("mode", BindingFlags.Public | BindingFlags.Instance);

                    for (int i = startIdx; i < totalCount; i++)
                    {
                        getEntryInternal.Invoke(null, new object[] { i, logEntry! });
                        var msg = messageField?.GetValue(logEntry) as string ?? "";
                        var mode = (int)(modeField?.GetValue(logEntry) ?? 0);

                        entries.Add(new ConsoleLogEntry
                        {
                            index = i,
                            type = ClassifyLogMode(mode),
                            message = msg
                        });
                    }
                }

                endGettingEntries.Invoke(null, null);

                var entriesJson = JsonUtility.ToJson(new ConsoleLogEntryList { entries = entries });

                return JsonUtility.ToJson(new ProjectCommandResponse
                {
                    ok = true,
                    message = $"tail: {entries.Count} entries (from index {startIdx})",
                    kind = "console-tail",
                    content = entriesJson
                });
            }
            catch (Exception ex)
            {
                return JsonUtility.ToJson(new ProjectCommandResponse
                {
                    ok = false,
                    message = $"console tail failed: {ex.Message}"
                });
            }
        }

        private static string ClassifyLogMode(int mode)
        {
            // Unity LogEntry mode flags: bit 0 = error, bit 1 = warning, bit 8 = error
            // Simplified classification
            if ((mode & 0x101) != 0) return "error";
            if ((mode & 0x102) != 0) return "warning";
            return "log";
        }

        [Serializable]
        private sealed class ConsoleDumpPayload
        {
            public string type = string.Empty;
            public int limit;
        }

        [Serializable]
        private sealed class ConsoleLogEntry
        {
            public int index;
            public string type = string.Empty;
            public string message = string.Empty;
        }

        [Serializable]
        private sealed class ConsoleLogEntryList
        {
            public List<ConsoleLogEntry> entries = new();
        }
    }
}
#endif
