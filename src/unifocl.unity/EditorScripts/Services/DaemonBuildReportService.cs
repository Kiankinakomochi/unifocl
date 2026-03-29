#if UNITY_EDITOR
#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace UniFocl.EditorBridge
{
    internal static class DaemonBuildReportService
    {
        private const string ReportPath = "Library/unifocl-last-build-report.json";

        public static void CaptureReport(BuildReport report)
        {
            var files = report.GetFiles()?.Select(f => new StoredBuildFile
            {
                path = f.path,
                role = f.role,
                size = (long)f.size
            }).ToArray() ?? Array.Empty<StoredBuildFile>();

            var steps = report.steps?.Select(s => new StoredBuildStep
            {
                name = s.name,
                depth = s.depth,
                durationMs = (float)s.duration.TotalMilliseconds,
                messages = s.messages?.Select(m => new StoredBuildMessage
                {
                    type = m.type.ToString(),
                    content = m.content
                }).ToArray() ?? Array.Empty<StoredBuildMessage>()
            }).ToArray() ?? Array.Empty<StoredBuildStep>();

            var stored = new StoredBuildReport
            {
                buildTarget = report.summary.platform.ToString(),
                outputPath = report.summary.outputPath,
                result = report.summary.result.ToString(),
                totalSize = (long)report.summary.totalSize,
                buildStartedAt = report.summary.buildStartedAt.ToString("O"),
                buildEndedAt = report.summary.buildEndedAt.ToString("O"),
                totalErrors = report.summary.totalErrors,
                totalWarnings = report.summary.totalWarnings,
                files = files,
                steps = steps
            };

            try
            {
                var json = JsonUtility.ToJson(stored, prettyPrint: false);
                File.WriteAllText(ReportPath, json);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[unifocl] failed to capture build report: {ex.Message}");
            }
        }

        public static string ExecuteBuildArtifactMetadata()
        {
            if (!File.Exists(ReportPath))
            {
                return JsonUtility.ToJson(new ProjectCommandResponse
                {
                    ok = false,
                    message = "no build report found — run a build first",
                    kind = "build-artifact-metadata"
                });
            }

            StoredBuildReport? stored;
            try
            {
                stored = JsonUtility.FromJson<StoredBuildReport>(File.ReadAllText(ReportPath));
            }
            catch (Exception ex)
            {
                return JsonUtility.ToJson(new ProjectCommandResponse
                {
                    ok = false,
                    message = $"failed to read build report: {ex.Message}",
                    kind = "build-artifact-metadata"
                });
            }

            if (stored == null)
            {
                return JsonUtility.ToJson(new ProjectCommandResponse
                {
                    ok = false,
                    message = "build report is empty",
                    kind = "build-artifact-metadata"
                });
            }

            var buildDuration = "";
            if (DateTime.TryParse(stored.buildStartedAt, out var start) && DateTime.TryParse(stored.buildEndedAt, out var end))
                buildDuration = (end - start).ToString(@"hh\:mm\:ss");

            var payload = new ArtifactMetadataPayload
            {
                buildTarget = stored.buildTarget,
                result = stored.result,
                outputPath = stored.outputPath,
                totalSize = stored.totalSize,
                buildTime = buildDuration,
                fileCount = stored.files?.Length ?? 0,
                files = stored.files?.Select(f => f.path).ToArray() ?? Array.Empty<string>()
            };

            return JsonUtility.ToJson(new ProjectCommandResponse
            {
                ok = true,
                message = $"build report loaded: {stored.result}",
                kind = "build-artifact-metadata",
                content = JsonUtility.ToJson(payload)
            });
        }

        public static string ExecuteBuildFailureClassify()
        {
            if (!File.Exists(ReportPath))
            {
                return JsonUtility.ToJson(new ProjectCommandResponse
                {
                    ok = false,
                    message = "no build report found — run a build first",
                    kind = "build-failure-classify"
                });
            }

            StoredBuildReport? stored;
            try
            {
                stored = JsonUtility.FromJson<StoredBuildReport>(File.ReadAllText(ReportPath));
            }
            catch (Exception ex)
            {
                return JsonUtility.ToJson(new ProjectCommandResponse
                {
                    ok = false,
                    message = $"failed to read build report: {ex.Message}",
                    kind = "build-failure-classify"
                });
            }

            if (stored == null)
            {
                return JsonUtility.ToJson(new ProjectCommandResponse
                {
                    ok = false,
                    message = "build report is empty",
                    kind = "build-failure-classify"
                });
            }

            var failures = new System.Collections.Generic.List<ClassifiedFailure>();

            foreach (var step in stored.steps ?? Array.Empty<StoredBuildStep>())
            {
                foreach (var msg in step.messages ?? Array.Empty<StoredBuildMessage>())
                {
                    if (string.IsNullOrWhiteSpace(msg.content))
                        continue;

                    var kind = ClassifyMessage(msg.content);
                    if (kind != null)
                    {
                        failures.Add(new ClassifiedFailure
                        {
                            kind = kind,
                            stepName = step.name,
                            message = msg.content
                        });
                    }
                }
            }

            var payload = new FailureClassifyPayload
            {
                hasFailures = failures.Count > 0,
                buildResult = stored.result,
                totalErrors = stored.totalErrors,
                failures = failures.ToArray()
            };

            return JsonUtility.ToJson(new ProjectCommandResponse
            {
                ok = true,
                message = $"classified {failures.Count} failure(s)",
                kind = "build-failure-classify",
                content = JsonUtility.ToJson(payload)
            });
        }

        private static string? ClassifyMessage(string content)
        {
            if (Regex.IsMatch(content, @"CS\d{4}"))
                return "CompileError";
            if (content.IndexOf("linker", StringComparison.OrdinalIgnoreCase) >= 0
                || content.IndexOf("stripping", StringComparison.OrdinalIgnoreCase) >= 0)
                return "LinkerError";
            if (content.IndexOf("Missing", StringComparison.OrdinalIgnoreCase) >= 0
                && (content.IndexOf("asset", StringComparison.OrdinalIgnoreCase) >= 0
                    || content.IndexOf("prefab", StringComparison.OrdinalIgnoreCase) >= 0))
                return "MissingAsset";
            if (content.IndexOf("timed out", StringComparison.OrdinalIgnoreCase) >= 0
                || content.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Timeout";
            if (content.IndexOf(".cs(", StringComparison.Ordinal) >= 0
                || content.IndexOf("ScriptCompilationFailed", StringComparison.Ordinal) >= 0)
                return "ScriptError";
            return null;
        }

        [Serializable]
        internal sealed class StoredBuildReport
        {
            public string buildTarget = "";
            public string outputPath = "";
            public string result = "";
            public long totalSize;
            public string buildStartedAt = "";
            public string buildEndedAt = "";
            public int totalErrors;
            public int totalWarnings;
            public StoredBuildFile[] files = Array.Empty<StoredBuildFile>();
            public StoredBuildStep[] steps = Array.Empty<StoredBuildStep>();
        }

        [Serializable]
        internal sealed class StoredBuildFile
        {
            public string path = "";
            public string role = "";
            public long size;
        }

        [Serializable]
        internal sealed class StoredBuildStep
        {
            public string name = "";
            public int depth;
            public float durationMs;
            public StoredBuildMessage[] messages = Array.Empty<StoredBuildMessage>();
        }

        [Serializable]
        internal sealed class StoredBuildMessage
        {
            public string type = "";
            public string content = "";
        }

        [Serializable]
        private sealed class ArtifactMetadataPayload
        {
            public string buildTarget = "";
            public string result = "";
            public string outputPath = "";
            public long totalSize;
            public string buildTime = "";
            public int fileCount;
            public string[] files = Array.Empty<string>();
        }

        [Serializable]
        private sealed class FailureClassifyPayload
        {
            public bool hasFailures;
            public string buildResult = "";
            public int totalErrors;
            public ClassifiedFailure[] failures = Array.Empty<ClassifiedFailure>();
        }

        [Serializable]
        private sealed class ClassifiedFailure
        {
            public string kind = "";
            public string stepName = "";
            public string message = "";
        }
    }

    internal sealed class BuildReportCapture : IPostprocessBuildWithReport
    {
        public int callbackOrder => 0;

        public void OnPostprocessBuild(BuildReport report)
        {
            DaemonBuildReportService.CaptureReport(report);
        }
    }
}
#endif
