#if UNITY_EDITOR
#nullable enable
using System;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;

// The TestRunnerApi work lives in a separate assembly (see the class remarks); let it reach
// these helpers without widening the bridge's public surface. Harmless when that assembly is
// absent, which is exactly what happens when the test framework package is not installed.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("UniFocl.EditorBridge.TestRunner")]

namespace UniFocl.EditorBridge
{
    /// <summary>
    /// Bridge-side entry point for running EditMode tests inside the editor that already holds
    /// the project.
    ///
    /// Unity refuses to open a project a second time, so the subprocess test runner cannot run
    /// while a project is attached — it aborts before any test executes. Driving the Test
    /// Framework from inside the live editor sidesteps the lock entirely.
    ///
    /// <c>UnityEditor.TestRunner</c> is not auto-referenced and is define-constrained, so this
    /// assembly cannot reference it. The actual API calls live in
    /// <c>UniFocl.EditorBridge.TestRunner</c>, which registers its handlers here at load. When
    /// the test framework package is absent that assembly is skipped entirely and the handlers
    /// stay null, which this class reports as a clean error.
    ///
    /// Results are handed back through disk rather than the response body, because a test run
    /// can trigger a domain reload that tears down the daemon socket and every in-memory
    /// continuation with it. This mirrors the compile-completion handoff.
    /// </summary>
    internal static class DaemonTestRunnerBridge
    {
        /// <summary>
        /// Starts an EditMode run for the given requestId. Returns null when the run was
        /// started, otherwise an error message. Registered by the TestRunner assembly.
        /// </summary>
        public static Func<string, string?>? RunHandler;

        /// <summary>
        /// Starts an EditMode test-list retrieval for the given requestId. Returns null when
        /// started, otherwise an error message. Registered by the TestRunner assembly.
        /// </summary>
        public static Func<string, string?>? ListHandler;

        public const string RunKind = "run";
        public const string ListKind = "list";

        private const string MissingFrameworkMessage =
            "the Unity Test Framework package (com.unity.test-framework) is not installed in this "
            + "project, so tests cannot be run from the editor";

        // Restrict requestId to [a-zA-Z0-9_-] so it cannot encode path separators or traversal
        // sequences when used as a file name.
        private static readonly Regex SafeRequestIdRegex = new("^[a-zA-Z0-9_-]+$", RegexOptions.Compiled);

        // Comfortably longer than any CLI poll budget, so a marker this old has no waiter left.
        private static readonly TimeSpan StaleMarkerThreshold = TimeSpan.FromHours(6);

        public static string ProjectRoot => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        /// <summary>Completion markers the CLI polls for.</summary>
        public static string GetResultDirectory() => Path.Combine(ProjectRoot, "Temp", "unifocl", "test-results");

        /// <summary>NUnit XML lands here, matching the subprocess runner's artifact layout.</summary>
        public static string GetArtifactsDirectory() => Path.Combine(ProjectRoot, "Logs", "unifocl-test");

        public static string GetResultsXmlPath() => Path.Combine(GetArtifactsDirectory(), "test-results-editmode.xml");

        public static string GetResultFilePath(string requestId) =>
            Path.Combine(GetResultDirectory(), requestId + ".json");

        public static bool IsRequestIdSafe(string? requestId) =>
            !string.IsNullOrWhiteSpace(requestId) && SafeRequestIdRegex.IsMatch(requestId!);

        // ── Daemon command handlers ──────────────────────────────────────────────

        public static string ExecuteTestRun(ProjectCommandRequest request)
        {
            string requestId = ResolveRequestId(request, "testrun");

            if (RunHandler is null)
            {
                return Fail(MissingFrameworkMessage);
            }

            try
            {
                ClearPrevious(requestId, clearXml: true);
                string? error = RunHandler(requestId);
                if (!string.IsNullOrEmpty(error))
                {
                    return Fail(error!);
                }

                return Accepted(requestId, RunKind, "EditMode test run started");
            }
            catch (Exception ex)
            {
                return Fail($"failed to start EditMode test run: {ex.GetType().Name}: {ex.Message}");
            }
        }

        public static string ExecuteTestList(ProjectCommandRequest request)
        {
            string requestId = ResolveRequestId(request, "testlist");

            if (ListHandler is null)
            {
                return Fail(MissingFrameworkMessage);
            }

            try
            {
                ClearPrevious(requestId, clearXml: false);
                string? error = ListHandler(requestId);
                if (!string.IsNullOrEmpty(error))
                {
                    return Fail(error!);
                }

                return Accepted(requestId, ListKind, "EditMode test list requested");
            }
            catch (Exception ex)
            {
                return Fail($"failed to list EditMode tests: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // ── Result handoff (called by the TestRunner assembly) ───────────────────

        public static void SaveResult(TestJobResult result)
        {
            if (result is null || !IsRequestIdSafe(result.requestId))
            {
                Debug.LogWarning("[unifocl] refusing to save a test result with an unsafe requestId");
                return;
            }

            try
            {
                string directory = GetResultDirectory();
                Directory.CreateDirectory(directory);

                // Write beside the target then move, so a poller never observes a partial file.
                string finalPath = GetResultFilePath(result.requestId);
                string tempPath = finalPath + ".tmp";
                File.WriteAllText(tempPath, JsonUtility.ToJson(result));
                if (File.Exists(finalPath))
                {
                    File.Delete(finalPath);
                }

                File.Move(tempPath, finalPath);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[unifocl] failed to persist test result: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // ── Helpers ─────────────────────────────────────────────────────────────

        private static string ResolveRequestId(ProjectCommandRequest request, string prefix)
        {
            string? candidate = request?.requestId;
            if (IsRequestIdSafe(candidate))
            {
                return candidate!;
            }

            long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string token = Guid.NewGuid().ToString("N").Substring(0, 8);
            return $"{prefix}_{timestamp}_{token}";
        }

        /// <summary>
        /// Removes the artifacts of an earlier run so a poller cannot mistake them for this
        /// job's output, and so a run that dies before finishing leaves no misleading XML.
        /// </summary>
        private static void ClearPrevious(string requestId, bool clearXml)
        {
            TryDelete(GetResultFilePath(requestId));
            if (clearXml)
            {
                TryDelete(GetResultsXmlPath());
            }

            SweepStaleMarkers();
        }

        /// <summary>
        /// Request ids are unique, so a completed marker is never overwritten and they would
        /// otherwise accumulate one file per job until Unity next clears Temp/. Nothing can still
        /// be waiting on a marker older than the threshold.
        /// </summary>
        private static void SweepStaleMarkers()
        {
            try
            {
                string directory = GetResultDirectory();
                if (!Directory.Exists(directory))
                {
                    return;
                }

                DateTime cutoff = DateTime.UtcNow - StaleMarkerThreshold;
                foreach (string path in Directory.GetFiles(directory, "*.json"))
                {
                    if (File.GetLastWriteTimeUtc(path) < cutoff)
                    {
                        TryDelete(path);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[unifocl] could not sweep stale test markers: {ex.Message}");
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[unifocl] could not remove stale test artifact '{path}': {ex.Message}");
            }
        }

        private static string Accepted(string requestId, string kind, string message)
        {
            var accepted = new TestJobAccepted
            {
                requestId = requestId,
                kind = kind,
                resultPath = GetResultFilePath(requestId),
                artifactsPath = GetArtifactsDirectory(),
            };

            return JsonUtility.ToJson(new ProjectCommandResponse
            {
                ok = true,
                message = message,
                kind = "test",
                content = JsonUtility.ToJson(accepted),
            });
        }

        private static string Fail(string message) =>
            JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = message, kind = "test" });
    }

    /// <summary>Payload returned when a test job is accepted; the CLI polls <c>resultPath</c>.</summary>
    [Serializable]
    internal sealed class TestJobAccepted
    {
        public string requestId = string.Empty;
        public string kind = string.Empty;
        public string resultPath = string.Empty;
        public string artifactsPath = string.Empty;
    }

    /// <summary>Completion marker written once a run or list finishes.</summary>
    [Serializable]
    internal sealed class TestJobResult
    {
        public string requestId = string.Empty;
        public string kind = string.Empty;
        public bool ok;
        public string message = string.Empty;

        /// <summary>Populated for runs: absolute path of the NUnit XML.</summary>
        public string xmlPath = string.Empty;

        /// <summary>Populated for lists: the discovered tests.</summary>
        public TestListEntry[] tests = Array.Empty<TestListEntry>();
    }

    [Serializable]
    internal sealed class TestListEntry
    {
        public string testName = string.Empty;
        public string assembly = string.Empty;
    }
}
#endif
