#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.SceneManagement;

namespace UniFocl.EditorBridge.Timeline
{
    /// <summary>
    /// Lazy-loaded category service for Unity Timeline authoring.
    /// Uses reflection to access <c>UnityEngine.Timeline</c> APIs so the package
    /// is not a hard dependency — commands return a descriptive error when
    /// <c>com.unity.timeline</c> is not installed.
    /// </summary>
    internal static class DaemonTimelineService
    {
        // ── type name constants ──────────────────────────────────────────────

        private const string TimelineAssetTypeName         = "UnityEngine.Timeline.TimelineAsset, Unity.Timeline";
        private const string AnimationTrackTypeName        = "UnityEngine.Timeline.AnimationTrack, Unity.Timeline";
        private const string AudioTrackTypeName            = "UnityEngine.Timeline.AudioTrack, Unity.Timeline";
        private const string ActivationTrackTypeName       = "UnityEngine.Timeline.ActivationTrack, Unity.Timeline";
        private const string ControlTrackTypeName          = "UnityEngine.Timeline.ControlTrack, Unity.Timeline";
        private const string GroupTrackTypeName            = "UnityEngine.Timeline.GroupTrack, Unity.Timeline";
        private const string MarkerTrackTypeName           = "UnityEngine.Timeline.MarkerTrack, Unity.Timeline";
        private const string SignalEmitterTypeName         = "UnityEngine.Timeline.SignalEmitter, Unity.Timeline";
        private const string SignalAssetTypeName           = "UnityEngine.Timeline.SignalAsset, Unity.Timeline";

        // ── timeline.track.add ───────────────────────────────────────────────

        [UnifoclCommand(
            "timeline.track.add",
            "Add a track to a TimelineAsset. " +
            "Required: {\"assetPath\":\"Assets/T.playable\",\"type\":\"animation\",\"name\":\"My Track\"}. " +
            "Track types: animation|audio|activation|control|group. " +
            "Returns the new track id and name.",
            "timeline")]
        public static string AddTrack(string json)
        {
            try
            {
                var payload = SafeFromJson<AddTrackPayload>(json);
                if (string.IsNullOrWhiteSpace(payload.assetPath))
                    return ErrorResponse("assetPath is required");
                if (string.IsNullOrWhiteSpace(payload.type))
                    return ErrorResponse("type is required (animation|audio|activation|control|group)");

                var timelineType = ResolveTimelineType(TimelineAssetTypeName);
                if (timelineType is null)
                    return PackageNotInstalledResponse();

                var timeline = AssetDatabase.LoadAssetAtPath(payload.assetPath, timelineType);
                if (timeline is null)
                    return ErrorResponse($"TimelineAsset not found at '{payload.assetPath}'");

                var trackType = ResolveTrackType(payload.type);
                if (trackType is null)
                    return ErrorResponse($"unknown track type '{payload.type}'; use animation|audio|activation|control|group");

                var trackName = string.IsNullOrWhiteSpace(payload.name)
                    ? payload.type + " Track"
                    : payload.name;

                // Find CreateTrack(Type, TrackAsset-or-null, string) via reflection to avoid
                // hard dependency on UnityEngine.Timeline.TrackAsset at compile time.
                var createTrackMethod = FindCreateTrackMethod(timelineType);
                if (createTrackMethod is null)
                    return ErrorResponse("failed to find CreateTrack on TimelineAsset — is com.unity.timeline installed?");

                Undo.RecordObject(timeline, "unifocl timeline.track.add");

                var paramCount = createTrackMethod.GetParameters().Length;
                var track = paramCount >= 3
                    ? createTrackMethod.Invoke(timeline, new object?[] { trackType, null, trackName }) as UnityEngine.Object
                    : createTrackMethod.Invoke(timeline, new object?[] { trackType, trackName }) as UnityEngine.Object;

                if (track is null)
                    return ErrorResponse("CreateTrack returned null — track may not be supported in this Timeline version");

                EditorUtility.SetDirty(timeline);
                AssetDatabase.SaveAssets();

                return OkResponse(
                    $"created {payload.type} track '{trackName}'",
                    $"{{\"id\":{track.GetInstanceID()},\"name\":\"{EscapeJson(trackName)}\",\"type\":\"{payload.type}\"}}");
            }
            catch (Exception ex)
            {
                return ErrorResponse($"timeline.track.add failed: {ex.Message}");
            }
        }

        // ── timeline.clip.add ────────────────────────────────────────────────

        [UnifoclCommand(
            "timeline.clip.add",
            "Add a clip to a named track on a TimelineAsset using semantic placement. " +
            "Required: {\"assetPath\":\"...\",\"trackName\":\"...\",\"clipName\":\"...\",\"placement\":{\"directive\":\"end\"}}. " +
            "Placement directives: start|end|after|with|at. " +
            "For after/with, add \"ref\":\"OtherClipName\". For at, add \"time\":1.5. " +
            "Optional: \"duration\" (default 1.0).",
            "timeline")]
        public static string AddClip(string json)
        {
            try
            {
                var payload = SafeFromJson<AddClipPayload>(json);
                if (string.IsNullOrWhiteSpace(payload.assetPath))
                    return ErrorResponse("assetPath is required");
                if (string.IsNullOrWhiteSpace(payload.trackName))
                    return ErrorResponse("trackName is required");
                if (string.IsNullOrWhiteSpace(payload.clipName))
                    return ErrorResponse("clipName is required");

                var timelineType = ResolveTimelineType(TimelineAssetTypeName);
                if (timelineType is null)
                    return PackageNotInstalledResponse();

                var timeline = AssetDatabase.LoadAssetAtPath(payload.assetPath, timelineType);
                if (timeline is null)
                    return ErrorResponse($"TimelineAsset not found at '{payload.assetPath}'");

                var track = FindTrackByName(timeline, timelineType, payload.trackName);
                if (track is null)
                    return ErrorResponse($"track '{payload.trackName}' not found on '{payload.assetPath}'");

                var clipInfos = GetClipInfos(track);
                var startTime = ResolveStartTime(payload.placement, clipInfos);
                var duration  = payload.duration > 0.0 ? payload.duration : 1.0;

                var createClipMethod = track.GetType().GetMethod("CreateDefaultClip")
                    ?? track.GetType().GetMethod("CreateClip");
                if (createClipMethod is null)
                    return ErrorResponse("failed to find CreateDefaultClip on track — is com.unity.timeline installed?");

                Undo.RecordObject(timeline, "unifocl timeline.clip.add");
                Undo.RecordObject(track, "unifocl timeline.clip.add");

                var clip = createClipMethod.Invoke(track, null);
                if (clip is null)
                    return ErrorResponse("CreateDefaultClip returned null");

                var clipType = clip.GetType();
                SetProperty(clipType, clip, "start",       (object)startTime);
                SetProperty(clipType, clip, "duration",    (object)duration);
                SetProperty(clipType, clip, "displayName", (object)payload.clipName);

                EditorUtility.SetDirty(timeline);
                AssetDatabase.SaveAssets();

                return OkResponse(
                    $"created clip '{payload.clipName}' at {startTime:0.###}s on track '{payload.trackName}'",
                    $"{{\"name\":\"{EscapeJson(payload.clipName)}\",\"start\":{startTime},\"duration\":{duration}}}");
            }
            catch (Exception ex)
            {
                return ErrorResponse($"timeline.clip.add failed: {ex.Message}");
            }
        }

        // ── timeline.clip.ease ───────────────────────────────────────────────

        [UnifoclCommand(
            "timeline.clip.ease",
            "Apply CSS-style easing to a timeline clip's mix-in or mix-out blend curves. " +
            "Required: {\"assetPath\":\"...\",\"trackName\":\"...\",\"clipName\":\"...\"}. " +
            "Optional: \"mixIn\" and/or \"mixOut\" — values: linear|ease-in|ease-out|ease-in-out|step.",
            "timeline")]
        public static string EaseClip(string json)
        {
            try
            {
                var payload = SafeFromJson<EasePayload>(json);
                if (string.IsNullOrWhiteSpace(payload.assetPath)
                    || string.IsNullOrWhiteSpace(payload.trackName)
                    || string.IsNullOrWhiteSpace(payload.clipName))
                    return ErrorResponse("assetPath, trackName, and clipName are required");

                var timelineType = ResolveTimelineType(TimelineAssetTypeName);
                if (timelineType is null)
                    return PackageNotInstalledResponse();

                var timeline = AssetDatabase.LoadAssetAtPath(payload.assetPath, timelineType);
                if (timeline is null)
                    return ErrorResponse($"TimelineAsset not found at '{payload.assetPath}'");

                var track = FindTrackByName(timeline, timelineType, payload.trackName);
                if (track is null)
                    return ErrorResponse($"track '{payload.trackName}' not found");

                var clip = FindClipByName(track, payload.clipName);
                if (clip is null)
                    return ErrorResponse($"clip '{payload.clipName}' not found on track '{payload.trackName}'");

                if (string.IsNullOrWhiteSpace(payload.mixIn) && string.IsNullOrWhiteSpace(payload.mixOut))
                    return ErrorResponse("at least one of mixIn or mixOut must be specified");

                Undo.RecordObject(timeline, "unifocl timeline.clip.ease");

                var clipType = clip.GetType();
                var applied  = new List<string>();

                if (!string.IsNullOrWhiteSpace(payload.mixIn))
                {
                    SetProperty(clipType, clip, "mixInCurve", ResolveCssEasingCurve(payload.mixIn));
                    applied.Add($"mixIn={payload.mixIn}");
                }

                if (!string.IsNullOrWhiteSpace(payload.mixOut))
                {
                    SetProperty(clipType, clip, "mixOutCurve", ResolveCssEasingCurve(payload.mixOut));
                    applied.Add($"mixOut={payload.mixOut}");
                }

                EditorUtility.SetDirty(timeline);
                AssetDatabase.SaveAssets();

                return OkResponse(
                    $"applied easing ({string.Join(", ", applied)}) to clip '{payload.clipName}'",
                    null);
            }
            catch (Exception ex)
            {
                return ErrorResponse($"timeline.clip.ease failed: {ex.Message}");
            }
        }

        // ── timeline.clip.preset ─────────────────────────────────────────────

        [UnifoclCommand(
            "timeline.clip.preset",
            "Assign a procedural motion preset AnimationClip to an animation timeline clip. " +
            "Required: {\"assetPath\":\"...\",\"trackName\":\"...\",\"clipName\":\"...\",\"preset\":\"scale-in\"}. " +
            "Presets: scale-in|scale-out|fade-in|fade-out|bounce-in. " +
            "Generated clips are cached at Assets/.unifocl/Presets/ for reuse across tracks.",
            "timeline")]
        public static string AssignPreset(string json)
        {
            try
            {
                var payload = SafeFromJson<PresetPayload>(json);
                if (string.IsNullOrWhiteSpace(payload.assetPath)
                    || string.IsNullOrWhiteSpace(payload.trackName)
                    || string.IsNullOrWhiteSpace(payload.clipName)
                    || string.IsNullOrWhiteSpace(payload.preset))
                    return ErrorResponse("assetPath, trackName, clipName, and preset are required");

                var presetPath = $"Assets/.unifocl/Presets/{payload.preset}.anim";

                // Dry-run: return a topology preview without creating any assets (REQ-3.4).
                if (DaemonDryRunContext.IsActive)
                {
                    var alreadyExists = AssetDatabase.LoadAssetAtPath<AnimationClip>(presetPath) is not null;
                    return OkResponse(
                        $"dry-run: would assign preset '{payload.preset}' to clip '{payload.clipName}'",
                        $"{{\"presetPath\":\"{EscapeJson(presetPath)}\",\"presetExists\":{(alreadyExists ? "true" : "false")}}}");
                }

                var timelineType = ResolveTimelineType(TimelineAssetTypeName);
                if (timelineType is null)
                    return PackageNotInstalledResponse();

                var timeline = AssetDatabase.LoadAssetAtPath(payload.assetPath, timelineType);
                if (timeline is null)
                    return ErrorResponse($"TimelineAsset not found at '{payload.assetPath}'");

                var track = FindTrackByName(timeline, timelineType, payload.trackName);
                if (track is null)
                    return ErrorResponse($"track '{payload.trackName}' not found");

                var clip = FindClipByName(track, payload.clipName);
                if (clip is null)
                    return ErrorResponse($"clip '{payload.clipName}' not found on track '{payload.trackName}'");

                // Reuse existing preset clip or generate a new one.
                var presetClip = AssetDatabase.LoadAssetAtPath<AnimationClip>(presetPath);
                if (presetClip is null)
                {
                    EnsurePresetDirectory();
                    presetClip = BuildPresetClip(payload.preset);
                    AssetDatabase.CreateAsset(presetClip, presetPath);
                    AssetDatabase.SaveAssets();
                }

                // Assign to AnimationPlayableAsset.clip via reflection.
                var clipType    = clip.GetType();
                var assetProp   = clipType.GetProperty("asset");
                var playableAsset = assetProp?.GetValue(clip);
                var assigned    = false;

                if (playableAsset is not null)
                {
                    var animPlayableType = playableAsset.GetType();
                    var clipProp         = animPlayableType.GetProperty("clip");

                    if (clipProp is not null && clipProp.CanWrite)
                    {
                        Undo.RecordObject(timeline, "unifocl timeline.clip.preset");
                        if (playableAsset is UnityEngine.Object uObj)
                            Undo.RecordObject(uObj, "unifocl timeline.clip.preset");

                        clipProp.SetValue(playableAsset, presetClip);

                        if (playableAsset is UnityEngine.Object uObj2)
                            EditorUtility.SetDirty(uObj2);
                        EditorUtility.SetDirty(timeline);
                        AssetDatabase.SaveAssets();
                        assigned = true;
                    }
                }

                if (!assigned)
                    return ErrorResponse("could not assign preset — track may not be an AnimationTrack or clip has no AnimationPlayableAsset");

                return OkResponse(
                    $"assigned preset '{payload.preset}' to clip '{payload.clipName}'",
                    $"{{\"presetPath\":\"{EscapeJson(presetPath)}\"}}");
            }
            catch (Exception ex)
            {
                return ErrorResponse($"timeline.clip.preset failed: {ex.Message}");
            }
        }

        // ── timeline.bind ────────────────────────────────────────────────────

        [UnifoclCommand(
            "timeline.bind",
            "Bind a track on a PlayableDirector to a scene GameObject or Component. " +
            "Required: {\"directorPath\":\"Director\",\"trackName\":\"My Track\",\"targetScenePath\":\"Player\"}. " +
            "AnimationTracks automatically bind to the Animator component when one is present.",
            "timeline")]
        public static string BindTrack(string json)
        {
            try
            {
                var payload = SafeFromJson<BindPayload>(json);
                if (string.IsNullOrWhiteSpace(payload.directorPath))
                    return ErrorResponse("directorPath is required");
                if (string.IsNullOrWhiteSpace(payload.trackName))
                    return ErrorResponse("trackName is required");
                if (string.IsNullOrWhiteSpace(payload.targetScenePath))
                    return ErrorResponse("targetScenePath is required");

                var directorGo = GameObject.Find(payload.directorPath);
                if (directorGo is null)
                    return ErrorResponse($"E_NOT_FOUND: GameObject '{payload.directorPath}' not found in scene");

                var director = directorGo.GetComponent<PlayableDirector>();
                if (director is null)
                    return ErrorResponse($"E_NOT_FOUND: no PlayableDirector on '{payload.directorPath}'");

                if (director.playableAsset is null)
                    return ErrorResponse($"E_NOT_FOUND: PlayableDirector on '{payload.directorPath}' has no playableAsset assigned");

                var timelineType = ResolveTimelineType(TimelineAssetTypeName);
                if (timelineType is null)
                    return PackageNotInstalledResponse();

                var playableAssetObj = director.playableAsset;
                if (playableAssetObj is null || !timelineType.IsInstanceOfType(playableAssetObj))
                    return ErrorResponse("playableAsset is not a TimelineAsset");

                var track = FindTrackByName((UnityEngine.Object)playableAssetObj, timelineType, payload.trackName);
                if (track is null)
                    return ErrorResponse($"E_NOT_FOUND: track '{payload.trackName}' not found on the director's timeline");

                var targetObj = GameObject.Find(payload.targetScenePath);
                if (targetObj is null)
                    return ErrorResponse($"E_NOT_FOUND: target '{payload.targetScenePath}' not found in scene");

                Undo.RecordObject(director, "unifocl timeline.bind");

                // Auto-resolve Animator component for AnimationTracks (REQ-3.2 auto-resolve).
                UnityEngine.Object bindTarget = targetObj;
                var animTrackType = ResolveTimelineType(AnimationTrackTypeName);
                if (animTrackType is not null && animTrackType.IsInstanceOfType(track))
                {
                    var animator = targetObj.GetComponent<Animator>();
                    if (animator is not null)
                        bindTarget = animator;
                }

                // Fulfils REQ-3.2: SetGenericBinding correctly serialises the scene reference.
                director.SetGenericBinding(track, bindTarget);

                EditorUtility.SetDirty(director);
                DaemonScenePersistenceService.PersistMutationScenes("timeline.bind", directorGo.scene);

                return OkResponse(
                    $"bound track '{payload.trackName}' to '{targetObj.name}'",
                    $"{{\"bound\":\"{EscapeJson(payload.trackName)}\",\"to\":\"{EscapeJson(targetObj.name)}\"}}");
            }
            catch (Exception ex)
            {
                return ErrorResponse($"timeline.bind failed: {ex.Message}");
            }
        }

        // ── timeline.marker.add ─────────────────────────────────────────────

        [UnifoclCommand(
            "timeline.marker.add",
            "Add a SignalEmitter marker to a TimelineAsset's marker track. " +
            "Required: {\"assetPath\":\"Assets/T.playable\",\"time\":0.5}. " +
            "Optional: \"signal\" — asset path to a SignalAsset (e.g. \"Assets/Signals/Cue.signal\"). " +
            "Returns the marker time and assigned signal.",
            "timeline")]
        public static string AddMarker(string json)
        {
            try
            {
                var payload = SafeFromJson<AddMarkerPayload>(json);
                if (string.IsNullOrWhiteSpace(payload.assetPath))
                    return ErrorResponse("assetPath is required");

                var timelineType = ResolveTimelineType(TimelineAssetTypeName);
                if (timelineType is null)
                    return PackageNotInstalledResponse();

                var signalEmitterType = ResolveTimelineType(SignalEmitterTypeName);
                if (signalEmitterType is null)
                    return ErrorResponse("SignalEmitter type not found — is com.unity.timeline installed?");

                var timeline = AssetDatabase.LoadAssetAtPath(payload.assetPath, timelineType);
                if (timeline is null)
                    return ErrorResponse($"TimelineAsset not found at '{payload.assetPath}'");

                // Access the markerTrack property (creates one if absent).
                var markerTrackProp = timelineType.GetProperty("markerTrack");
                if (markerTrackProp is null)
                    return ErrorResponse("markerTrack property not found on TimelineAsset");

                var markerTrack = markerTrackProp.GetValue(timeline) as UnityEngine.Object;
                if (markerTrack is null)
                    return ErrorResponse("failed to get or create marker track");

                // Find IMarker CreateMarker(Type, double) on the track.
                var createMarkerMethod = markerTrack.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
                    .FirstOrDefault(m =>
                        m.Name == "CreateMarker"
                        && !m.IsGenericMethod
                        && m.GetParameters().Length == 2
                        && m.GetParameters()[0].ParameterType == typeof(Type)
                        && m.GetParameters()[1].ParameterType == typeof(double));

                if (createMarkerMethod is null)
                    return ErrorResponse("CreateMarker(Type, double) not found on marker track");

                Undo.RecordObject(timeline, "unifocl timeline.marker.add");
                Undo.RecordObject(markerTrack, "unifocl timeline.marker.add");

                var marker = createMarkerMethod.Invoke(markerTrack, new object[] { signalEmitterType, payload.time });
                if (marker is null)
                    return ErrorResponse("CreateMarker returned null");

                // If a SignalAsset path is provided, assign it to the emitter's asset property.
                string? assignedSignal = null;
                if (!string.IsNullOrWhiteSpace(payload.signal))
                {
                    var signalAssetType = ResolveTimelineType(SignalAssetTypeName);
                    if (signalAssetType is null)
                        return ErrorResponse("SignalAsset type not found");

                    var signalAsset = AssetDatabase.LoadAssetAtPath(payload.signal, signalAssetType);
                    if (signalAsset is null)
                        return ErrorResponse($"SignalAsset not found at '{payload.signal}'");

                    var assetProp = marker.GetType().GetProperty("asset");
                    if (assetProp is not null && assetProp.CanWrite)
                    {
                        if (marker is UnityEngine.Object markerObj)
                            Undo.RecordObject(markerObj, "unifocl timeline.marker.add");
                        assetProp.SetValue(marker, signalAsset);
                        assignedSignal = payload.signal;
                    }
                }

                EditorUtility.SetDirty(timeline);
                EditorUtility.SetDirty(markerTrack);
                AssetDatabase.SaveAssets();

                var signalJson = assignedSignal is not null
                    ? $",\"signal\":\"{EscapeJson(assignedSignal)}\""
                    : "";
                return OkResponse(
                    $"added SignalEmitter marker at {payload.time:0.###}s" +
                    (assignedSignal is not null ? $" with signal '{assignedSignal}'" : ""),
                    $"{{\"time\":{payload.time}{signalJson}}}");
            }
            catch (Exception ex)
            {
                return ErrorResponse($"timeline.marker.add failed: {ex.Message}");
            }
        }

        // ── reflection helpers ───────────────────────────────────────────────

        private static readonly Dictionary<string, Type?> _typeCache = new();

        private static Type? ResolveTimelineType(string qualifiedName)
        {
            if (_typeCache.TryGetValue(qualifiedName, out var cached)) return cached;
            var t = Type.GetType(qualifiedName);
            if (t is null)
            {
                var shortName = qualifiedName.Split(',')[0].Trim();
                t = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => a.GetType(shortName, throwOnError: false))
                    .FirstOrDefault(x => x is not null);
            }
            _typeCache[qualifiedName] = t;
            return t;
        }

        private static Type? ResolveTrackType(string alias)
        {
            return alias.ToLowerInvariant() switch
            {
                "animation"  => ResolveTimelineType(AnimationTrackTypeName),
                "audio"      => ResolveTimelineType(AudioTrackTypeName),
                "activation" => ResolveTimelineType(ActivationTrackTypeName),
                "control"    => ResolveTimelineType(ControlTrackTypeName),
                "group"      => ResolveTimelineType(GroupTrackTypeName),
                _            => null
            };
        }

        /// <summary>Finds the non-generic CreateTrack method that accepts a System.Type first arg.</summary>
        private static MethodInfo? FindCreateTrackMethod(Type timelineType)
        {
            return timelineType.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .FirstOrDefault(m =>
                    m.Name == "CreateTrack"
                    && !m.IsGenericMethod
                    && m.GetParameters().Length >= 1
                    && m.GetParameters()[0].ParameterType == typeof(Type));
        }

        private static UnityEngine.Object? FindTrackByName(
            UnityEngine.Object timeline, Type timelineType, string name)
        {
            var getTracksMethod = timelineType.GetMethod("GetOutputTracks");
            if (getTracksMethod is null) return null;
            var tracks = getTracksMethod.Invoke(timeline, null) as IEnumerable;
            if (tracks is null) return null;
            foreach (var t in tracks)
            {
                if (t is UnityEngine.Object obj && obj.name == name)
                    return obj;
            }
            return null;
        }

        private static object? FindClipByName(UnityEngine.Object track, string name)
        {
            var getClipsMethod = track.GetType().GetMethod("GetClips");
            if (getClipsMethod is null) return null;
            var clips = getClipsMethod.Invoke(track, null) as IEnumerable;
            if (clips is null) return null;
            foreach (var c in clips)
            {
                if (c is null) continue;
                var displayName = c.GetType().GetProperty("displayName")?.GetValue(c) as string;
                if (displayName == name)
                    return c;
            }
            return null;
        }

        private static List<(string name, double start, double duration)> GetClipInfos(UnityEngine.Object track)
        {
            var result = new List<(string, double, double)>();
            var getClipsMethod = track.GetType().GetMethod("GetClips");
            if (getClipsMethod is null) return result;
            var clips = getClipsMethod.Invoke(track, null) as IEnumerable;
            if (clips is null) return result;
            foreach (var c in clips)
            {
                if (c is null) continue;
                var ct           = c.GetType();
                var displayName  = ct.GetProperty("displayName")?.GetValue(c) as string ?? string.Empty;
                var start        = GetDoubleProperty(ct, c, "start");
                var dur          = GetDoubleProperty(ct, c, "duration");
                result.Add((displayName, start, dur));
            }
            return result;
        }

        private static double ResolveStartTime(
            PlacementPayload placement,
            List<(string name, double start, double duration)> clips)
        {
            switch (placement.directive.ToLowerInvariant())
            {
                case "start":
                    return 0.0;

                case "end":
                case "":
                    return clips.Count == 0 ? 0.0 : clips.Max(c => c.start + c.duration);

                case "after":
                {
                    var match = clips.FirstOrDefault(c =>
                        string.Equals(c.name, placement.reference, StringComparison.Ordinal));
                    return match.name is not null
                        ? match.start + match.duration
                        : clips.Count == 0 ? 0.0 : clips.Max(c => c.start + c.duration);
                }

                case "with":
                {
                    var match = clips.FirstOrDefault(c =>
                        string.Equals(c.name, placement.reference, StringComparison.Ordinal));
                    return match.name is not null ? match.start : 0.0;
                }

                case "at":
                    return placement.time;

                default:
                    return clips.Count == 0 ? 0.0 : clips.Max(c => c.start + c.duration);
            }
        }

        private static void SetProperty(Type type, object obj, string name, object value)
        {
            var prop = type.GetProperty(name);
            if (prop is not null && prop.CanWrite)
            {
                prop.SetValue(obj, value);
                return;
            }
            type.GetField(name)?.SetValue(obj, value);
        }

        private static double GetDoubleProperty(Type type, object obj, string name)
        {
            var val = type.GetProperty(name)?.GetValue(obj);
            return val switch
            {
                double d => d,
                float  f => f,
                _        => 0.0
            };
        }

        // ── preset builders ──────────────────────────────────────────────────

        private static void EnsurePresetDirectory()
        {
            if (!AssetDatabase.IsValidFolder("Assets/.unifocl"))
                AssetDatabase.CreateFolder("Assets", ".unifocl");
            if (!AssetDatabase.IsValidFolder("Assets/.unifocl/Presets"))
                AssetDatabase.CreateFolder("Assets/.unifocl", "Presets");
        }

        private static AnimationClip BuildPresetClip(string preset)
        {
            var clip = new AnimationClip { name = preset };

            switch (preset.ToLowerInvariant())
            {
                case "scale-in":
                    SetScaleCurves(clip, 0f, 1f, 1f, easeOut: true);
                    break;
                case "scale-out":
                    SetScaleCurves(clip, 1f, 0f, 1f, easeOut: false);
                    break;
                case "fade-in":
                    SetActiveCurve(clip, 0f, 1f, 1f);
                    break;
                case "fade-out":
                    SetActiveCurve(clip, 1f, 0f, 1f);
                    break;
                case "bounce-in":
                    SetBounceCurves(clip, 1f);
                    break;
            }

            return clip;
        }

        private static void SetScaleCurves(AnimationClip clip, float from, float to, float dur, bool easeOut)
        {
            // Ease-out: fast start → settle (outTangent high, inTangent 0).
            // Ease-in:  slow start → accelerate (outTangent 0, inTangent high).
            var outT = easeOut ? 2f : 0f;
            var inT  = easeOut ? 0f : 2f;
            var curve = new AnimationCurve(
                new Keyframe(0f,  from, 0f, outT),
                new Keyframe(dur, to,   inT, 0f));
            clip.SetCurve("", typeof(Transform), "localScale.x", curve);
            clip.SetCurve("", typeof(Transform), "localScale.y", curve);
            clip.SetCurve("", typeof(Transform), "localScale.z", curve);
        }

        private static void SetActiveCurve(AnimationClip clip, float from, float to, float dur)
        {
            var curve = new AnimationCurve(
                new Keyframe(0f,  from),
                new Keyframe(dur, to));
            clip.SetCurve("", typeof(GameObject), "m_IsActive", curve);
        }

        private static void SetBounceCurves(AnimationClip clip, float dur)
        {
            // 0 → overshoot 1.1 at 60% → settle 1.0 at 100%.
            var curve = new AnimationCurve(
                new Keyframe(0f,         0f),
                new Keyframe(dur * 0.6f, 1.1f),
                new Keyframe(dur,        1.0f));
            clip.SetCurve("", typeof(Transform), "localScale.x", curve);
            clip.SetCurve("", typeof(Transform), "localScale.y", curve);
            clip.SetCurve("", typeof(Transform), "localScale.z", curve);
        }

        // ── CSS easing map ───────────────────────────────────────────────────

        private static AnimationCurve ResolveCssEasingCurve(string css)
        {
            return css.ToLowerInvariant() switch
            {
                "linear"       => AnimationCurve.Linear(0f, 0f, 1f, 1f),
                "ease-in"      => new AnimationCurve(
                                      new Keyframe(0f, 0f, 0f, 0f),
                                      new Keyframe(1f, 1f, 2f, 0f)),
                "ease-out"     => new AnimationCurve(
                                      new Keyframe(0f, 0f, 0f, 2f),
                                      new Keyframe(1f, 1f, 0f, 0f)),
                "ease-in-out"  => AnimationCurve.EaseInOut(0f, 0f, 1f, 1f),
                "step"         => new AnimationCurve(
                                      new Keyframe(0f, 0f, 0f, 0f),
                                      new Keyframe(1f, 1f, 0f, 0f)),
                _              => AnimationCurve.Linear(0f, 0f, 1f, 1f)
            };
        }

        // ── response helpers ─────────────────────────────────────────────────

        private static T SafeFromJson<T>(string json) where T : new()
        {
            if (string.IsNullOrWhiteSpace(json)) return new T();
            try { return JsonUtility.FromJson<T>(json); }
            catch { return new T(); }
        }

        private static string OkResponse(string message, string? content)
        {
            return string.IsNullOrWhiteSpace(content)
                ? JsonUtility.ToJson(new TimelineResponse { ok = true, message = message })
                : JsonUtility.ToJson(new TimelineResponse { ok = true, message = message, content = content });
        }

        private static string ErrorResponse(string message)
        {
            return JsonUtility.ToJson(new TimelineResponse { ok = false, message = message });
        }

        private static string PackageNotInstalledResponse()
        {
            return ErrorResponse(
                "com.unity.timeline package is not installed — add it via Package Manager");
        }

        private static string EscapeJson(string s)
        {
            return s.Replace("\\", "\\\\")
                    .Replace("\"", "\\\"")
                    .Replace("\n", "\\n")
                    .Replace("\r", "\\r")
                    .Replace("\t", "\\t");
        }

        // ── DTOs ─────────────────────────────────────────────────────────────

        [Serializable]
        private sealed class AddTrackPayload
        {
            public string assetPath = string.Empty;
            public string type      = string.Empty;
            public string name      = string.Empty;
        }

        [Serializable]
        private sealed class PlacementPayload
        {
            public string directive = "end";
            public string reference = string.Empty;
            public double time;
        }

        [Serializable]
        private sealed class AddClipPayload
        {
            public string          assetPath  = string.Empty;
            public string          trackName  = string.Empty;
            public string          clipName   = string.Empty;
            public double          duration   = 1.0;
            public PlacementPayload placement  = new PlacementPayload();
        }

        [Serializable]
        private sealed class EasePayload
        {
            public string assetPath = string.Empty;
            public string trackName = string.Empty;
            public string clipName  = string.Empty;
            public string mixIn     = string.Empty;
            public string mixOut    = string.Empty;
        }

        [Serializable]
        private sealed class PresetPayload
        {
            public string assetPath = string.Empty;
            public string trackName = string.Empty;
            public string clipName  = string.Empty;
            public string preset    = string.Empty;
        }

        [Serializable]
        private sealed class AddMarkerPayload
        {
            public string assetPath = string.Empty;
            public double time;
            public string signal    = string.Empty;
        }

        [Serializable]
        private sealed class BindPayload
        {
            public string directorPath    = string.Empty;
            public string trackName       = string.Empty;
            public string targetScenePath = string.Empty;
        }

        [Serializable]
        private sealed class TimelineResponse
        {
            public bool   ok;
            public string message = string.Empty;
            public string content = string.Empty;
        }
    }
}
#endif
