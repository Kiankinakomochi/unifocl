#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Profiling;
using Unity.Profiling.LowLevel.Unsafe;
using UnityEngine;

namespace UniFocl.EditorBridge.Profiling
{
    internal static partial class DaemonProfilerService
    {
        private static readonly Dictionary<string, ProfilerRecorder> s_liveRecorders = new();

        private static readonly string[] DefaultCounterNames =
        {
            "Main Thread",
            "System Used Memory",
            "GC Used Memory",
            "Gfx Used Memory",
        };

        [UnifoclCommand(
            "profiling.live_start",
            "Start low-overhead live counter recording using ProfilerRecorder. " +
            "Pass comma-separated counter names, or leave empty for defaults. " +
            "Call profiling.live_stop to get results.",
            "profiling")]
        public static string LiveStart(string counters = "", int capacity = 300)
        {
            // Dispose any existing recorders
            DisposeLiveRecorders();

            string[] names;
            if (string.IsNullOrWhiteSpace(counters))
            {
                names = DefaultCounterNames;
            }
            else
            {
                names = counters.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < names.Length; i++)
                    names[i] = names[i].Trim();
            }

            var attached = new List<string>();

            foreach (var name in names)
            {
                try
                {
                    var recorder = ProfilerRecorder.StartNew(ProfilerCategory.Any, name, capacity);
                    if (recorder.Valid)
                    {
                        s_liveRecorders[name] = recorder;
                        attached.Add(name);
                    }
                    else
                    {
                        recorder.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[unifocl] Failed to attach recorder '{name}': {ex.Message}");
                }
            }

            return JsonUtility.ToJson(new ProfilerLiveStartResponse
            {
                ok               = true,
                message          = $"Attached {attached.Count}/{names.Length} recorders",
                attachedCounters = attached,
            });
        }

        [UnifoclCommand(
            "profiling.live_stop",
            "Stop live counter recording and return collected stats and recent samples.",
            "profiling")]
        public static string LiveStop()
        {
            var results = new List<ProfilerLiveCounterResult>();

            foreach (var (name, recorder) in s_liveRecorders)
            {
                var result = new ProfilerLiveCounterResult { name = name };

                try
                {
                    var count = recorder.Count;
                    if (count > 0)
                    {
                        var values = new List<float>();
                        var recent = new List<double>();

                        for (int i = 0; i < count; i++)
                        {
                            var sample = recorder.GetSample(i);
                            var val = (float)sample.Value;
                            values.Add(val);
                            recent.Add(sample.Value);
                        }

                        result.stats = ComputeRangeStats(values);
                        // Keep last 100 samples max for response size
                        if (recent.Count > 100)
                            recent = recent.GetRange(recent.Count - 100, 100);
                        result.recentSamples = recent;
                    }
                    else
                    {
                        result.stats = new ProfilerRangeStats { available = false };
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[unifocl] Error reading recorder '{name}': {ex.Message}");
                    result.stats = new ProfilerRangeStats { available = false };
                }
                finally
                {
                    try { recorder.Dispose(); } catch { /* best effort */ }
                }

                results.Add(result);
            }

            s_liveRecorders.Clear();

            return JsonUtility.ToJson(new ProfilerLiveStopResponse
            {
                ok       = true,
                message  = $"Stopped {results.Count} recorders",
                counters = results,
            });
        }

        [UnifoclCommand(
            "profiling.recorders_list",
            "List available profiler recorders/counters that can be attached with live_start.",
            "profiling")]
        public static string RecordersList()
        {
            var recorders = new List<ProfilerRecorderInfo>();

            try
            {
                var handles = ProfilerRecorderHandle.GetAvailable(Allocator.Temp);
                foreach (var handle in handles)
                {
                    var desc = ProfilerRecorderHandle.GetDescription(handle);
                    recorders.Add(new ProfilerRecorderInfo
                    {
                        name     = desc.Name,
                        category = desc.Category.ToString(),
                        unit     = desc.UnitType.ToString(),
                    });
                }
                handles.Dispose();
            }
            catch (Exception ex)
            {
                return ErrorJson(ErrorCodes.Unsupported,
                    $"Failed to enumerate recorders: {ex.Message}");
            }

            return JsonUtility.ToJson(new ProfilerRecordersListResponse
            {
                recorders = recorders,
            });
        }

        [UnifoclCommand(
            "profiling.frame_timing",
            "Return CPU/GPU frame timing from FrameTimingManager. " +
            "Identifies bottleneck as cpu, gpu, or balanced.",
            "profiling")]
        public static string FrameTiming()
        {
            try
            {
                FrameTimingManager.CaptureFrameTimings();

                var timings = new FrameTiming[1];
                var count = FrameTimingManager.GetLatestTimings((uint)timings.Length, timings);

                if (count == 0)
                {
                    return JsonUtility.ToJson(new ProfilerFrameTimingResponse
                    {
                        available = false,
                        bottleneck = "unknown",
                    });
                }

                var t = timings[0];
                var cpuMs = (float)t.cpuFrameTime;
                var gpuMs = (float)t.gpuFrameTime;

                string bottleneck;
                if (gpuMs <= 0f) bottleneck = "cpu";
                else if (cpuMs > gpuMs * 1.1f) bottleneck = "cpu";
                else if (gpuMs > cpuMs * 1.1f) bottleneck = "gpu";
                else bottleneck = "balanced";

                return JsonUtility.ToJson(new ProfilerFrameTimingResponse
                {
                    available                  = true,
                    cpuFrameTimeMs             = cpuMs,
                    gpuFrameTimeMs             = gpuMs,
                    cpuMainThreadFrameTimeMs   = (float)t.cpuMainThreadFrameTime,
                    cpuRenderThreadFrameTimeMs = (float)t.cpuRenderThreadFrameTime,
                    bottleneck                 = bottleneck,
                });
            }
            catch (Exception ex)
            {
                return ErrorJson(ErrorCodes.Unsupported,
                    $"FrameTimingManager not available: {ex.Message}");
            }
        }

        private static void DisposeLiveRecorders()
        {
            foreach (var recorder in s_liveRecorders.Values)
            {
                try { recorder.Dispose(); } catch { /* best effort */ }
            }
            s_liveRecorders.Clear();
        }
    }
}
#endif
