#if UNITY_EDITOR
using System;
using System.Text;
using UnityEngine;

namespace UniFocl.EditorBridge.Profiling
{
    internal static partial class DaemonProfilerService
    {
        private static readonly Guid k_UnifoclMetadataGuid = new("a1b2c3d4-e5f6-7890-abcd-ef1234567890");
        private const int k_SessionMetadataTag = 1;
        private const int k_FrameMetadataTag   = 2;

        [UnifoclCommand(
            "profiling.annotate_session",
            "Emit session-level metadata into the profiler stream. Pass a JSON string with " +
            "fields like gitSha, branch, testCase, sceneName, buildTarget, deviceModel, " +
            "graphicsApi, qualityTier, commandOrigin.",
            "profiling")]
        public static string AnnotateSession(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return ErrorJson(ErrorCodes.InvalidPath, "Metadata JSON must not be empty.");

            try
            {
                var data = Encoding.UTF8.GetBytes(json);
                RuntimeApi.EmitSessionMetaData(k_UnifoclMetadataGuid, k_SessionMetadataTag, data);

                return JsonUtility.ToJson(new ProfilerAnnotateResponse
                {
                    ok      = true,
                    message = $"Session metadata emitted ({data.Length} bytes)",
                });
            }
            catch (Exception ex)
            {
                return ErrorJson(ErrorCodes.IoError, $"Failed to emit session metadata: {ex.Message}");
            }
        }

        [UnifoclCommand(
            "profiling.annotate_frame",
            "Emit frame-level metadata into the profiler stream for the current frame. " +
            "Pass a JSON string with per-frame context.",
            "profiling")]
        public static string AnnotateFrame(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return ErrorJson(ErrorCodes.InvalidPath, "Metadata JSON must not be empty.");

            try
            {
                var data = Encoding.UTF8.GetBytes(json);
                RuntimeApi.EmitFrameMetaData(k_UnifoclMetadataGuid, k_FrameMetadataTag, data);

                return JsonUtility.ToJson(new ProfilerAnnotateResponse
                {
                    ok      = true,
                    message = $"Frame metadata emitted ({data.Length} bytes)",
                });
            }
            catch (Exception ex)
            {
                return ErrorJson(ErrorCodes.IoError, $"Failed to emit frame metadata: {ex.Message}");
            }
        }
    }
}
#endif
