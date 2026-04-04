#if UNITY_EDITOR
using System;
using System.Reflection;
using UnityEngine;

namespace UniFocl.EditorBridge.Recorder
{
    /// <summary>
    /// Lazy-loaded category service for Unity Recorder capture control.
    /// Uses reflection to access <c>UnityEditor.Recorder</c> APIs so the package
    /// is not a hard dependency — commands return a descriptive error when the
    /// <c>com.unity.recorder</c> package is not installed.
    /// </summary>
    internal static class DaemonRecorderService
    {
        private const string RecorderWindowType =
            "UnityEditor.Recorder.RecorderWindow, Unity.Recorder.Editor";
        private const string RecorderControllerType =
            "UnityEditor.Recorder.RecorderController, Unity.Recorder.Editor";
        private const string RecorderControllerSettingsType =
            "UnityEditor.Recorder.RecorderControllerSettings, Unity.Recorder.Editor";

        // ── recorder.start ───────────────────────────────────────────

        [UnifoclCommand(
            "recorder.start",
            "Start a Unity Recorder capture session. Requires the com.unity.recorder package to be installed.",
            "recorder")]
        public static string StartRecording(string json)
        {
            try
            {
                var controllerSettingsType = Type.GetType(RecorderControllerSettingsType);
                var controllerType = Type.GetType(RecorderControllerType);
                if (controllerSettingsType is null || controllerType is null)
                {
                    return JsonUtility.ToJson(new RecorderResponse
                    {
                        ok = false,
                        message = "Unity Recorder package (com.unity.recorder) is not installed or not found"
                    });
                }

                // Get or create RecorderControllerSettings
                var getDefaultMethod = controllerSettingsType.GetMethod("GetGlobalSettings",
                    BindingFlags.Public | BindingFlags.Static);

                if (getDefaultMethod is null)
                {
                    // Try alternative: find existing settings asset
                    var settingsObj = FindRecorderSettings(controllerSettingsType);
                    if (settingsObj is null)
                    {
                        return JsonUtility.ToJson(new RecorderResponse
                        {
                            ok = false,
                            message = "No RecorderControllerSettings found. Open Window > General > Recorder > Recorder Window to create default settings."
                        });
                    }

                    var controller = Activator.CreateInstance(controllerType, new object[] { settingsObj });
                    var startMethod = controllerType.GetMethod("PrepareRecording");
                    var startRecordingMethod = controllerType.GetMethod("StartRecording");

                    startMethod?.Invoke(controller, null);
                    startRecordingMethod?.Invoke(controller, null);

                    return JsonUtility.ToJson(new RecorderResponse
                    {
                        ok = true,
                        message = "recording started",
                        state = "recording"
                    });
                }
                else
                {
                    var settings = getDefaultMethod.Invoke(null, null);
                    var controller = Activator.CreateInstance(controllerType, new object[] { settings });
                    var startMethod = controllerType.GetMethod("PrepareRecording");
                    var startRecordingMethod = controllerType.GetMethod("StartRecording");

                    startMethod?.Invoke(controller, null);
                    startRecordingMethod?.Invoke(controller, null);

                    return JsonUtility.ToJson(new RecorderResponse
                    {
                        ok = true,
                        message = "recording started",
                        state = "recording"
                    });
                }
            }
            catch (Exception ex)
            {
                return JsonUtility.ToJson(new RecorderResponse
                {
                    ok = false,
                    message = $"recorder start failed: {ex.Message}"
                });
            }
        }

        // ── recorder.stop ────────────────────────────────────────────

        [UnifoclCommand(
            "recorder.stop",
            "Stop the active Unity Recorder session and flush the output file to disk.",
            "recorder")]
        public static string StopRecording(string json)
        {
            try
            {
                var controllerType = Type.GetType(RecorderControllerType);
                if (controllerType is null)
                {
                    return JsonUtility.ToJson(new RecorderResponse
                    {
                        ok = false,
                        message = "Unity Recorder package (com.unity.recorder) is not installed"
                    });
                }

                // Try to find the active RecorderController via the RecorderWindow
                var windowType = Type.GetType(RecorderWindowType);
                if (windowType is not null)
                {
                    var windows = Resources.FindObjectsOfTypeAll(windowType);
                    if (windows is not null && windows.Length > 0)
                    {
                        var window = windows[0];
                        var stopMethod = windowType.GetMethod("StopRecording",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                        if (stopMethod is not null)
                        {
                            stopMethod.Invoke(window, null);
                            return JsonUtility.ToJson(new RecorderResponse
                            {
                                ok = true,
                                message = "recording stopped",
                                state = "idle"
                            });
                        }
                    }
                }

                return JsonUtility.ToJson(new RecorderResponse
                {
                    ok = false,
                    message = "no active recording session found"
                });
            }
            catch (Exception ex)
            {
                return JsonUtility.ToJson(new RecorderResponse
                {
                    ok = false,
                    message = $"recorder stop failed: {ex.Message}"
                });
            }
        }

        // ── recorder.status ──────────────────────────────────────────

        [UnifoclCommand(
            "recorder.status",
            "Return the current Recorder state (recording/idle) and the path of the active profile.",
            "recorder")]
        public static string GetStatus(string json)
        {
            try
            {
                var controllerSettingsType = Type.GetType(RecorderControllerSettingsType);
                var windowType = Type.GetType(RecorderWindowType);

                if (controllerSettingsType is null)
                {
                    return JsonUtility.ToJson(new RecorderResponse
                    {
                        ok = true,
                        message = "Unity Recorder package not installed",
                        state = "unavailable"
                    });
                }

                var isRecording = false;
                var outputPath = "";

                if (windowType is not null)
                {
                    var windows = Resources.FindObjectsOfTypeAll(windowType);
                    if (windows is not null && windows.Length > 0)
                    {
                        var window = windows[0];
                        var isRecordingProp = windowType.GetProperty("IsRecording",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (isRecordingProp is not null)
                        {
                            isRecording = (bool)isRecordingProp.GetValue(window);
                        }
                    }
                }

                // Try to get output path from settings
                var getDefaultMethod = controllerSettingsType.GetMethod("GetGlobalSettings",
                    BindingFlags.Public | BindingFlags.Static);
                if (getDefaultMethod is not null)
                {
                    var settings = getDefaultMethod.Invoke(null, null);
                    if (settings is not null)
                    {
                        var outputPathProp = controllerSettingsType.GetProperty("OutputPath",
                            BindingFlags.Public | BindingFlags.Instance);
                        if (outputPathProp is not null)
                        {
                            outputPath = outputPathProp.GetValue(settings) as string ?? "";
                        }
                    }
                }

                return JsonUtility.ToJson(new RecorderResponse
                {
                    ok = true,
                    message = isRecording ? "recording in progress" : "idle",
                    state = isRecording ? "recording" : "idle",
                    outputPath = outputPath
                });
            }
            catch (Exception ex)
            {
                return JsonUtility.ToJson(new RecorderResponse
                {
                    ok = false,
                    message = $"recorder status failed: {ex.Message}"
                });
            }
        }

        // ── Helpers ──────────────────────────────────────────────────

        private static object FindRecorderSettings(Type settingsType)
        {
            // Search for ScriptableObject assets of the settings type
            var guids = UnityEditor.AssetDatabase.FindAssets($"t:{settingsType.Name}");
            if (guids is null || guids.Length == 0) return null;

            var path = UnityEditor.AssetDatabase.GUIDToAssetPath(guids[0]);
            return UnityEditor.AssetDatabase.LoadAssetAtPath(path, settingsType);
        }

        [Serializable]
        private sealed class RecorderResponse
        {
            public bool ok;
            public string message = string.Empty;
            public string state = string.Empty;
            public string outputPath = string.Empty;
        }
    }
}
#endif
