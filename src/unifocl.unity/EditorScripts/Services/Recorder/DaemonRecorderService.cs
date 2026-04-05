#if UNITY_EDITOR
using System;
using System.Collections.Generic;
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
        private const string RecorderWindowTypeName =
            "UnityEditor.Recorder.RecorderWindow, Unity.Recorder.Editor";
        private const string RecorderControllerTypeName =
            "UnityEditor.Recorder.RecorderController, Unity.Recorder.Editor";
        private const string RecorderControllerSettingsTypeName =
            "UnityEditor.Recorder.RecorderControllerSettings, Unity.Recorder.Editor";
        private const string RecorderSettingsTypeName =
            "UnityEditor.Recorder.RecorderSettings, Unity.Recorder.Editor";
        private const string MovieRecorderSettingsTypeName =
            "UnityEditor.Recorder.MovieRecorderSettings, Unity.Recorder.Editor";
        private const string ImageRecorderSettingsTypeName =
            "UnityEditor.Recorder.ImageRecorderSettings, Unity.Recorder.Editor";

        // ── recorder.start ───────────────────────────────────────────

        [UnifoclCommand(
            "recorder.start",
            "Start a Unity Recorder capture session. Pass {\"profile\":\"<name>\"} to " +
            "select a specific recorder profile; defaults to the currently active profile. " +
            "Returns an error if no recorder profiles are configured.",
            "recorder")]
        public static string StartRecording(string json)
        {
            try
            {
                var resolved = ResolveRecorderTypes();
                if (resolved.Error is not null) return resolved.Error;

                var settings = LoadControllerSettings(resolved.ControllerSettingsType!);
                if (settings is null)
                {
                    return ErrorResponse("No RecorderControllerSettings found. " +
                        "Open Window > General > Recorder > Recorder Window to create default settings.");
                }

                var recordersList = GetRecorderSettingsList(settings, resolved.ControllerSettingsType);
                if (recordersList is null || recordersList.Count == 0)
                {
                    return ErrorResponse("no recorder profiles configured — " +
                        "add at least one recorder in the Recorder window before starting");
                }

                // If a profile name is specified, enable only that profile
                var payload = SafeFromJson<StartPayload>(json);
                if (!string.IsNullOrWhiteSpace(payload.profile))
                {
                    var switched = SwitchToProfile(settings, resolved.ControllerSettingsType!,
                        resolved.RecorderSettingsType!, payload.profile);
                    if (!switched.ok) return JsonUtility.ToJson(switched);
                }

                // Verify at least one profile is enabled
                if (!HasEnabledRecorder(recordersList, resolved.RecorderSettingsType))
                {
                    return ErrorResponse("all recorder profiles are disabled — " +
                        "enable at least one profile or specify --profile <name>");
                }

                var controllerType = resolved.ControllerType!;
                var controller = Activator.CreateInstance(controllerType, new object[] { settings });
                controllerType.GetMethod("PrepareRecording")?.Invoke(controller, null);
                controllerType.GetMethod("StartRecording")?.Invoke(controller, null);

                var activeProfile = GetActiveProfileName(recordersList, resolved.RecorderSettingsType);
                return JsonUtility.ToJson(new RecorderResponse
                {
                    ok = true,
                    message = string.IsNullOrEmpty(activeProfile)
                        ? "recording started"
                        : $"recording started (profile: {activeProfile})",
                    state = "recording",
                    profile = activeProfile
                });
            }
            catch (Exception ex)
            {
                return ErrorResponse($"recorder start failed: {ex.Message}");
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
                var controllerType = Type.GetType(RecorderControllerTypeName);
                if (controllerType is null)
                    return PackageNotInstalledResponse();

                var windowType = Type.GetType(RecorderWindowTypeName);
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

                return ErrorResponse("no active recording session found");
            }
            catch (Exception ex)
            {
                return ErrorResponse($"recorder stop failed: {ex.Message}");
            }
        }

        // ── recorder.status ──────────────────────────────────────────

        [UnifoclCommand(
            "recorder.status",
            "Return the current Recorder state (recording/idle), the active profile name, " +
            "and the list of all configured profiles with their enabled state.",
            "recorder")]
        public static string GetStatus(string json)
        {
            try
            {
                var resolved = ResolveRecorderTypes();
                if (resolved.ControllerSettingsType is null)
                {
                    return JsonUtility.ToJson(new RecorderResponse
                    {
                        ok = true,
                        message = "Unity Recorder package not installed",
                        state = "unavailable"
                    });
                }

                var isRecording = false;
                var windowType = Type.GetType(RecorderWindowTypeName);
                if (windowType is not null)
                {
                    var windows = Resources.FindObjectsOfTypeAll(windowType);
                    if (windows is not null && windows.Length > 0)
                    {
                        var isRecordingProp = windowType.GetProperty("IsRecording",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (isRecordingProp is not null)
                            isRecording = (bool)isRecordingProp.GetValue(windows[0]);
                    }
                }

                var settings = LoadControllerSettings(resolved.ControllerSettingsType);
                var outputPath = "";
                var profiles = new List<ProfileEntry>();

                if (settings is not null)
                {
                    var outputPathProp = resolved.ControllerSettingsType.GetProperty("OutputPath",
                        BindingFlags.Public | BindingFlags.Instance);
                    if (outputPathProp is not null)
                        outputPath = outputPathProp.GetValue(settings) as string ?? "";

                    var recordersList = GetRecorderSettingsList(settings, resolved.ControllerSettingsType);
                    if (recordersList is not null && resolved.RecorderSettingsType is not null)
                    {
                        var enabledProp = resolved.RecorderSettingsType.GetProperty("Enabled",
                            BindingFlags.Public | BindingFlags.Instance);
                        foreach (var rec in recordersList)
                        {
                            if (rec is null) continue;
                            var name = rec.ToString();
                            var nameProp = rec.GetType().GetProperty("name",
                                BindingFlags.Public | BindingFlags.Instance);
                            if (nameProp is not null)
                                name = nameProp.GetValue(rec) as string ?? name;
                            var enabled = enabledProp is not null && (bool)enabledProp.GetValue(rec);
                            profiles.Add(new ProfileEntry
                            {
                                name = name,
                                type = rec.GetType().Name,
                                enabled = enabled
                            });
                        }
                    }
                }

                var profilesJson = JsonUtility.ToJson(new ProfileEntryList { profiles = profiles });
                return JsonUtility.ToJson(new RecorderResponse
                {
                    ok = true,
                    message = isRecording ? "recording in progress" : "idle",
                    state = isRecording ? "recording" : "idle",
                    outputPath = outputPath,
                    profile = GetActiveProfileName(
                        GetRecorderSettingsList(settings, resolved.ControllerSettingsType),
                        resolved.RecorderSettingsType),
                    content = profilesJson
                });
            }
            catch (Exception ex)
            {
                return ErrorResponse($"recorder status failed: {ex.Message}");
            }
        }

        // ── recorder.switch ──────────────────────────────────────────

        [UnifoclCommand(
            "recorder.switch",
            "Switch the active recorder profile by name. Enables the named profile " +
            "and disables all others. Pass {\"profile\":\"<name>\"}.",
            "recorder")]
        public static string SwitchProfile(string json)
        {
            try
            {
                var resolved = ResolveRecorderTypes();
                if (resolved.Error is not null) return resolved.Error;

                var payload = SafeFromJson<SwitchPayload>(json);
                if (string.IsNullOrWhiteSpace(payload.profile))
                    return ErrorResponse("recorder.switch requires 'profile' (profile name)");

                var settings = LoadControllerSettings(resolved.ControllerSettingsType!);
                if (settings is null)
                    return ErrorResponse("no RecorderControllerSettings found");

                var result = SwitchToProfile(settings, resolved.ControllerSettingsType!,
                    resolved.RecorderSettingsType!, payload.profile);
                return JsonUtility.ToJson(result);
            }
            catch (Exception ex)
            {
                return ErrorResponse($"recorder switch failed: {ex.Message}");
            }
        }

        // ── recorder.config ──────────────────────────────────────────

        [UnifoclCommand(
            "recorder.config",
            "Configure a recorder profile. Pass a JSON object with 'profile' (name) and " +
            "optional fields: 'outputFile' (output path without extension), " +
            "'captureFrameRate' (int, target FPS), 'capFrameRate' (bool, lock frame rate), " +
            "'imageWidth' (int), 'imageHeight' (int). " +
            "Only fields that are present will be updated.",
            "recorder")]
        public static string ConfigProfile(string json)
        {
            try
            {
                var resolved = ResolveRecorderTypes();
                if (resolved.Error is not null) return resolved.Error;

                var payload = SafeFromJson<ConfigPayload>(json);
                if (string.IsNullOrWhiteSpace(payload.profile))
                    return ErrorResponse("recorder.config requires 'profile' (profile name)");

                var settings = LoadControllerSettings(resolved.ControllerSettingsType!);
                if (settings is null)
                    return ErrorResponse("no RecorderControllerSettings found");

                var recordersList = GetRecorderSettingsList(settings, resolved.ControllerSettingsType);
                if (recordersList is null || recordersList.Count == 0)
                    return ErrorResponse("no recorder profiles configured");

                var target = FindProfileByName(recordersList, resolved.RecorderSettingsType!, payload.profile);
                if (target is null)
                    return ErrorResponse($"profile '{payload.profile}' not found");

                var targetType = target.GetType();
                var recorderSettingsType = resolved.RecorderSettingsType!;
                var applied = new List<string>();

                // outputFile — available on RecorderSettings base
                if (!string.IsNullOrWhiteSpace(payload.outputFile))
                {
                    var prop = recorderSettingsType.GetProperty("OutputFile",
                        BindingFlags.Public | BindingFlags.Instance);
                    if (prop is not null)
                    {
                        prop.SetValue(target, payload.outputFile);
                        applied.Add($"outputFile={payload.outputFile}");
                    }
                }

                // captureFrameRate — on RecorderSettings
                if (payload.captureFrameRate > 0)
                {
                    var prop = recorderSettingsType.GetProperty("FrameRate",
                        BindingFlags.Public | BindingFlags.Instance);
                    if (prop is not null)
                    {
                        prop.SetValue(target, (float)payload.captureFrameRate);
                        applied.Add($"captureFrameRate={payload.captureFrameRate}");
                    }
                }

                // capFrameRate — on RecorderSettings (lock frame rate to capture rate)
                if (payload.setCapFrameRate)
                {
                    var prop = recorderSettingsType.GetProperty("CapFrameRate",
                        BindingFlags.Public | BindingFlags.Instance);
                    if (prop is not null)
                    {
                        prop.SetValue(target, payload.capFrameRate);
                        applied.Add($"capFrameRate={payload.capFrameRate}");
                    }
                }

                // imageWidth / imageHeight — on ImageInputSettings (nested)
                if (payload.imageWidth > 0 || payload.imageHeight > 0)
                {
                    var imageInputProp = targetType.GetProperty("ImageInputSettings",
                        BindingFlags.Public | BindingFlags.Instance);
                    if (imageInputProp is not null)
                    {
                        var imageInput = imageInputProp.GetValue(target);
                        if (imageInput is not null)
                        {
                            var imageInputType = imageInput.GetType();
                            if (payload.imageWidth > 0)
                            {
                                var wProp = imageInputType.GetProperty("OutputWidth",
                                    BindingFlags.Public | BindingFlags.Instance);
                                if (wProp is not null)
                                {
                                    wProp.SetValue(imageInput, payload.imageWidth);
                                    applied.Add($"imageWidth={payload.imageWidth}");
                                }
                            }
                            if (payload.imageHeight > 0)
                            {
                                var hProp = imageInputType.GetProperty("OutputHeight",
                                    BindingFlags.Public | BindingFlags.Instance);
                                if (hProp is not null)
                                {
                                    hProp.SetValue(imageInput, payload.imageHeight);
                                    applied.Add($"imageHeight={payload.imageHeight}");
                                }
                            }
                        }
                    }
                    else if (payload.imageWidth > 0 || payload.imageHeight > 0)
                    {
                        applied.Add("imageWidth/imageHeight skipped (no ImageInputSettings on this profile type)");
                    }
                }

                if (applied.Count == 0)
                    return ErrorResponse("no configurable fields were provided or applied");

                // Mark dirty so Unity persists the change
                UnityEditor.EditorUtility.SetDirty(target as UnityEngine.Object);
                UnityEditor.AssetDatabase.SaveAssets();

                return JsonUtility.ToJson(new RecorderResponse
                {
                    ok = true,
                    message = $"profile '{payload.profile}' configured: {string.Join(", ", applied)}",
                    profile = payload.profile
                });
            }
            catch (Exception ex)
            {
                return ErrorResponse($"recorder config failed: {ex.Message}");
            }
        }

        // ── recorder.snapshot ────────────────────────────────────────

        [UnifoclCommand(
            "recorder.snapshot",
            "Capture a single screenshot frame using ScreenCapture. Pass {\"outputPath\":\"<path>\"} to " +
            "specify the output file (relative to project root, default: Captures/snapshot_<timestamp>.png). " +
            "Pass {\"superSize\":<n>} to multiply resolution (default: 1). " +
            "Does not require the com.unity.recorder package.",
            "recorder")]
        public static string TakeSnapshot(string json)
        {
            try
            {
                var payload = SafeFromJson<SnapshotPayload>(json);

                var outputPath = payload.outputPath;
                if (string.IsNullOrWhiteSpace(outputPath))
                {
                    var timestamp = System.DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    outputPath = $"Captures/snapshot_{timestamp}.png";
                }

                // Resolve to absolute path relative to project root
                var projectRoot = System.IO.Path.GetDirectoryName(UnityEngine.Application.dataPath) ?? "";
                var fullPath = System.IO.Path.IsPathRooted(outputPath)
                    ? outputPath
                    : System.IO.Path.Combine(projectRoot, outputPath);

                var dir = System.IO.Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrWhiteSpace(dir) && !System.IO.Directory.Exists(dir))
                    System.IO.Directory.CreateDirectory(dir);

                var superSize = payload.superSize > 0 ? payload.superSize : 1;
                UnityEngine.ScreenCapture.CaptureScreenshot(fullPath, superSize);

                return JsonUtility.ToJson(new RecorderResponse
                {
                    ok = true,
                    message = $"snapshot captured: {outputPath}",
                    outputPath = fullPath
                });
            }
            catch (Exception ex)
            {
                return ErrorResponse($"recorder snapshot failed: {ex.Message}");
            }
        }

        // ── Helpers ──────────────────────────────────────────────────

        private static ResolvedTypes ResolveRecorderTypes()
        {
            var controllerSettingsType = Type.GetType(RecorderControllerSettingsTypeName);
            var controllerType = Type.GetType(RecorderControllerTypeName);
            var recorderSettingsType = Type.GetType(RecorderSettingsTypeName);

            if (controllerSettingsType is null || controllerType is null)
            {
                return new ResolvedTypes
                {
                    Error = PackageNotInstalledResponse()
                };
            }

            return new ResolvedTypes
            {
                ControllerSettingsType = controllerSettingsType,
                ControllerType = controllerType,
                RecorderSettingsType = recorderSettingsType
            };
        }

        private static object? LoadControllerSettings(Type controllerSettingsType)
        {
            var getGlobal = controllerSettingsType.GetMethod("GetGlobalSettings",
                BindingFlags.Public | BindingFlags.Static);
            if (getGlobal is not null)
                return getGlobal.Invoke(null, null);

            return FindRecorderSettingsAsset(controllerSettingsType);
        }

        private static object? FindRecorderSettingsAsset(Type settingsType)
        {
            var guids = UnityEditor.AssetDatabase.FindAssets($"t:{settingsType.Name}");
            if (guids is null || guids.Length == 0) return null;

            var path = UnityEditor.AssetDatabase.GUIDToAssetPath(guids[0]);
            return UnityEditor.AssetDatabase.LoadAssetAtPath(path, settingsType);
        }

        private static System.Collections.IList? GetRecorderSettingsList(
            object? controllerSettings, Type? controllerSettingsType)
        {
            if (controllerSettings is null || controllerSettingsType is null) return null;

            var prop = controllerSettingsType.GetProperty("RecorderSettings",
                BindingFlags.Public | BindingFlags.Instance);
            return prop?.GetValue(controllerSettings) as System.Collections.IList;
        }

        private static bool HasEnabledRecorder(
            System.Collections.IList? recordersList, Type? recorderSettingsType)
        {
            if (recordersList is null || recorderSettingsType is null) return false;

            var enabledProp = recorderSettingsType.GetProperty("Enabled",
                BindingFlags.Public | BindingFlags.Instance);
            if (enabledProp is null) return recordersList.Count > 0;

            foreach (var rec in recordersList)
            {
                if (rec is not null && (bool)enabledProp.GetValue(rec))
                    return true;
            }
            return false;
        }

        private static string GetActiveProfileName(
            System.Collections.IList? recordersList, Type? recorderSettingsType)
        {
            if (recordersList is null || recorderSettingsType is null) return "";

            var enabledProp = recorderSettingsType.GetProperty("Enabled",
                BindingFlags.Public | BindingFlags.Instance);
            foreach (var rec in recordersList)
            {
                if (rec is null) continue;
                var enabled = enabledProp is null || (bool)enabledProp.GetValue(rec);
                if (!enabled) continue;

                var nameProp = rec.GetType().GetProperty("name",
                    BindingFlags.Public | BindingFlags.Instance);
                return nameProp?.GetValue(rec) as string ?? rec.GetType().Name;
            }
            return "";
        }

        private static object? FindProfileByName(
            System.Collections.IList? recordersList, Type recorderSettingsType, string profileName)
        {
            if (recordersList is null) return null;

            foreach (var rec in recordersList)
            {
                if (rec is null) continue;
                var nameProp = rec.GetType().GetProperty("name",
                    BindingFlags.Public | BindingFlags.Instance);
                var name = nameProp?.GetValue(rec) as string ?? "";

                if (name.Equals(profileName, StringComparison.OrdinalIgnoreCase))
                    return rec;
            }
            return null;
        }

        private static RecorderResponse SwitchToProfile(
            object controllerSettings, Type controllerSettingsType,
            Type recorderSettingsType, string profileName)
        {
            var recordersList = GetRecorderSettingsList(controllerSettings, controllerSettingsType);
            if (recordersList is null || recordersList.Count == 0)
            {
                return new RecorderResponse
                {
                    ok = false,
                    message = "no recorder profiles configured"
                };
            }

            if (recorderSettingsType is null)
            {
                return new RecorderResponse
                {
                    ok = false,
                    message = "RecorderSettings type not found"
                };
            }

            var enabledProp = recorderSettingsType.GetProperty("Enabled",
                BindingFlags.Public | BindingFlags.Instance);
            if (enabledProp is null)
            {
                return new RecorderResponse
                {
                    ok = false,
                    message = "Enabled property not found on RecorderSettings"
                };
            }

            var found = false;
            foreach (var rec in recordersList)
            {
                if (rec is null) continue;
                var nameProp = rec.GetType().GetProperty("name",
                    BindingFlags.Public | BindingFlags.Instance);
                var name = nameProp?.GetValue(rec) as string ?? "";

                if (name.Equals(profileName, StringComparison.OrdinalIgnoreCase))
                {
                    enabledProp.SetValue(rec, true);
                    found = true;
                }
                else
                {
                    enabledProp.SetValue(rec, false);
                }
            }

            if (!found)
            {
                // Re-enable all that were disabled — rollback
                foreach (var rec in recordersList)
                {
                    if (rec is not null) enabledProp.SetValue(rec, true);
                }

                return new RecorderResponse
                {
                    ok = false,
                    message = $"profile '{profileName}' not found"
                };
            }

            return new RecorderResponse
            {
                ok = true,
                message = $"switched to profile '{profileName}'",
                profile = profileName
            };
        }

        private static T SafeFromJson<T>(string json) where T : new()
        {
            if (string.IsNullOrWhiteSpace(json)) return new T();
            try { return JsonUtility.FromJson<T>(json); }
            catch { return new T(); }
        }

        private static string ErrorResponse(string message)
        {
            return JsonUtility.ToJson(new RecorderResponse
            {
                ok = false,
                message = message
            });
        }

        private static string PackageNotInstalledResponse()
        {
            return ErrorResponse("Unity Recorder package (com.unity.recorder) is not installed or not found");
        }

        // ── DTOs ─────────────────────────────────────────────────────

        private sealed class ResolvedTypes
        {
            public Type? ControllerSettingsType;
            public Type? ControllerType;
            public Type? RecorderSettingsType;
            public string? Error;
        }

        [Serializable]
        private sealed class SnapshotPayload
        {
            public string outputPath = string.Empty;
            public int superSize;
        }

        [Serializable]
        private sealed class StartPayload
        {
            public string profile = string.Empty;
        }

        [Serializable]
        private sealed class SwitchPayload
        {
            public string profile = string.Empty;
        }

        [Serializable]
        private sealed class ConfigPayload
        {
            public string profile = string.Empty;
            public string outputFile = string.Empty;
            public int captureFrameRate;
            public bool capFrameRate;
            public bool setCapFrameRate;
            public int imageWidth;
            public int imageHeight;
        }

        [Serializable]
        private sealed class RecorderResponse
        {
            public bool ok;
            public string message = string.Empty;
            public string state = string.Empty;
            public string outputPath = string.Empty;
            public string profile = string.Empty;
            public string content = string.Empty;
        }

        [Serializable]
        private sealed class ProfileEntry
        {
            public string name = string.Empty;
            public string type = string.Empty;
            public bool enabled;
        }

        [Serializable]
        private sealed class ProfileEntryList
        {
            public List<ProfileEntry> profiles = new();
        }
    }
}
#endif
