#if UNITY_EDITOR
#nullable enable
using System;
using UnityEditor;
using UnityEngine;

namespace UniFocl.EditorBridge
{
    internal static partial class DaemonProjectService
    {
        private static string ExecutePlaymodeStart()
        {
            try
            {
                if (EditorApplication.isPlaying)
                {
                    return JsonUtility.ToJson(new ProjectCommandResponse
                    {
                        ok = false,
                        message = "already in Play Mode"
                    });
                }

                EditorApplication.isPlaying = true;
                return JsonUtility.ToJson(new ProjectCommandResponse
                {
                    ok = true,
                    message = "entering Play Mode",
                    kind = "playmode"
                });
            }
            catch (Exception ex)
            {
                return JsonUtility.ToJson(new ProjectCommandResponse
                {
                    ok = false,
                    message = $"playmode start failed: {ex.Message}"
                });
            }
        }

        private static string ExecutePlaymodeStop()
        {
            try
            {
                if (!EditorApplication.isPlaying)
                {
                    return JsonUtility.ToJson(new ProjectCommandResponse
                    {
                        ok = false,
                        message = "not in Play Mode"
                    });
                }

                EditorApplication.isPlaying = false;
                return JsonUtility.ToJson(new ProjectCommandResponse
                {
                    ok = true,
                    message = "exiting Play Mode",
                    kind = "playmode"
                });
            }
            catch (Exception ex)
            {
                return JsonUtility.ToJson(new ProjectCommandResponse
                {
                    ok = false,
                    message = $"playmode stop failed: {ex.Message}"
                });
            }
        }

        private static string ExecutePlaymodePause()
        {
            try
            {
                if (!EditorApplication.isPlaying)
                {
                    return JsonUtility.ToJson(new ProjectCommandResponse
                    {
                        ok = false,
                        message = "cannot pause: not in Play Mode"
                    });
                }

                if (EditorApplication.isPaused)
                {
                    return JsonUtility.ToJson(new ProjectCommandResponse
                    {
                        ok = false,
                        message = "already paused"
                    });
                }

                EditorApplication.isPaused = true;
                return JsonUtility.ToJson(new ProjectCommandResponse
                {
                    ok = true,
                    message = "Play Mode paused",
                    kind = "playmode"
                });
            }
            catch (Exception ex)
            {
                return JsonUtility.ToJson(new ProjectCommandResponse
                {
                    ok = false,
                    message = $"playmode pause failed: {ex.Message}"
                });
            }
        }

        private static string ExecutePlaymodeResume()
        {
            try
            {
                if (!EditorApplication.isPlaying)
                {
                    return JsonUtility.ToJson(new ProjectCommandResponse
                    {
                        ok = false,
                        message = "cannot resume: not in Play Mode"
                    });
                }

                if (!EditorApplication.isPaused)
                {
                    return JsonUtility.ToJson(new ProjectCommandResponse
                    {
                        ok = false,
                        message = "not paused"
                    });
                }

                EditorApplication.isPaused = false;
                return JsonUtility.ToJson(new ProjectCommandResponse
                {
                    ok = true,
                    message = "Play Mode resumed",
                    kind = "playmode"
                });
            }
            catch (Exception ex)
            {
                return JsonUtility.ToJson(new ProjectCommandResponse
                {
                    ok = false,
                    message = $"playmode resume failed: {ex.Message}"
                });
            }
        }

        private static string ExecutePlaymodeStep()
        {
            try
            {
                if (!EditorApplication.isPlaying)
                {
                    return JsonUtility.ToJson(new ProjectCommandResponse
                    {
                        ok = false,
                        message = "cannot step: not in Play Mode"
                    });
                }

                if (!EditorApplication.isPaused)
                {
                    return JsonUtility.ToJson(new ProjectCommandResponse
                    {
                        ok = false,
                        message = "cannot step: Play Mode is not paused"
                    });
                }

                EditorApplication.Step();
                return JsonUtility.ToJson(new ProjectCommandResponse
                {
                    ok = true,
                    message = "advanced one frame",
                    kind = "playmode"
                });
            }
            catch (Exception ex)
            {
                return JsonUtility.ToJson(new ProjectCommandResponse
                {
                    ok = false,
                    message = $"playmode step failed: {ex.Message}"
                });
            }
        }
    }
}
#endif
