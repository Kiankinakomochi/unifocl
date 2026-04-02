#if UNITY_EDITOR
namespace UniFocl.EditorBridge.Profiling
{
    /// <summary>
    /// Adapter interface for Unity Editor-only profiler APIs.
    /// Wraps <c>ProfilerDriver</c>, <c>FrameDataView</c>, and related types
    /// so that the service layer can be tested or compatibility-shimmed.
    /// </summary>
    internal interface IProfilerEditorApi
    {
        // ── Session state ───────────────────────────────────────────
        bool Enabled { get; set; }
        bool DeepProfiling { get; set; }
        bool ProfileEditor { get; set; }
        int FirstFrameIndex { get; }
        int LastFrameIndex { get; }

        // ── Session lifecycle ───────────────────────────────────────
        void ClearAllFrames();
        void SaveProfile(string path);
        bool LoadProfile(string path, bool keepExistingData);

        // ── Frame data views ────────────────────────────────────────
        /// <summary>
        /// Get a raw frame data view. Caller MUST dispose the returned object.
        /// Returns null if the frame/thread combination is invalid.
        /// </summary>
        UnityEditor.Profiling.RawFrameDataView GetRawFrameDataView(int frameIndex, int threadIndex);

        /// <summary>
        /// Get a hierarchy frame data view. Caller MUST dispose the returned object.
        /// Returns null if the frame/thread combination is invalid.
        /// </summary>
        UnityEditor.Profiling.HierarchyFrameDataView GetHierarchyFrameDataView(
            int frameIndex, int threadIndex,
            UnityEditor.Profiling.HierarchyFrameDataView.ViewModes viewMode,
            int sortColumn, bool sortAscending);
    }
}
#endif
