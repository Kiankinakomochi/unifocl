#if UNITY_EDITOR
using System;
using UnityEngine;

namespace UniFocl.EditorBridge.Profiling
{
    internal static partial class DaemonProfilerService
    {
        [UnifoclCommand(
            "profiling.binary_log_start",
            "Start raw binary profiler logging (.raw) via Profiler.logFile + enableBinaryLog. " +
            "This is separate from editor save/load — it writes a streaming .raw file.",
            "profiling")]
        public static string BinaryLogStart(string path)
        {
            try
            {
                var normalized = ProfilerPathUtils.NormalizeBinaryLogPath(path);
                ProfilerPathUtils.EnsureParentDirectory(normalized);

                RuntimeApi.LogFile = normalized;
                RuntimeApi.EnableBinaryLog = true;
                RuntimeApi.Enabled = true;

                return JsonUtility.ToJson(new ProfilerBinaryLogStartResponse
                {
                    ok          = true,
                    message     = "Binary logging started",
                    logFilePath = normalized,
                });
            }
            catch (ArgumentException ex)
            {
                return ErrorJson(ErrorCodes.InvalidPath, ex.Message);
            }
            catch (Exception ex)
            {
                return ErrorJson(ErrorCodes.IoError, $"Failed to start binary log: {ex.Message}");
            }
        }

        [UnifoclCommand(
            "profiling.binary_log_stop",
            "Stop raw binary profiler logging and return the log file path and size.",
            "profiling")]
        public static string BinaryLogStop()
        {
            try
            {
                var logPath = RuntimeApi.LogFile;

                RuntimeApi.EnableBinaryLog = false;
                RuntimeApi.Enabled = false;
                RuntimeApi.LogFile = string.Empty;

                var size = !string.IsNullOrEmpty(logPath)
                    ? ProfilerPathUtils.GetFileSize(logPath)
                    : -1;

                return JsonUtility.ToJson(new ProfilerBinaryLogStopResponse
                {
                    ok            = true,
                    message       = "Binary logging stopped",
                    logFilePath   = logPath ?? string.Empty,
                    fileSizeBytes = size,
                });
            }
            catch (Exception ex)
            {
                return ErrorJson(ErrorCodes.IoError, $"Failed to stop binary log: {ex.Message}");
            }
        }
    }
}
#endif
