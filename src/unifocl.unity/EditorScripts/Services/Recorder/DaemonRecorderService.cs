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
                if (!UnityEditor.EditorApplication.isPlaying)
                    return ErrorResponse(
                        "recorder.start requires Play mode — enter Play mode before starting a recording session");

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
                if (!UnityEditor.EditorApplication.isPlaying)
                    return ErrorResponse(
                        "recorder.stop requires Play mode — no recording session is active outside Play mode");

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

                // ScreenCapture writes straight to disk — nothing the Undo-based
                // dry-run sandbox could revert, so skip the capture entirely.
                if (DaemonDryRunContext.IsActive)
                {
                    return JsonUtility.ToJson(new RecorderResponse
                    {
                        ok = true,
                        message = $"Dry-run: snapshot skipped (no file written): {outputPath}",
                        outputPath = fullPath
                    });
                }

                var dir = System.IO.Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrWhiteSpace(dir) && !System.IO.Directory.Exists(dir))
                    DaemonDryRunFileIo.CreateDirectory(dir);

                if (!UnityEditor.EditorApplication.isPlaying)
                {
                    return ErrorResponse(
                        "recorder.snapshot requires Play mode — enter Play mode before capturing a screenshot");
                }

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

        // ── recorder.sceneview ───────────────────────────────────────

        private const int SceneViewCaptureMaxDimension = 8192;

        [UnifoclCommand(
            "recorder.sceneview",
            "Capture the Scene View camera to a PNG. Works in Edit mode and Play mode. " +
            "outputPath: relative to project root (default: Captures/sceneview_<timestamp>.png). " +
            "width/height: pixels (default: current Scene View size). " +
            "frame: hierarchy path or 'selection' to fit that object without moving the user's Scene View. " +
            "Renders scene content only (no gizmos, grid, or overlays). " +
            "Does not require the com.unity.recorder package.",
            "recorder")]
        // Flat primitive params: DaemonCustomToolService binds tool arguments by parameter name.
        public static string CaptureSceneView(string outputPath = "", int width = 0, int height = 0, string frame = "")
        {
            try
            {
                var sceneView = UnityEditor.SceneView.lastActiveSceneView;
                if (sceneView == null && UnityEditor.SceneView.sceneViews.Count > 0)
                    sceneView = UnityEditor.SceneView.sceneViews[0] as UnityEditor.SceneView;
                if (sceneView == null || sceneView.camera == null)
                    return ErrorResponse("no Scene View is open — open Window > General > Scene first");

                var source = sceneView.camera;
                if (width <= 0) width = source.pixelWidth;
                if (height <= 0) height = source.pixelHeight;
                if (width <= 0 || height <= 0)
                {
                    width = 1280;
                    height = 720;
                }

                if (width > SceneViewCaptureMaxDimension || height > SceneViewCaptureMaxDimension)
                {
                    return ErrorResponse(
                        $"recorder.sceneview: width/height must be <= {SceneViewCaptureMaxDimension} (got {width}x{height})");
                }

                if (string.IsNullOrWhiteSpace(outputPath))
                {
                    var timestamp = System.DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    outputPath = $"Captures/sceneview_{timestamp}.png";
                }

                var projectRoot = System.IO.Path.GetDirectoryName(UnityEngine.Application.dataPath) ?? "";
                var fullPath = System.IO.Path.IsPathRooted(outputPath)
                    ? outputPath
                    : System.IO.Path.Combine(projectRoot, outputPath);

                GameObject? frameTarget = null;
                if (!string.IsNullOrWhiteSpace(frame))
                {
                    frameTarget = ResolveFrameTarget(frame);
                    if (frameTarget == null)
                        return ErrorResponse($"recorder.sceneview: frame target not found: {frame}");
                }

                if (DaemonDryRunContext.IsActive)
                {
                    return JsonUtility.ToJson(new SceneViewCaptureResponse
                    {
                        ok = true,
                        message = $"Dry-run: scene view capture skipped (no file written): {outputPath}",
                        outputPath = fullPath,
                        width = width,
                        height = height,
                        framed = frameTarget != null ? frameTarget.name : string.Empty
                    });
                }

                var png = RenderSceneViewPng(source, width, height, frameTarget);

                var dir = System.IO.Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrWhiteSpace(dir) && !System.IO.Directory.Exists(dir))
                    DaemonDryRunFileIo.CreateDirectory(dir);
                DaemonDryRunFileIo.WriteAllBytes(fullPath, png);

                return JsonUtility.ToJson(new SceneViewCaptureResponse
                {
                    ok = true,
                    message = $"scene view captured: {outputPath} ({width}x{height})",
                    outputPath = fullPath,
                    width = width,
                    height = height,
                    framed = frameTarget != null ? frameTarget.name : string.Empty
                });
            }
            catch (Exception ex)
            {
                return ErrorResponse($"recorder sceneview failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Renders through a hidden clone of the Scene View camera so framing never moves
        /// the user's view and the Scene View's own render target is left untouched.
        /// </summary>
        private static byte[] RenderSceneViewPng(Camera source, int width, int height, GameObject? frameTarget)
        {
            var cameraObject = new GameObject("unifocl_SceneViewCapture")
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            RenderTexture? renderTexture = null;
            Texture2D? texture = null;
            var previousActive = RenderTexture.active;

            try
            {
                var camera = cameraObject.AddComponent<Camera>();
                camera.CopyFrom(source);
                camera.enabled = false;
                camera.aspect = (float)width / height;

                if (frameTarget != null)
                    FrameCamera(camera, frameTarget);

                renderTexture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
                {
                    antiAliasing = Mathf.Max(1, QualitySettings.antiAliasing)
                };
                camera.targetTexture = renderTexture;
                camera.Render();

                RenderTexture.active = renderTexture;
                texture = new Texture2D(width, height, TextureFormat.RGB24, false);
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                texture.Apply();
                camera.targetTexture = null;
                return texture.EncodeToPNG();
            }
            finally
            {
                RenderTexture.active = previousActive;
                if (renderTexture != null) UnityEngine.Object.DestroyImmediate(renderTexture);
                if (texture != null) UnityEngine.Object.DestroyImmediate(texture);
                UnityEngine.Object.DestroyImmediate(cameraObject);
            }
        }

        private static GameObject? ResolveFrameTarget(string frame)
        {
            var trimmed = frame.Trim();
            if (trimmed.Equals("selection", StringComparison.OrdinalIgnoreCase))
                return UnityEditor.Selection.activeGameObject;

            // GameObject.Find accepts '/'-separated hierarchy paths; a leading '/' anchors at a root.
            return GameObject.Find(trimmed);
        }

        /// <summary>
        /// Keeps the camera's current orientation, centres it on the target's bounds, backs it
        /// off along its forward axis until every bounds corner fits inside the frustum, and moves
        /// the near clip plane up to the bounds' front face.
        /// </summary>
        private static void FrameCamera(Camera camera, GameObject target)
        {
            const float margin = 1.1f;
            var bounds = CalculateWorldBounds(target);
            var transform = camera.transform;
            var right = transform.right;
            var up = transform.up;
            var forward = transform.forward;

            var maxX = 0.01f;
            var maxY = 0.01f;
            var maxDistance = 0.01f;
            var minDepth = 0f;
            var extents = bounds.extents;
            for (var i = 0; i < 8; i++)
            {
                var offset = new Vector3(
                    (i & 1) == 0 ? -extents.x : extents.x,
                    (i & 2) == 0 ? -extents.y : extents.y,
                    (i & 4) == 0 ? -extents.z : extents.z);
                var x = Mathf.Abs(Vector3.Dot(offset, right)) * margin;
                var y = Mathf.Abs(Vector3.Dot(offset, up)) * margin;
                var z = Vector3.Dot(offset, forward);
                maxX = Mathf.Max(maxX, x);
                maxY = Mathf.Max(maxY, y);
                minDepth = Mathf.Min(minDepth, z);

                if (!camera.orthographic)
                {
                    var tanV = Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad);
                    var tanH = tanV * camera.aspect;
                    maxDistance = Mathf.Max(maxDistance, x / tanH - z, y / tanV - z);
                }
            }

            if (camera.orthographic)
            {
                camera.orthographicSize = Mathf.Max(maxY, maxX / camera.aspect);
                maxDistance = -minDepth + extents.magnitude;
            }

            maxDistance = Mathf.Max(maxDistance, -minDepth + camera.nearClipPlane);
            transform.position = bounds.center - forward * maxDistance;

            // Clip everything in front of the target's bounds so foreground geometry
            // between the Scene View angle and the target cannot hide it.
            var radius = Mathf.Max(extents.magnitude, 0.01f);
            var frontDepth = maxDistance + minDepth;
            camera.nearClipPlane = Mathf.Max(frontDepth - radius * 0.05f, 0.001f);
            camera.farClipPlane = Mathf.Max(camera.farClipPlane, maxDistance + radius * 2f);
        }

        private static Bounds CalculateWorldBounds(GameObject target)
        {
            var hasBounds = false;
            var bounds = new Bounds(target.transform.position, Vector3.zero);

            foreach (var renderer in target.GetComponentsInChildren<Renderer>())
            {
                if (!renderer.enabled || renderer is ParticleSystemRenderer) continue;
                if (!hasBounds) { bounds = renderer.bounds; hasBounds = true; }
                else bounds.Encapsulate(renderer.bounds);
            }

            if (!hasBounds)
            {
                foreach (var rectTransform in target.GetComponentsInChildren<RectTransform>())
                {
                    var corners = new Vector3[4];
                    rectTransform.GetWorldCorners(corners);
                    foreach (var corner in corners)
                    {
                        if (!hasBounds) { bounds = new Bounds(corner, Vector3.zero); hasBounds = true; }
                        else bounds.Encapsulate(corner);
                    }
                }
            }

            if (!hasBounds)
                bounds = new Bounds(target.transform.position, Vector3.one);

            return bounds;
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
        private sealed class SceneViewCaptureResponse
        {
            public bool ok;
            public string message = string.Empty;
            public string outputPath = string.Empty;
            public int width;
            public int height;
            public string framed = string.Empty;
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
