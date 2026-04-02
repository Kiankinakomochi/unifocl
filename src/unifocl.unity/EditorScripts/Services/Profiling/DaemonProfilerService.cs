#if UNITY_EDITOR
using System;
using UnityEngine;

namespace UniFocl.EditorBridge.Profiling
{
    /// <summary>
    /// Root partial for the profiling category service.
    /// Owns adapter instances and the <c>profiling.capabilities</c> command.
    /// Additional commands are added via partial files per sprint.
    /// </summary>
    internal static partial class DaemonProfilerService
    {
        private static readonly IProfilerEditorApi  EditorApi  = ProfilerEditorApiAdapter.Instance;
        private static readonly IProfilerRuntimeApi RuntimeApi = ProfilerRuntimeApiAdapter.Instance;

        // ── Error codes ─────────────────────────────────────────────
        internal static class ErrorCodes
        {
            public const string Unsupported       = "PROFILER_UNSUPPORTED";
            public const string InvalidPath       = "PROFILER_INVALID_PATH";
            public const string FileNotFound      = "PROFILER_FILE_NOT_FOUND";
            public const string IoError           = "PROFILER_IO_ERROR";
            public const string NotRecording      = "PROFILER_NOT_RECORDING";
            public const string AlreadyRecording  = "PROFILER_ALREADY_RECORDING";
            public const string SnapshotFailed    = "PROFILER_SNAPSHOT_FAILED";
            public const string InvalidFrameRange = "PROFILER_INVALID_FRAME_RANGE";
            public const string NoFrameData       = "PROFILER_NO_FRAME_DATA";
        }

        // ── Capabilities ────────────────────────────────────────────
        [UnifoclCommand(
            "profiling.capabilities",
            "Return available profiling features for the current editor/runtime context. " +
            "Use this to check what profiling operations are supported before calling them.",
            "profiling")]
        public static string Capabilities()
        {
            var response = new ProfilerCapabilitiesResponse
            {
                editorSessionApis  = true,
                frameDataViews     = true,
                memorySnapshots    = true,
                profilerRecorder   = true,
                frameTimingManager = true,
                binaryLog          = true,
                externalGpuCapture = IsExternalGpuCaptureAvailable(),
            };

            if (!response.externalGpuCapture)
                response.notes.Add("External GPU capture not available on this platform/configuration.");

            return JsonUtility.ToJson(response);
        }

        private static bool IsExternalGpuCaptureAvailable()
        {
            try
            {
#if UNITY_2020_1_OR_NEWER
                return UnityEngine.Experimental.Rendering.ExternalGPUProfiler.IsAttached();
#else
                return false;
#endif
            }
            catch
            {
                return false;
            }
        }

        // ── Helpers ─────────────────────────────────────────────────
        internal static string ErrorJson(string errorCode, string message)
        {
            return JsonUtility.ToJson(new ProfilerErrorResponse
            {
                ok        = false,
                error     = message,
                errorCode = errorCode,
            });
        }
    }
}
#endif
