#if UNITY_EDITOR
#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace UniFocl.EditorBridge
{
    /// <summary>
    /// Persists compile results to disk so the CLI can observe completion even
    /// when Unity tears down its HTTP daemon during the domain reload that
    /// follows a successful compile.
    ///
    /// The result file lives at:
    ///   <c>&lt;projectRoot&gt;/Temp/unifocl/compile-results/{requestId}.json</c>
    ///
    /// The CLI polls this file plus Unity's <c>Temp/compiling.lock</c> and
    /// <c>Temp/domainreload.lock</c> markers; once the result file exists and
    /// both lock files are gone for a grace period, the compile is treated
    /// as fully settled.
    /// </summary>
    internal static class CompileResultPersistenceService
    {
        // Match the CLI waiter's max poll budget plus a generous safety margin.
        // Files older than this can never be claimed by an active waiter.
        private static readonly TimeSpan StaleResultThreshold = TimeSpan.FromMinutes(2);

        // Restrict requestId to [a-zA-Z0-9_-] so it cannot encode path
        // separators or traversal sequences when used as a file name.
        private static readonly Regex SafeRequestIdRegex = new(@"^[a-zA-Z0-9_-]+$", RegexOptions.Compiled);

        public static string GetResultDirectory()
        {
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            return Path.Combine(projectRoot, "Temp", "unifocl", "compile-results");
        }

        public static bool IsRequestIdSafe(string? requestId)
        {
            return !string.IsNullOrWhiteSpace(requestId) && SafeRequestIdRegex.IsMatch(requestId!);
        }

        public static string CreateRequestId()
        {
            long ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string token = Guid.NewGuid().ToString("N").Substring(0, 8);
            return $"compile_{ts}_{token}";
        }

        public static void SaveResult(string requestId, CompilePersistedResult result)
        {
            if (!IsRequestIdSafe(requestId))
            {
                Debug.LogWarning($"[unifocl] refusing to save compile result with unsafe requestId: '{requestId}'");
                return;
            }

            string directory = GetResultDirectory();
            try
            {
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[unifocl] could not create compile-results directory: {ex.Message}");
                return;
            }

            string filePath = Path.Combine(directory, $"{requestId}.json");
            string tempPath = filePath + ".tmp";

            try
            {
                string json = JsonUtility.ToJson(result);
                // Write to a temp file then atomically move into place so a
                // racing reader never sees a partially written file.
                File.WriteAllText(tempPath, json, new UTF8Encoding(false));

                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
                File.Move(tempPath, filePath);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[unifocl] failed to write compile result {requestId}: {ex.Message}");
                try
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
                catch
                {
                    // best-effort cleanup
                }
            }
        }

        public static void ClearStaleResults()
        {
            string directory = GetResultDirectory();
            if (!Directory.Exists(directory))
            {
                return;
            }

            DateTime threshold = DateTime.UtcNow - StaleResultThreshold;

            string[] files;
            try
            {
                files = Directory.GetFiles(directory, "*.json");
            }
            catch
            {
                return;
            }

            foreach (string file in files)
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < threshold)
                    {
                        File.Delete(file);
                    }
                }
                catch
                {
                    // best-effort
                }
            }
        }
    }

    [Serializable]
    internal sealed class CompilePersistedResult
    {
        public string requestId = string.Empty;
        public string outcome = string.Empty;          // "success" | "errors" | "indeterminate" | "rejected"
        public bool success;
        public int errorCount;
        public int warningCount;
        public string startedAtUtc = string.Empty;
        public string finishedAtUtc = string.Empty;
        public string message = string.Empty;
        public CompilePersistedIssue[] errors = Array.Empty<CompilePersistedIssue>();
        public CompilePersistedIssue[] warnings = Array.Empty<CompilePersistedIssue>();
        public bool forceRecompile;
    }

    [Serializable]
    internal sealed class CompilePersistedIssue
    {
        public string message = string.Empty;
        public string file = string.Empty;
        public int line;
        public string assembly = string.Empty;
    }
}
#endif
