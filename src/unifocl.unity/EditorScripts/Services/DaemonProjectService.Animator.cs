#if UNITY_EDITOR
#nullable enable
using System;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace UniFocl.EditorBridge
{
    internal static partial class DaemonProjectService
    {
        [Serializable]
        private sealed class AnimatorParamPayload
        {
            public string name = string.Empty;
            public string type = string.Empty;
        }

        [Serializable]
        private sealed class AnimatorStatePayload
        {
            public string name = string.Empty;
            public int layer;
        }

        [Serializable]
        private sealed class AnimatorTransitionPayload
        {
            public string fromState = string.Empty;
            public string toState = string.Empty;
            public int layer;
        }

        private static string ExecuteAnimatorParamAdd(ProjectCommandRequest request)
        {
            var controller = LoadAnimatorController(request.assetPath, out var errorResponse);
            if (controller == null)
            {
                return errorResponse!;
            }

            AnimatorParamPayload? payload;
            try { payload = JsonUtility.FromJson<AnimatorParamPayload>(request.content); }
            catch { payload = null; }

            if (payload == null || string.IsNullOrWhiteSpace(payload.name))
            {
                return JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = "animator-param-add requires content.name" });
            }

            if (!TryParseAnimatorParameterType(payload.type, out var paramType))
            {
                return JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = $"unknown parameter type '{payload.type}'; must be float, int, bool, or trigger" });
            }

            foreach (var existing in controller.parameters)
            {
                if (existing.name.Equals(payload.name, StringComparison.Ordinal))
                {
                    return JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = $"parameter '{payload.name}' already exists" });
                }
            }

            controller.AddParameter(payload.name, paramType);
            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();

            return JsonUtility.ToJson(new ProjectCommandResponse
            {
                ok = true,
                message = $"added {payload.type} parameter '{payload.name}' to '{request.assetPath}'",
                kind = "animator"
            });
        }

        private static string ExecuteAnimatorParamRemove(ProjectCommandRequest request)
        {
            var controller = LoadAnimatorController(request.assetPath, out var errorResponse);
            if (controller == null)
            {
                return errorResponse!;
            }

            AnimatorParamPayload? payload;
            try { payload = JsonUtility.FromJson<AnimatorParamPayload>(request.content); }
            catch { payload = null; }

            if (payload == null || string.IsNullOrWhiteSpace(payload.name))
            {
                return JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = "animator-param-remove requires content.name" });
            }

            var parameters = controller.parameters;
            var found = false;
            for (var i = 0; i < parameters.Length; i++)
            {
                if (!parameters[i].name.Equals(payload.name, StringComparison.Ordinal))
                {
                    continue;
                }

                controller.RemoveParameter(i);
                found = true;
                break;
            }

            if (!found)
            {
                return JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = $"parameter '{payload.name}' not found" });
            }

            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();

            return JsonUtility.ToJson(new ProjectCommandResponse
            {
                ok = true,
                message = $"removed parameter '{payload.name}' from '{request.assetPath}'",
                kind = "animator"
            });
        }

        private static string ExecuteAnimatorStateAdd(ProjectCommandRequest request)
        {
            var controller = LoadAnimatorController(request.assetPath, out var errorResponse);
            if (controller == null)
            {
                return errorResponse!;
            }

            AnimatorStatePayload? payload;
            try { payload = JsonUtility.FromJson<AnimatorStatePayload>(request.content); }
            catch { payload = null; }

            if (payload == null || string.IsNullOrWhiteSpace(payload.name))
            {
                return JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = "animator-state-add requires content.name" });
            }

            if (payload.layer < 0 || payload.layer >= controller.layers.Length)
            {
                return JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = $"layer index {payload.layer} is out of range (controller has {controller.layers.Length} layer(s))" });
            }

            var stateMachine = controller.layers[payload.layer].stateMachine;

            foreach (var existing in stateMachine.states)
            {
                if (existing.state.name.Equals(payload.name, StringComparison.Ordinal))
                {
                    return JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = $"state '{payload.name}' already exists in layer {payload.layer}" });
                }
            }

            stateMachine.AddState(payload.name);
            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();

            return JsonUtility.ToJson(new ProjectCommandResponse
            {
                ok = true,
                message = $"added state '{payload.name}' to layer {payload.layer} of '{request.assetPath}'",
                kind = "animator"
            });
        }

        private static string ExecuteAnimatorTransitionAdd(ProjectCommandRequest request)
        {
            var controller = LoadAnimatorController(request.assetPath, out var errorResponse);
            if (controller == null)
            {
                return errorResponse!;
            }

            AnimatorTransitionPayload? payload;
            try { payload = JsonUtility.FromJson<AnimatorTransitionPayload>(request.content); }
            catch { payload = null; }

            if (payload == null || string.IsNullOrWhiteSpace(payload.fromState) || string.IsNullOrWhiteSpace(payload.toState))
            {
                return JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = "animator-transition-add requires content.fromState and content.toState" });
            }

            if (payload.layer < 0 || payload.layer >= controller.layers.Length)
            {
                return JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = $"layer index {payload.layer} is out of range (controller has {controller.layers.Length} layer(s))" });
            }

            var stateMachine = controller.layers[payload.layer].stateMachine;

            var toState = FindState(stateMachine, payload.toState);
            if (toState == null)
            {
                return JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = $"destination state '{payload.toState}' not found in layer {payload.layer}" });
            }

            if (payload.fromState.Equals("AnyState", StringComparison.OrdinalIgnoreCase))
            {
                stateMachine.AddAnyStateTransition(toState);
            }
            else
            {
                var fromState = FindState(stateMachine, payload.fromState);
                if (fromState == null)
                {
                    return JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = $"source state '{payload.fromState}' not found in layer {payload.layer}" });
                }

                fromState.AddTransition(toState);
            }

            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();

            return JsonUtility.ToJson(new ProjectCommandResponse
            {
                ok = true,
                message = $"added transition from '{payload.fromState}' to '{payload.toState}' in layer {payload.layer} of '{request.assetPath}'",
                kind = "animator"
            });
        }

        private static AnimatorController? LoadAnimatorController(string? assetPath, out string? errorResponse)
        {
            if (!IsValidAssetPath(assetPath))
            {
                errorResponse = JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = "a valid assetPath to an AnimatorController (.controller) is required" });
                return null;
            }

            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(assetPath);
            if (controller == null)
            {
                errorResponse = JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = $"AnimatorController not found at: {assetPath}" });
                return null;
            }

            errorResponse = null;
            return controller;
        }

        private static AnimatorState? FindState(AnimatorStateMachine stateMachine, string stateName)
        {
            foreach (var childState in stateMachine.states)
            {
                if (childState.state.name.Equals(stateName, StringComparison.Ordinal))
                {
                    return childState.state;
                }
            }

            return null;
        }

        private static bool TryParseAnimatorParameterType(string? raw, out AnimatorControllerParameterType result)
        {
            if (raw == null)
            {
                result = default;
                return false;
            }

            switch (raw.ToLowerInvariant())
            {
                case "float":   result = AnimatorControllerParameterType.Float;   return true;
                case "int":     result = AnimatorControllerParameterType.Int;     return true;
                case "bool":    result = AnimatorControllerParameterType.Bool;    return true;
                case "trigger": result = AnimatorControllerParameterType.Trigger; return true;
                default:
                    result = default;
                    return false;
            }
        }
    }
}
#nullable restore
#endif
