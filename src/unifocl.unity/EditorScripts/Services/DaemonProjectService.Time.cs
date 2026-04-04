#if UNITY_EDITOR
using System;
using UnityEngine;

namespace UniFocl.EditorBridge
{
    internal static partial class DaemonProjectService
    {
        [Serializable]
        private sealed class TimeScalePayload
        {
            public float scale;
        }

        private static string ExecuteTimeScale(ProjectCommandRequest request)
        {
            try
            {
                var payload = JsonUtility.FromJson<TimeScalePayload>(request.content ?? "{}");

                if (payload.scale < 0f)
                {
                    return JsonUtility.ToJson(new ProjectCommandResponse
                    {
                        ok = false,
                        message = "timeScale must be >= 0"
                    });
                }

                var previous = Time.timeScale;
                Time.timeScale = payload.scale;

                return JsonUtility.ToJson(new ProjectCommandResponse
                {
                    ok = true,
                    message = $"Time.timeScale set to {payload.scale} (was {previous})",
                    kind = "time",
                    content = JsonUtility.ToJson(new TimeScaleResult
                    {
                        previous = previous,
                        current = payload.scale
                    })
                });
            }
            catch (Exception ex)
            {
                return JsonUtility.ToJson(new ProjectCommandResponse
                {
                    ok = false,
                    message = $"time scale failed: {ex.Message}"
                });
            }
        }

        [Serializable]
        private sealed class TimeScaleResult
        {
            public float previous;
            public float current;
        }
    }
}
#endif
