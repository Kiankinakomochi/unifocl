#if UNITY_EDITOR
using System;
using UnityEngine;

namespace UniFocl.EditorBridge.Profiling
{
    internal static partial class DaemonProfilerService
    {
        [UnifoclCommand(
            "profiling.gpu_capture_begin",
            "Begin an external GPU capture (RenderDoc/PIX). Requires a supported GPU debugger " +
            "to be attached. Only available on Unity 2020.1+ with experimental rendering.",
            "profiling")]
        public static string GpuCaptureBegin()
        {
#if UNITY_2020_1_OR_NEWER
            try
            {
                if (!UnityEngine.Experimental.Rendering.ExternalGPUProfiler.IsAttached())
                    return ErrorJson(ErrorCodes.Unsupported,
                        "No external GPU profiler attached (RenderDoc/PIX not connected).");

                UnityEngine.Experimental.Rendering.ExternalGPUProfiler.BeginGPUCapture();

                return JsonUtility.ToJson(new ProfilerGpuCaptureResponse
                {
                    ok         = true,
                    message    = "GPU capture started",
                    isAttached = true,
                });
            }
            catch (Exception ex)
            {
                return ErrorJson(ErrorCodes.Unsupported,
                    $"GPU capture failed: {ex.Message}");
            }
#else
            return ErrorJson(ErrorCodes.Unsupported,
                "External GPU capture requires Unity 2020.1+");
#endif
        }

        [UnifoclCommand(
            "profiling.gpu_capture_end",
            "End an external GPU capture (RenderDoc/PIX). The captured data is handled " +
            "by the attached GPU debugger.",
            "profiling")]
        public static string GpuCaptureEnd()
        {
#if UNITY_2020_1_OR_NEWER
            try
            {
                if (!UnityEngine.Experimental.Rendering.ExternalGPUProfiler.IsAttached())
                    return ErrorJson(ErrorCodes.Unsupported,
                        "No external GPU profiler attached.");

                UnityEngine.Experimental.Rendering.ExternalGPUProfiler.EndGPUCapture();

                return JsonUtility.ToJson(new ProfilerGpuCaptureResponse
                {
                    ok         = true,
                    message    = "GPU capture ended",
                    isAttached = true,
                });
            }
            catch (Exception ex)
            {
                return ErrorJson(ErrorCodes.Unsupported,
                    $"GPU capture end failed: {ex.Message}");
            }
#else
            return ErrorJson(ErrorCodes.Unsupported,
                "External GPU capture requires Unity 2020.1+");
#endif
        }
    }
}
#endif
