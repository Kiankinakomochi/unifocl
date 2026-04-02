#if UNITY_EDITOR
using System;
using System.IO;
using UnityEngine;

namespace UniFocl.EditorBridge.Profiling
{
    internal static partial class DaemonProfilerService
    {
        [UnifoclCommand(
            "profiling.save_profile",
            "Save the current editor profiler session to disk as a .data capture. " +
            "Returns the file path and size on success.",
            "profiling")]
        public static string SaveProfile(string path)
        {
            try
            {
                var normalized = ProfilerPathUtils.NormalizeEditorCapturePath(path);
                ProfilerPathUtils.EnsureParentDirectory(normalized);

                EditorApi.SaveProfile(normalized);

                if (!ProfilerPathUtils.FileExists(normalized))
                    return ErrorJson(ErrorCodes.IoError, $"SaveProfile completed but file not found at: {normalized}");

                return JsonUtility.ToJson(new ProfilerSaveProfileResponse
                {
                    ok            = true,
                    message       = "Profile saved",
                    path          = normalized,
                    fileSizeBytes = ProfilerPathUtils.GetFileSize(normalized),
                });
            }
            catch (ArgumentException ex)
            {
                return ErrorJson(ErrorCodes.InvalidPath, ex.Message);
            }
            catch (Exception ex)
            {
                return ErrorJson(ErrorCodes.IoError, $"Failed to save profile: {ex.Message}");
            }
        }

        [UnifoclCommand(
            "profiling.load_profile",
            "Load a profiler capture (.data) into the editor session. " +
            "Set keepExisting=true to append to existing frame data.",
            "profiling")]
        public static string LoadProfile(string path, bool keepExisting = false)
        {
            try
            {
                var normalized = ProfilerPathUtils.NormalizeEditorCapturePath(path);

                if (!ProfilerPathUtils.FileExists(normalized))
                    return ErrorJson(ErrorCodes.FileNotFound, $"File not found: {normalized}");

                var ok = EditorApi.LoadProfile(normalized, keepExisting);
                if (!ok)
                    return ErrorJson(ErrorCodes.IoError, $"ProfilerDriver.LoadProfile returned false for: {normalized}");

                return JsonUtility.ToJson(new ProfilerLoadProfileResponse
                {
                    ok              = true,
                    message         = "Profile loaded",
                    path            = normalized,
                    firstFrameIndex = EditorApi.FirstFrameIndex,
                    lastFrameIndex  = EditorApi.LastFrameIndex,
                });
            }
            catch (ArgumentException ex)
            {
                return ErrorJson(ErrorCodes.InvalidPath, ex.Message);
            }
            catch (Exception ex)
            {
                return ErrorJson(ErrorCodes.IoError, $"Failed to load profile: {ex.Message}");
            }
        }
    }
}
#endif
