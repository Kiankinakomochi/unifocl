#if UNITY_EDITOR
#nullable enable
using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace UniFocl.EditorBridge
{
    internal static partial class DaemonProjectService
    {
        [Serializable]
        private sealed class ClipConfigPayload
        {
            public bool loopTime;
            public bool loopPose;
            public bool setLoopTime;
            public bool setLoopPose;
        }

        [Serializable]
        private sealed class ClipEventAddPayload
        {
            public float time;
            public string functionName = string.Empty;
            public string stringParam = string.Empty;
            public float floatParam;
            public int intParam;
            public bool hasStringParam;
            public bool hasFloatParam;
            public bool hasIntParam;
        }

        private static string ExecuteClipConfig(ProjectCommandRequest request)
        {
            var clip = LoadAnimationClip(request.assetPath, out var errorResponse);
            if (clip == null) return errorResponse!;

            ClipConfigPayload? payload;
            try { payload = JsonUtility.FromJson<ClipConfigPayload>(request.content); }
            catch { payload = null; }

            if (payload == null || (!payload.setLoopTime && !payload.setLoopPose))
            {
                return JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = "clip-config requires at least one of --loop-time or --loop-pose" });
            }

            var settings = AnimationUtility.GetAnimationClipSettings(clip);
            if (payload.setLoopTime) settings.loopTime = payload.loopTime;
            if (payload.setLoopPose) settings.loopBlend = payload.loopPose;
            AnimationUtility.SetAnimationClipSettings(clip, settings);
            EditorUtility.SetDirty(clip);
            AssetDatabase.SaveAssets();

            return JsonUtility.ToJson(new ProjectCommandResponse
            {
                ok = true,
                message = $"updated clip settings on '{request.assetPath}'",
                kind = "clip"
            });
        }

        private static string ExecuteClipEventAdd(ProjectCommandRequest request)
        {
            var clip = LoadAnimationClip(request.assetPath, out var errorResponse);
            if (clip == null) return errorResponse!;

            ClipEventAddPayload? payload;
            try { payload = JsonUtility.FromJson<ClipEventAddPayload>(request.content); }
            catch { payload = null; }

            if (payload == null || string.IsNullOrWhiteSpace(payload.functionName))
            {
                return JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = "clip-event-add requires content.functionName" });
            }

            var evt = new AnimationEvent
            {
                time = payload.time,
                functionName = payload.functionName
            };

            if (payload.hasStringParam) evt.stringParameter = payload.stringParam;
            if (payload.hasFloatParam) evt.floatParameter = payload.floatParam;
            if (payload.hasIntParam) evt.intParameter = payload.intParam;

            var existing = AnimationUtility.GetAnimationEvents(clip);
            var updated = existing.Concat(new[] { evt }).ToArray();
            AnimationUtility.SetAnimationEvents(clip, updated);
            EditorUtility.SetDirty(clip);
            AssetDatabase.SaveAssets();

            return JsonUtility.ToJson(new ProjectCommandResponse
            {
                ok = true,
                message = $"added event '{payload.functionName}' at t={payload.time} to '{request.assetPath}'",
                kind = "clip"
            });
        }

        private static string ExecuteClipEventClear(ProjectCommandRequest request)
        {
            var clip = LoadAnimationClip(request.assetPath, out var errorResponse);
            if (clip == null) return errorResponse!;

            AnimationUtility.SetAnimationEvents(clip, Array.Empty<AnimationEvent>());
            EditorUtility.SetDirty(clip);
            AssetDatabase.SaveAssets();

            return JsonUtility.ToJson(new ProjectCommandResponse
            {
                ok = true,
                message = $"cleared all animation events from '{request.assetPath}'",
                kind = "clip"
            });
        }

        private static string ExecuteClipCurveClear(ProjectCommandRequest request)
        {
            var clip = LoadAnimationClip(request.assetPath, out var errorResponse);
            if (clip == null) return errorResponse!;

            clip.ClearCurves();
            EditorUtility.SetDirty(clip);
            AssetDatabase.SaveAssets();

            return JsonUtility.ToJson(new ProjectCommandResponse
            {
                ok = true,
                message = $"cleared all curves from '{request.assetPath}'",
                kind = "clip"
            });
        }

        private static AnimationClip? LoadAnimationClip(string? assetPath, out string? errorResponse)
        {
            if (!IsValidAssetPath(assetPath))
            {
                errorResponse = JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = "a valid assetPath to an AnimationClip (.anim) is required" });
                return null;
            }

            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(assetPath);
            if (clip == null)
            {
                errorResponse = JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = $"AnimationClip not found at: {assetPath}" });
                return null;
            }

            errorResponse = null;
            return clip;
        }
    }
}
#nullable restore
#endif
