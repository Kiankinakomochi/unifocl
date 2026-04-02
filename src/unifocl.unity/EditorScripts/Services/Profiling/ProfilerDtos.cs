#if UNITY_EDITOR
using System;
using System.Collections.Generic;

namespace UniFocl.EditorBridge.Profiling
{
    // ── Capability ──────────────────────────────────────────────────
    [Serializable]
    public sealed class ProfilerCapabilitiesResponse
    {
        public bool editorSessionApis;
        public bool frameDataViews;
        public bool memorySnapshots;
        public bool profilerRecorder;
        public bool frameTimingManager;
        public bool binaryLog;
        public bool externalGpuCapture;
        public List<string> notes = new();
    }

    // ── Inspect ─────────────────────────────────────────────────────
    [Serializable]
    public sealed class ProfilerInspectResponse
    {
        public bool enabled;
        public bool deepProfiling;
        public bool profileEditor;
        public int firstFrameIndex;
        public int lastFrameIndex;
        public int frameCount;
        public long totalAllocatedMemory;
        public long totalReservedMemory;
        public long monoHeapSize;
        public long monoUsedSize;
        public long graphicsMemory;
        public ProfilerInspectExtras extras = new();
    }

    [Serializable]
    public sealed class ProfilerInspectExtras
    {
        public bool allocationCallstacks;
    }

    // ── Start / Stop Recording ──────────────────────────────────────
    [Serializable]
    public sealed class ProfilerStartRecordingArgs
    {
        public bool deep;
        public bool editor;
        public bool keepFrames;
    }

    [Serializable]
    public sealed class ProfilerStartRecordingResponse
    {
        public bool ok;
        public string message = string.Empty;
    }

    [Serializable]
    public sealed class ProfilerStopRecordingResponse
    {
        public bool ok;
        public string message = string.Empty;
        public int firstFrameIndex;
        public int lastFrameIndex;
        public int frameCount;
    }

    // ── Save / Load Profile ─────────────────────────────────────────
    [Serializable]
    public sealed class ProfilerSaveProfileResponse
    {
        public bool ok;
        public string message = string.Empty;
        public string path = string.Empty;
        public long fileSizeBytes;
    }

    [Serializable]
    public sealed class ProfilerLoadProfileResponse
    {
        public bool ok;
        public string message = string.Empty;
        public string path = string.Empty;
        public int firstFrameIndex;
        public int lastFrameIndex;
    }

    // ── Memory Snapshot ─────────────────────────────────────────────
    [Serializable]
    public sealed class ProfilerTakeSnapshotResponse
    {
        public bool ok;
        public string message = string.Empty;
        public string path = string.Empty;
        public long fileSizeBytes;
    }

    // ── Frame Range Stats ───────────────────────────────────────────
    [Serializable]
    public sealed class ProfilerFramesResponse
    {
        public int from;
        public int to;
        public int frameCount;
        public ProfilerRangeStats cpuMs = new();
        public ProfilerRangeStats gpuMs = new();
        public ProfilerRangeStats fps = new();
    }

    [Serializable]
    public sealed class ProfilerRangeStats
    {
        public float avg;
        public float min;
        public float p50;
        public float p95;
        public float max;
        public bool available = true;
    }

    // ── Counters ────────────────────────────────────────────────────
    [Serializable]
    public sealed class ProfilerCountersResponse
    {
        public int from;
        public int to;
        public List<ProfilerCounterSeries> counters = new();
    }

    [Serializable]
    public sealed class ProfilerCounterSeries
    {
        public string name = string.Empty;
        public string category = string.Empty;
        public List<float> values = new();
    }

    // ── Threads ─────────────────────────────────────────────────────
    [Serializable]
    public sealed class ProfilerThreadsResponse
    {
        public int frame;
        public List<ProfilerThreadInfo> threads = new();
    }

    [Serializable]
    public sealed class ProfilerThreadInfo
    {
        public int threadIndex;
        public string threadName = string.Empty;
        public string threadGroupName = string.Empty;
    }

    // ── Markers ─────────────────────────────────────────────────────
    [Serializable]
    public sealed class ProfilerMarkersResponse
    {
        public int from;
        public int to;
        public string thread = string.Empty;
        public List<ProfilerMarkerEntry> markers = new();
    }

    [Serializable]
    public sealed class ProfilerMarkerEntry
    {
        public string name = string.Empty;
        public string path = string.Empty;
        public float totalMs;
        public float selfMs;
        public int calls;
        public float gcAllocBytes;
    }

    // ── Sample ──────────────────────────────────────────────────────
    [Serializable]
    public sealed class ProfilerSampleResponse
    {
        public int frame;
        public int threadIndex;
        public List<ProfilerSampleEntry> samples = new();
    }

    [Serializable]
    public sealed class ProfilerSampleEntry
    {
        public int sampleIndex;
        public string name = string.Empty;
        public float timeMs;
        public long metadataLong;
        public List<string> callstack = new();
    }

    // ── GC Alloc ────────────────────────────────────────────────────
    [Serializable]
    public sealed class ProfilerGcAllocResponse
    {
        public int from;
        public int to;
        public long totalBytes;
        public List<ProfilerGcAllocEntry> entries = new();
    }

    [Serializable]
    public sealed class ProfilerGcAllocEntry
    {
        public int frame;
        public string markerName = string.Empty;
        public long bytes;
        public List<string> callstack = new();
    }

    // ── Compare ─────────────────────────────────────────────────────
    [Serializable]
    public sealed class ProfilerCompareResponse
    {
        public ProfilerCompareSide baseline = new();
        public ProfilerCompareSide candidate = new();
        public ProfilerCompareDelta delta = new();
    }

    [Serializable]
    public sealed class ProfilerCompareSide
    {
        public int from;
        public int to;
        public int frameCount;
        public ProfilerRangeStats cpuMs = new();
        public ProfilerRangeStats gpuMs = new();
        public ProfilerRangeStats fps = new();
    }

    [Serializable]
    public sealed class ProfilerCompareDelta
    {
        public ProfilerDeltaEntry cpuMs = new();
        public ProfilerDeltaEntry gpuMs = new();
        public ProfilerDeltaEntry fps = new();
        public List<ProfilerMarkerDelta> markers = new();
    }

    [Serializable]
    public sealed class ProfilerDeltaEntry
    {
        public float absoluteAvg;
        public float percentAvg;
        public float absoluteP95;
        public float percentP95;
    }

    [Serializable]
    public sealed class ProfilerMarkerDelta
    {
        public string name = string.Empty;
        public float baselineTotalMs;
        public float candidateTotalMs;
        public float absoluteDelta;
        public float percentDelta;
    }

    // ── Budget Check ────────────────────────────────────────────────
    [Serializable]
    public sealed class ProfilerBudgetCheckResponse
    {
        public bool passed;
        public List<ProfilerBudgetResult> results = new();
    }

    [Serializable]
    public sealed class ProfilerBudgetResult
    {
        public string expression = string.Empty;
        public bool passed;
        public float actualValue;
        public string message = string.Empty;
    }

    // ── Export Summary ───────────────────────────────────────────────
    [Serializable]
    public sealed class ProfilerExportSummaryResponse
    {
        public bool ok;
        public string message = string.Empty;
        public string path = string.Empty;
        public long fileSizeBytes;
    }

    // ── Live Telemetry ──────────────────────────────────────────────
    [Serializable]
    public sealed class ProfilerLiveStartResponse
    {
        public bool ok;
        public string message = string.Empty;
        public List<string> attachedCounters = new();
    }

    [Serializable]
    public sealed class ProfilerLiveStopResponse
    {
        public bool ok;
        public string message = string.Empty;
        public List<ProfilerLiveCounterResult> counters = new();
    }

    [Serializable]
    public sealed class ProfilerLiveCounterResult
    {
        public string name = string.Empty;
        public ProfilerRangeStats stats = new();
        public List<double> recentSamples = new();
    }

    // ── Recorders List ──────────────────────────────────────────────
    [Serializable]
    public sealed class ProfilerRecordersListResponse
    {
        public List<ProfilerRecorderInfo> recorders = new();
    }

    [Serializable]
    public sealed class ProfilerRecorderInfo
    {
        public string name = string.Empty;
        public string category = string.Empty;
        public string unit = string.Empty;
    }

    // ── Frame Timing ────────────────────────────────────────────────
    [Serializable]
    public sealed class ProfilerFrameTimingResponse
    {
        public bool available;
        public float cpuFrameTimeMs;
        public float gpuFrameTimeMs;
        public float cpuMainThreadFrameTimeMs;
        public float cpuRenderThreadFrameTimeMs;
        public string bottleneck = string.Empty;
    }

    // ── Binary Log ──────────────────────────────────────────────────
    [Serializable]
    public sealed class ProfilerBinaryLogStartResponse
    {
        public bool ok;
        public string message = string.Empty;
        public string logFilePath = string.Empty;
    }

    [Serializable]
    public sealed class ProfilerBinaryLogStopResponse
    {
        public bool ok;
        public string message = string.Empty;
        public string logFilePath = string.Empty;
        public long fileSizeBytes;
    }

    // ── Annotate ────────────────────────────────────────────────────
    [Serializable]
    public sealed class ProfilerAnnotateResponse
    {
        public bool ok;
        public string message = string.Empty;
    }

    // ── GPU Capture ─────────────────────────────────────────────────
    [Serializable]
    public sealed class ProfilerGpuCaptureResponse
    {
        public bool ok;
        public string message = string.Empty;
        public bool isAttached;
    }

    // ── Generic error envelope ──────────────────────────────────────
    [Serializable]
    public sealed class ProfilerErrorResponse
    {
        public bool ok;
        public string error = string.Empty;
        public string errorCode = string.Empty;
    }
}
#endif
