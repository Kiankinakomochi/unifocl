#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor.Profiling;
using UnityEngine;

namespace UniFocl.EditorBridge.Profiling
{
    internal static partial class DaemonProfilerService
    {
        [UnifoclCommand(
            "profiling.markers",
            "Return top markers sorted by total or self time. For a single frame pass from=N, to=N. " +
            "For a range, markers are aggregated (merged) across frames. sortBy: totalMs or selfMs.",
            "profiling")]
        public static string Markers(int from, int to = -1, int threadIndex = 0, int limit = 20, string sortBy = "totalMs")
        {
            if (to == -1) to = from;
            if (from < 0 || to < from)
                return ErrorJson(ErrorCodes.InvalidFrameRange, $"Invalid frame range: {from}..{to}");

            var acc = new Dictionary<string, ProfilerMarkerEntry>();

            for (int f = from; f <= to; f++)
            {
                var view = EditorApi.GetHierarchyFrameDataView(
                    f, threadIndex,
                    HierarchyFrameDataView.ViewModes.MergeSamplesWithTheSameName,
                    (int)HierarchyFrameDataView.columnTotalTime,
                    false);
                if (view == null) continue;
                try
                {
                    var root = view.GetRootItemID();
                    CollectMarkersRecursive(view, root, acc);
                }
                finally { view.Dispose(); }
            }

            var sorted = acc.Values.ToList();
            if (sortBy == "selfMs")
                sorted.Sort((a, b) => b.selfMs.CompareTo(a.selfMs));
            else
                sorted.Sort((a, b) => b.totalMs.CompareTo(a.totalMs));

            if (sorted.Count > limit)
                sorted = sorted.GetRange(0, limit);

            return JsonUtility.ToJson(new ProfilerMarkersResponse
            {
                from    = from,
                to      = to,
                thread  = threadIndex.ToString(),
                markers = sorted,
            });
        }

        [UnifoclCommand(
            "profiling.sample",
            "Return raw sample details for a specific frame and thread. " +
            "Includes per-sample timing, metadata, and callstacks when available.",
            "profiling")]
        public static string Sample(int frame, int threadIndex = 0, int limit = 50)
        {
            var view = EditorApi.GetRawFrameDataView(frame, threadIndex);
            if (view == null)
                return ErrorJson(ErrorCodes.NoFrameData, $"No frame data for frame {frame}, thread {threadIndex}");

            var samples = new List<ProfilerSampleEntry>();
            try
            {
                var count = Math.Min(view.sampleCount, limit);
                for (int i = 0; i < count; i++)
                {
                    var entry = new ProfilerSampleEntry
                    {
                        sampleIndex = i,
                        name        = view.GetSampleName(i) ?? string.Empty,
                        timeMs      = view.GetSampleTimeMs(i),
                    };

                    // Metadata
                    if (view.GetSampleMetadataCount(i) > 0)
                    {
                        try { entry.metadataLong = view.GetSampleMetadataAsLong(i, 0); }
                        catch { /* metadata format mismatch — ignore */ }
                    }

                    // Callstack
                    try
                    {
                        var callstackStr = view.GetSampleCallstack(i);
                        if (!string.IsNullOrEmpty(callstackStr))
                        {
                            foreach (var line in callstackStr.Split('\n'))
                            {
                                var trimmed = line.Trim();
                                if (!string.IsNullOrEmpty(trimmed))
                                    entry.callstack.Add(trimmed);
                            }
                        }
                    }
                    catch { /* callstack not available for this sample */ }

                    samples.Add(entry);
                }
            }
            finally { view.Dispose(); }

            return JsonUtility.ToJson(new ProfilerSampleResponse
            {
                frame       = frame,
                threadIndex = threadIndex,
                samples     = samples,
            });
        }

        [UnifoclCommand(
            "profiling.gc_alloc",
            "Analyze GC allocations in a frame range. Returns per-allocation entries with marker name, " +
            "byte count, and callstack when available. Sorted by bytes descending.",
            "profiling")]
        public static string GcAlloc(int from, int to, int limit = 50)
        {
            if (from < 0 || to < from)
                return ErrorJson(ErrorCodes.InvalidFrameRange, $"Invalid frame range: {from}..{to}");

            var entries = new List<ProfilerGcAllocEntry>();
            long totalBytes = 0;

            for (int f = from; f <= to; f++)
            {
                var view = EditorApi.GetRawFrameDataView(f, 0);
                if (view == null) continue;
                try
                {
                    for (int i = 0; i < view.sampleCount; i++)
                    {
                        if (view.GetSampleMetadataCount(i) == 0) continue;

                        long allocBytes;
                        try { allocBytes = view.GetSampleMetadataAsLong(i, 0); }
                        catch { continue; }

                        if (allocBytes <= 0) continue;

                        totalBytes += allocBytes;

                        var entry = new ProfilerGcAllocEntry
                        {
                            frame      = f,
                            markerName = view.GetSampleName(i) ?? string.Empty,
                            bytes      = allocBytes,
                        };

                        try
                        {
                            var cs = view.GetSampleCallstack(i);
                            if (!string.IsNullOrEmpty(cs))
                            {
                                foreach (var line in cs.Split('\n'))
                                {
                                    var trimmed = line.Trim();
                                    if (!string.IsNullOrEmpty(trimmed))
                                        entry.callstack.Add(trimmed);
                                }
                            }
                        }
                        catch { /* callstack not available */ }

                        entries.Add(entry);
                    }
                }
                finally { view.Dispose(); }
            }

            entries.Sort((a, b) => b.bytes.CompareTo(a.bytes));
            if (entries.Count > limit)
                entries = entries.GetRange(0, limit);

            return JsonUtility.ToJson(new ProfilerGcAllocResponse
            {
                from       = from,
                to         = to,
                totalBytes = totalBytes,
                entries    = entries,
            });
        }

        // ── Hierarchy walker ────────────────────────────────────────
        private static void CollectMarkersRecursive(
            HierarchyFrameDataView view, int itemId,
            Dictionary<string, ProfilerMarkerEntry> acc)
        {
            var name = view.GetItemName(itemId);
            if (!string.IsNullOrEmpty(name) && name != "N/A")
            {
                var totalMs  = view.GetItemColumnDataAsSingle(itemId, HierarchyFrameDataView.columnTotalTime);
                var selfMs   = view.GetItemColumnDataAsSingle(itemId, HierarchyFrameDataView.columnSelfTime);
                var calls    = (int)view.GetItemColumnDataAsSingle(itemId, HierarchyFrameDataView.columnCalls);
                var gcAlloc  = view.GetItemColumnDataAsSingle(itemId, HierarchyFrameDataView.columnGcMemory);

                if (acc.TryGetValue(name, out var existing))
                {
                    existing.totalMs       += totalMs;
                    existing.selfMs        += selfMs;
                    existing.calls         += calls;
                    existing.gcAllocBytes  += gcAlloc;
                }
                else
                {
                    acc[name] = new ProfilerMarkerEntry
                    {
                        name         = name,
                        path         = view.GetItemPath(itemId),
                        totalMs      = totalMs,
                        selfMs       = selfMs,
                        calls        = calls,
                        gcAllocBytes = gcAlloc,
                    };
                }
            }

            var children = new List<int>();
            view.GetItemChildren(itemId, children);
            foreach (var child in children)
                CollectMarkersRecursive(view, child, acc);
        }
    }
}
#endif
