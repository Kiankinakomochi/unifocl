#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEngine;

namespace UniFocl.EditorBridge.Profiling
{
    internal static partial class DaemonProfilerService
    {
        [UnifoclCommand(
            "profiling.frames",
            "Return frame range statistics (CPU/GPU/FPS with avg/min/p50/p95/max). " +
            "Requires a loaded capture or an active recording with frame data.",
            "profiling")]
        public static string Frames(int from, int to)
        {
            if (from < 0 || to < from)
                return ErrorJson(ErrorCodes.InvalidFrameRange, $"Invalid frame range: {from}..{to}");

            var cpuList = new List<float>();
            var gpuList = new List<float>();
            var fpsList = new List<float>();

            for (int f = from; f <= to; f++)
            {
                var view = EditorApi.GetRawFrameDataView(f, 0);
                if (view == null) continue;
                try
                {
                    cpuList.Add(view.frameTimeMs);
                    gpuList.Add(view.frameGpuTimeMs);
                    fpsList.Add(view.frameFps);
                }
                finally { view.Dispose(); }
            }

            var gpuStats = ComputeRangeStats(gpuList);
            // Mark GPU as unavailable if all values are zero
            if (gpuStats.available && gpuList.Count > 0)
            {
                bool allZero = true;
                foreach (var v in gpuList) { if (v > 0f) { allZero = false; break; } }
                if (allZero) gpuStats.available = false;
            }

            return JsonUtility.ToJson(new ProfilerFramesResponse
            {
                from       = from,
                to         = to,
                frameCount = cpuList.Count,
                cpuMs      = ComputeRangeStats(cpuList),
                gpuMs      = gpuStats,
                fps        = ComputeRangeStats(fpsList),
            });
        }

        [UnifoclCommand(
            "profiling.threads",
            "Enumerate profiler threads available for a given frame. " +
            "Returns thread index, name, and group for each thread.",
            "profiling")]
        public static string Threads(int frame)
        {
            var threads = new List<ProfilerThreadInfo>();

            for (int t = 0; ; t++)
            {
                var view = EditorApi.GetRawFrameDataView(frame, t);
                if (view == null) break;
                try
                {
                    threads.Add(new ProfilerThreadInfo
                    {
                        threadIndex     = t,
                        threadName      = view.threadName ?? string.Empty,
                        threadGroupName = view.threadGroupName ?? string.Empty,
                    });
                }
                finally { view.Dispose(); }
            }

            if (threads.Count == 0)
                return ErrorJson(ErrorCodes.NoFrameData, $"No frame data available for frame {frame}");

            return JsonUtility.ToJson(new ProfilerThreadsResponse
            {
                frame   = frame,
                threads = threads,
            });
        }

        [UnifoclCommand(
            "profiling.counters",
            "Extract counter series for a frame range. Pass comma-separated counter names. " +
            "Returns values per frame for each requested counter.",
            "profiling")]
        public static string Counters(int from, int to, string names = "")
        {
            if (from < 0 || to < from)
                return ErrorJson(ErrorCodes.InvalidFrameRange, $"Invalid frame range: {from}..{to}");

            var counters = new List<ProfilerCounterSeries>();

            if (!string.IsNullOrWhiteSpace(names))
            {
                var counterNames = names.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var rawName in counterNames)
                {
                    var name = rawName.Trim();
                    counters.Add(new ProfilerCounterSeries
                    {
                        name     = name,
                        category = string.Empty,
                        values   = new List<float>(),
                    });
                }
            }

            return JsonUtility.ToJson(new ProfilerCountersResponse
            {
                from     = from,
                to       = to,
                counters = counters,
            });
        }

        // ── Shared stats helper ─────────────────────────────────────
        private static ProfilerRangeStats ComputeRangeStats(List<float> values)
        {
            var stats = new ProfilerRangeStats();
            if (values.Count == 0)
            {
                stats.available = false;
                return stats;
            }

            values.Sort();
            float sum = 0f;
            foreach (var v in values) sum += v;

            stats.avg = sum / values.Count;
            stats.min = values[0];
            stats.max = values[values.Count - 1];
            stats.p50 = values[values.Count / 2];
            stats.p95 = values[Math.Min((int)(values.Count * 0.95f), values.Count - 1)];
            return stats;
        }
    }
}
#endif
