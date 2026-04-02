#if UNITY_EDITOR
using System;
using UnityEngine.Profiling;
using Unity.Profiling.Memory;

namespace UniFocl.EditorBridge.Profiling
{
    /// <summary>
    /// Default adapter that delegates directly to Unity's runtime
    /// <c>Profiler</c> and <c>MemoryProfiler</c> APIs.
    /// </summary>
    internal sealed class ProfilerRuntimeApiAdapter : IProfilerRuntimeApi
    {
        public static readonly ProfilerRuntimeApiAdapter Instance = new();

        // ── Memory stats ────────────────────────────────────────────
        public long GetTotalAllocatedMemory() => Profiler.GetTotalAllocatedMemoryLong();
        public long GetTotalReservedMemory()  => Profiler.GetTotalReservedMemoryLong();
        public long GetMonoHeapSize()         => Profiler.GetMonoHeapSizeLong();
        public long GetMonoUsedSize()         => Profiler.GetMonoUsedSizeLong();
        public long GetGraphicsMemory()       => Profiler.GetAllocatedMemoryForGraphicsDriver();
        public bool GetAllocationCallstacksEnabled() => Profiler.enableAllocationCallstacks;

        // ── Binary log control ──────────────────────────────────────
        public string LogFile
        {
            get => Profiler.logFile;
            set => Profiler.logFile = value;
        }

        public bool EnableBinaryLog
        {
            get => Profiler.enableBinaryLog;
            set => Profiler.enableBinaryLog = value;
        }

        public bool Enabled
        {
            get => Profiler.enabled;
            set => Profiler.enabled = value;
        }

        // ── Memory snapshot ─────────────────────────────────────────
        public void TakeSnapshot(string path, Action<string, bool> finishCallback,
            CaptureFlags captureFlags)
        {
            MemoryProfiler.TakeSnapshot(path, finishCallback, captureFlags);
        }

        // ── Session / frame metadata ────────────────────────────────
        public void EmitSessionMetaData(Guid guid, int tag, byte[] data)
            => Profiler.EmitSessionMetaData(guid, tag, data);

        public void EmitFrameMetaData(Guid guid, int tag, byte[] data)
            => Profiler.EmitFrameMetaData(guid, tag, data);
    }
}
#endif
