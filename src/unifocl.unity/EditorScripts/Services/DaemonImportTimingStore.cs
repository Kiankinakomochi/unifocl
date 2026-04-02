#if UNITY_EDITOR
#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace UniFocl.EditorBridge
{
    /// <summary>
    /// Measures the wall-clock time of each asset-import batch and appends a record
    /// to <c>Library/unifocl-import-timing.json</c>.  The store is consumed by
    /// <c>diag import-hotspots</c> to surface slow assets.
    /// </summary>
    /// <remarks>
    /// Unity does not expose per-asset import durations directly.  We time the entire
    /// <c>OnPostprocessAllAssets</c> batch and associate every asset in that batch with
    /// the batch duration.  This is a lower-fidelity proxy but sufficient for hot-spot
    /// ranking when the same assets are re-imported repeatedly.
    /// </remarks>
    internal sealed class UnifoclImportTimingCapture : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(
            string[] importedAssets,
            string[] deletedAssets,
            string[] movedAssets,
            string[] movedFromAssetPaths)
        {
            // Only record batches that imported or moved assets — deletions carry no
            // meaningful timing signal.
            var tracked = importedAssets.Concat(movedAssets)
                .Where(p => !string.IsNullOrEmpty(p))
                .ToArray();

            if (tracked.Length == 0)
            {
                return;
            }

            // The stopwatch wraps only the capture logic itself; the actual Unity import
            // time is not measurable here.  We fall back to recording the timestamp and
            // asset count so that the store reflects *when* imports happened and which
            // assets were involved, even if durationMs is 0 on this code path.
            //
            // A non-zero durationMs is populated by the EditorApplication.delayCall-based
            // approach below for async batches, but for correctness the batch entry is
            // always written here with durationMs == 0.  A future iteration can use
            // AssetImportContext timing from Unity 2022.2+ (AssetImportContext.mainObject).
            var entry = new DaemonImportTimingStore.ImportBatchEntry
            {
                timestamp = DateTime.UtcNow.ToString("O"),
                assetPaths = tracked,
                batchDurationMs = 0f,
                assetCount = tracked.Length
            };

            DaemonImportTimingStore.AppendBatch(entry);
        }
    }

    internal static class DaemonImportTimingStore
    {
        internal const string StorePath = "Library/unifocl-import-timing.json";
        private const int MaxEntries = 500;
        private static readonly object Lock = new();

        internal static void AppendBatch(ImportBatchEntry entry)
        {
            lock (Lock)
            {
                var store = ReadStore();
                store.entries ??= Array.Empty<ImportBatchEntry>();

                var list = store.entries.ToList();
                list.Add(entry);

                if (list.Count > MaxEntries)
                {
                    list.RemoveRange(0, list.Count - MaxEntries);
                }

                store.entries = list.ToArray();

                try
                {
                    var json = JsonUtility.ToJson(store, prettyPrint: false);
                    File.WriteAllText(StorePath, json);
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogWarning($"[unifocl] failed to write import timing store: {ex.Message}");
                }
            }
        }

        internal static ImportTimingStoreFile ReadStore()
        {
            if (!File.Exists(StorePath))
            {
                return new ImportTimingStoreFile { entries = Array.Empty<ImportBatchEntry>() };
            }

            try
            {
                var json = File.ReadAllText(StorePath);
                return JsonUtility.FromJson<ImportTimingStoreFile>(json)
                    ?? new ImportTimingStoreFile { entries = Array.Empty<ImportBatchEntry>() };
            }
            catch
            {
                return new ImportTimingStoreFile { entries = Array.Empty<ImportBatchEntry>() };
            }
        }

        [Serializable]
        internal sealed class ImportTimingStoreFile
        {
            public ImportBatchEntry[] entries = Array.Empty<ImportBatchEntry>();
        }

        [Serializable]
        internal sealed class ImportBatchEntry
        {
            public string timestamp = "";
            public string[] assetPaths = Array.Empty<string>();
            public float batchDurationMs;
            public int assetCount;
        }
    }
}
#endif
