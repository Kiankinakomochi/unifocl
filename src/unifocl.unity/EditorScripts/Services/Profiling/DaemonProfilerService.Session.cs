#if UNITY_EDITOR
using UnityEngine;

namespace UniFocl.EditorBridge.Profiling
{
    internal static partial class DaemonProfilerService
    {
        [UnifoclCommand(
            "profiling.inspect",
            "Return current profiler state: enabled, deep profiling, frame range, memory stats. " +
            "Use this to check what the profiler is doing before starting/stopping a recording.",
            "profiling")]
        public static string Inspect()
        {
            var first = EditorApi.FirstFrameIndex;
            var last  = EditorApi.LastFrameIndex;

            var response = new ProfilerInspectResponse
            {
                enabled              = EditorApi.Enabled,
                deepProfiling        = EditorApi.DeepProfiling,
                profileEditor        = EditorApi.ProfileEditor,
                firstFrameIndex      = first,
                lastFrameIndex       = last,
                frameCount           = last >= first ? last - first + 1 : 0,
                totalAllocatedMemory = RuntimeApi.GetTotalAllocatedMemory(),
                totalReservedMemory  = RuntimeApi.GetTotalReservedMemory(),
                monoHeapSize         = RuntimeApi.GetMonoHeapSize(),
                monoUsedSize         = RuntimeApi.GetMonoUsedSize(),
                graphicsMemory       = RuntimeApi.GetGraphicsMemory(),
                extras = new ProfilerInspectExtras
                {
                    allocationCallstacks = RuntimeApi.GetAllocationCallstacksEnabled(),
                },
            };

            return JsonUtility.ToJson(response);
        }

        [UnifoclCommand(
            "profiling.start_recording",
            "Start profiler recording. Options: deep (deep profiling), editor (profile editor), " +
            "keepFrames (preserve existing frame history). Requires PrivilegedExec.",
            "profiling")]
        public static string StartRecording(bool deep = false, bool editor = false, bool keepFrames = false)
        {
            if (EditorApi.Enabled)
                return ErrorJson(ErrorCodes.AlreadyRecording, "Profiler is already recording. Stop it first.");

            if (!keepFrames)
                EditorApi.ClearAllFrames();

            EditorApi.DeepProfiling = deep;
            EditorApi.ProfileEditor = editor;
            EditorApi.Enabled = true;

            return JsonUtility.ToJson(new ProfilerStartRecordingResponse
            {
                ok      = true,
                message = $"Recording started (deep={deep}, editor={editor}, keepFrames={keepFrames})",
            });
        }

        [UnifoclCommand(
            "profiling.stop_recording",
            "Stop profiler recording and return the captured frame range summary.",
            "profiling")]
        public static string StopRecording()
        {
            if (!EditorApi.Enabled)
                return ErrorJson(ErrorCodes.NotRecording, "Profiler is not recording.");

            EditorApi.Enabled = false;

            var first = EditorApi.FirstFrameIndex;
            var last  = EditorApi.LastFrameIndex;

            return JsonUtility.ToJson(new ProfilerStopRecordingResponse
            {
                ok              = true,
                message         = "Recording stopped",
                firstFrameIndex = first,
                lastFrameIndex  = last,
                frameCount      = last >= first ? last - first + 1 : 0,
            });
        }
    }
}
#endif
