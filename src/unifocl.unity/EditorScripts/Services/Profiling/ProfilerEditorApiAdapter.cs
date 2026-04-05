#if UNITY_EDITOR
using UnityEditor.Profiling;
using UnityEditorInternal;

namespace UniFocl.EditorBridge.Profiling
{
    /// <summary>
    /// Default adapter that delegates directly to Unity's <c>ProfilerDriver</c>
    /// and related editor-only APIs.
    /// </summary>
    internal sealed class ProfilerEditorApiAdapter : IProfilerEditorApi
    {
        public static readonly ProfilerEditorApiAdapter Instance = new();

        // ── Session state ───────────────────────────────────────────
        public bool Enabled
        {
            get => ProfilerDriver.enabled;
            set => ProfilerDriver.enabled = value;
        }

        public bool DeepProfiling
        {
            get => ProfilerDriver.deepProfiling;
            set => ProfilerDriver.deepProfiling = value;
        }

        public bool ProfileEditor
        {
            get => ProfilerDriver.profileEditor;
            set => ProfilerDriver.profileEditor = value;
        }

        public int FirstFrameIndex => ProfilerDriver.firstFrameIndex;
        public int LastFrameIndex  => ProfilerDriver.lastFrameIndex;

        // ── Session lifecycle ───────────────────────────────────────
        public void ClearAllFrames() => ProfilerDriver.ClearAllFrames();

        public void SaveProfile(string path) => ProfilerDriver.SaveProfile(path);

        public bool LoadProfile(string path, bool keepExistingData)
            => ProfilerDriver.LoadProfile(path, keepExistingData);

        // ── Frame data views ────────────────────────────────────────
        public RawFrameDataView? GetRawFrameDataView(int frameIndex, int threadIndex)
        {
            var view = ProfilerDriver.GetRawFrameDataView(frameIndex, threadIndex);
            return view != null && view.valid ? view : null;
        }

        public HierarchyFrameDataView? GetHierarchyFrameDataView(
            int frameIndex, int threadIndex,
            HierarchyFrameDataView.ViewModes viewMode,
            int sortColumn, bool sortAscending)
        {
            var view = ProfilerDriver.GetHierarchyFrameDataView(
                frameIndex, threadIndex, viewMode, sortColumn, sortAscending);
            return view != null && view.valid ? view : null;
        }
    }
}
#endif
