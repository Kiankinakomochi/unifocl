#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;

namespace UniFocl.EditorBridge.Profiling
{
    internal static partial class DaemonProfilerService
    {
        [UnifoclCommand(
            "profiling.compare",
            "Compare two frame ranges (baseline vs candidate). Returns absolute and percentage " +
            "deltas for CPU, GPU, FPS averages and p95 values.",
            "profiling")]
        public static string Compare(int baselineFrom, int baselineTo, int candidateFrom, int candidateTo)
        {
            if (baselineFrom < 0 || baselineTo < baselineFrom)
                return ErrorJson(ErrorCodes.InvalidFrameRange, $"Invalid baseline range: {baselineFrom}..{baselineTo}");
            if (candidateFrom < 0 || candidateTo < candidateFrom)
                return ErrorJson(ErrorCodes.InvalidFrameRange, $"Invalid candidate range: {candidateFrom}..{candidateTo}");

            var (bCpu, bGpu, bFps) = CollectFrameMetrics(baselineFrom, baselineTo);
            var (cCpu, cGpu, cFps) = CollectFrameMetrics(candidateFrom, candidateTo);

            var bCpuStats = ComputeRangeStats(bCpu);
            var bGpuStats = ComputeRangeStats(bGpu);
            var bFpsStats = ComputeRangeStats(bFps);
            var cCpuStats = ComputeRangeStats(cCpu);
            var cGpuStats = ComputeRangeStats(cGpu);
            var cFpsStats = ComputeRangeStats(cFps);

            return JsonUtility.ToJson(new ProfilerCompareResponse
            {
                baseline = new ProfilerCompareSide
                {
                    from       = baselineFrom,
                    to         = baselineTo,
                    frameCount = bCpu.Count,
                    cpuMs      = bCpuStats,
                    gpuMs      = bGpuStats,
                    fps        = bFpsStats,
                },
                candidate = new ProfilerCompareSide
                {
                    from       = candidateFrom,
                    to         = candidateTo,
                    frameCount = cCpu.Count,
                    cpuMs      = cCpuStats,
                    gpuMs      = cGpuStats,
                    fps        = cFpsStats,
                },
                delta = new ProfilerCompareDelta
                {
                    cpuMs = ComputeDelta(bCpuStats, cCpuStats),
                    gpuMs = ComputeDelta(bGpuStats, cGpuStats),
                    fps   = ComputeDelta(bFpsStats, cFpsStats),
                },
            });
        }

        [UnifoclCommand(
            "profiling.budget_check",
            "Check performance budgets against the current profiler session. " +
            "Pass semicolon-separated expressions like 'p95(cpuMs) < 16.6; avg(fps) > 60'. " +
            "Returns pass/fail for each rule.",
            "profiling")]
        public static string BudgetCheck(string expressions)
        {
            if (string.IsNullOrWhiteSpace(expressions))
                return ErrorJson(ErrorCodes.InvalidFrameRange, "No budget expressions provided.");

            var first = EditorApi.FirstFrameIndex;
            var last  = EditorApi.LastFrameIndex;
            if (last < first)
                return ErrorJson(ErrorCodes.NoFrameData, "No frame data available for budget check.");

            var (cpuList, gpuList, fpsList) = CollectFrameMetrics(first, last);
            var cpuStats = ComputeRangeStats(cpuList);
            var gpuStats = ComputeRangeStats(gpuList);
            var fpsStats = ComputeRangeStats(fpsList);

            var rules = expressions.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            var results = new List<ProfilerBudgetResult>();
            bool allPassed = true;

            // Pattern: func(metric) op value  e.g. "p95(cpuMs) < 16.6"
            var pattern = new Regex(@"^\s*(\w+)\((\w+)\)\s*([<>=!]+)\s*([\d.]+)\s*$");

            foreach (var rule in rules)
            {
                var m = pattern.Match(rule.Trim());
                if (!m.Success)
                {
                    results.Add(new ProfilerBudgetResult
                    {
                        expression = rule.Trim(),
                        passed     = false,
                        message    = "Parse error: expected format 'func(metric) op value'",
                    });
                    allPassed = false;
                    continue;
                }

                var func      = m.Groups[1].Value.ToLowerInvariant();
                var metric    = m.Groups[2].Value.ToLowerInvariant();
                var op        = m.Groups[3].Value;
                var threshold = float.Parse(m.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture);

                var stats = metric switch
                {
                    "cpums" => cpuStats,
                    "gpums" => gpuStats,
                    "fps"   => fpsStats,
                    _       => null,
                };

                if (stats == null || !stats.available)
                {
                    results.Add(new ProfilerBudgetResult
                    {
                        expression = rule.Trim(),
                        passed     = false,
                        message    = $"Unknown or unavailable metric: {metric}",
                    });
                    allPassed = false;
                    continue;
                }

                var actual = func switch
                {
                    "avg" => stats.avg,
                    "min" => stats.min,
                    "max" => stats.max,
                    "p50" => stats.p50,
                    "p95" => stats.p95,
                    _     => float.NaN,
                };

                if (float.IsNaN(actual))
                {
                    results.Add(new ProfilerBudgetResult
                    {
                        expression = rule.Trim(),
                        passed     = false,
                        message    = $"Unknown function: {func}",
                    });
                    allPassed = false;
                    continue;
                }

                bool passed = op switch
                {
                    "<"  => actual <  threshold,
                    "<=" => actual <= threshold,
                    ">"  => actual >  threshold,
                    ">=" => actual >= threshold,
                    "==" => Math.Abs(actual - threshold) < 0.001f,
                    "!=" => Math.Abs(actual - threshold) >= 0.001f,
                    _    => false,
                };

                if (!passed) allPassed = false;

                results.Add(new ProfilerBudgetResult
                {
                    expression  = rule.Trim(),
                    passed      = passed,
                    actualValue = actual,
                    message     = passed ? "OK" : $"FAIL: {func}({metric}) = {actual:F2}, threshold {op} {threshold}",
                });
            }

            return JsonUtility.ToJson(new ProfilerBudgetCheckResponse
            {
                passed  = allPassed,
                results = results,
            });
        }

        [UnifoclCommand(
            "profiling.export_summary",
            "Export a JSON summary of the current profiler session to a file. " +
            "Includes frame range stats for the full captured range.",
            "profiling")]
        public static string ExportSummary(string path)
        {
            try
            {
                var first = EditorApi.FirstFrameIndex;
                var last  = EditorApi.LastFrameIndex;
                if (last < first)
                    return ErrorJson(ErrorCodes.NoFrameData, "No frame data to export.");

                var (cpuList, gpuList, fpsList) = CollectFrameMetrics(first, last);

                var summary = new ProfilerFramesResponse
                {
                    from       = first,
                    to         = last,
                    frameCount = cpuList.Count,
                    cpuMs      = ComputeRangeStats(cpuList),
                    gpuMs      = ComputeRangeStats(gpuList),
                    fps        = ComputeRangeStats(fpsList),
                };

                var json = JsonUtility.ToJson(summary, true);
                var normalized = Path.GetFullPath(path.Trim());
                var dir = Path.GetDirectoryName(normalized);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                File.WriteAllText(normalized, json);

                return JsonUtility.ToJson(new ProfilerExportSummaryResponse
                {
                    ok            = true,
                    message       = "Summary exported",
                    path          = normalized,
                    fileSizeBytes = ProfilerPathUtils.GetFileSize(normalized),
                });
            }
            catch (Exception ex)
            {
                return ErrorJson(ErrorCodes.IoError, $"Failed to export summary: {ex.Message}");
            }
        }

        // ── Compare helpers ─────────────────────────────────────────
        private static (List<float> cpu, List<float> gpu, List<float> fps) CollectFrameMetrics(int from, int to)
        {
            var cpu = new List<float>();
            var gpu = new List<float>();
            var fps = new List<float>();
            for (int f = from; f <= to; f++)
            {
                var view = EditorApi.GetRawFrameDataView(f, 0);
                if (view == null) continue;
                try
                {
                    cpu.Add(view.frameTimeMs);
                    gpu.Add(view.frameGpuTimeMs);
                    fps.Add(view.frameFps);
                }
                finally { view.Dispose(); }
            }
            return (cpu, gpu, fps);
        }

        private static ProfilerDeltaEntry ComputeDelta(ProfilerRangeStats baseline, ProfilerRangeStats candidate)
        {
            return new ProfilerDeltaEntry
            {
                absoluteAvg = candidate.avg - baseline.avg,
                percentAvg  = baseline.avg != 0 ? (candidate.avg - baseline.avg) / baseline.avg * 100f : 0f,
                absoluteP95 = candidate.p95 - baseline.p95,
                percentP95  = baseline.p95 != 0 ? (candidate.p95 - baseline.p95) / baseline.p95 * 100f : 0f,
            };
        }
    }
}
#endif
