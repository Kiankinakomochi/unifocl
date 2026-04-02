#if UNITY_EDITOR
using System;
using System.IO;
using Unity.Profiling.Memory;
using UnityEngine;

namespace UniFocl.EditorBridge.Profiling
{
    internal static partial class DaemonProfilerService
    {
        private const CaptureFlags DefaultSnapshotFlags =
            CaptureFlags.ManagedObjects |
            CaptureFlags.NativeObjects  |
            CaptureFlags.NativeAllocations;

        [UnifoclCommand(
            "profiling.take_snapshot",
            "Take a memory snapshot (.snap). The snapshot is written to a temp path first, " +
            "then renamed on success. Completion is async — the response indicates initiation.",
            "profiling")]
        public static string TakeSnapshot(string path)
        {
            try
            {
                var normalized = ProfilerPathUtils.NormalizeSnapshotPath(path);
                ProfilerPathUtils.EnsureParentDirectory(normalized);

                var tempPath = ProfilerPathUtils.GetTempPathForAtomic(normalized);
                var finalPath = normalized;

                RuntimeApi.TakeSnapshot(tempPath, (snapshotPath, success) =>
                {
                    if (!success)
                    {
                        Debug.LogWarning($"[unifocl] Memory snapshot failed for: {finalPath}");
                        TryDeleteFile(tempPath);
                        return;
                    }

                    try
                    {
                        if (File.Exists(finalPath))
                            File.Delete(finalPath);
                        File.Move(snapshotPath, finalPath);
                        Debug.Log($"[unifocl] Memory snapshot saved: {finalPath} ({ProfilerPathUtils.GetFileSize(finalPath)} bytes)");
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"[unifocl] Failed to move snapshot to final path: {ex.Message}");
                    }
                }, DefaultSnapshotFlags);

                return JsonUtility.ToJson(new ProfilerTakeSnapshotResponse
                {
                    ok            = true,
                    message       = $"Snapshot initiated. Will be saved to: {finalPath}",
                    path          = finalPath,
                    fileSizeBytes = 0, // not yet available — async completion
                });
            }
            catch (ArgumentException ex)
            {
                return ErrorJson(ErrorCodes.InvalidPath, ex.Message);
            }
            catch (Exception ex)
            {
                return ErrorJson(ErrorCodes.SnapshotFailed, $"Failed to initiate snapshot: {ex.Message}");
            }
        }

        private static void TryDeleteFile(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { /* best effort cleanup */ }
        }
    }
}
#endif
