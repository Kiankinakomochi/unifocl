#if UNITY_EDITOR
namespace UniFocl.EditorBridge.Profiling
{
    /// <summary>
    /// Adapter interface for Unity runtime profiler APIs.
    /// Wraps <c>UnityEngine.Profiling.Profiler</c>, <c>ProfilerRecorder</c>,
    /// <c>FrameTimingManager</c>, and <c>MemoryProfiler</c> surfaces.
    /// </summary>
    internal interface IProfilerRuntimeApi
    {
        // ── Memory stats ────────────────────────────────────────────
        long GetTotalAllocatedMemory();
        long GetTotalReservedMemory();
        long GetMonoHeapSize();
        long GetMonoUsedSize();
        long GetGraphicsMemory();
        bool GetAllocationCallstacksEnabled();

        // ── Binary log control ──────────────────────────────────────
        string LogFile { get; set; }
        bool EnableBinaryLog { get; set; }
        bool Enabled { get; set; }

        // ── Memory snapshot ─────────────────────────────────────────
        /// <summary>
        /// Take a memory snapshot. The callback fires on completion with (path, success).
        /// </summary>
        void TakeSnapshot(string path, System.Action<string, bool> finishCallback,
            Unity.Profiling.Memory.CaptureFlags captureFlags);

        // ── Session / frame metadata ────────────────────────────────
        void EmitSessionMetaData(System.Guid guid, int tag, byte[] data);
        void EmitFrameMetaData(System.Guid guid, int tag, byte[] data);
    }
}
#endif
