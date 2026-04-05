#if UNITY_EDITOR
#nullable enable
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace UniFocl.EditorBridge
{
    internal static partial class DaemonProjectService
    {
        [Serializable]
        private sealed class PrefabNodeSelectorOptions
        {
            public string nodeSelector = string.Empty;
            public bool completely;
        }

        private static GameObject? ResolveSceneGameObject(string nodeSelector)
        {
            if (int.TryParse(nodeSelector, out var instanceId))
            {
                // InstanceIDToObject(int) deprecated in Unity 6; EntityIdToObject replacement
                // unavailable in Unity 2021–2022 LTS — suppress until minimum version is raised.
#pragma warning disable CS0618
                return EditorUtility.InstanceIDToObject(instanceId) as GameObject;
#pragma warning restore CS0618
            }

            return GameObject.Find(nodeSelector);
        }

        private static string ExecutePrefabCreate(ProjectCommandRequest request)
        {
            return ExecuteWithFileSystemCriticalSection(() =>
            {
                if (!IsValidAssetPath(request.assetPath))
                {
                    return JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = "prefab-create requires a valid assetPath for the output .prefab file" });
                }

                PrefabNodeSelectorOptions? options;
                try { options = JsonUtility.FromJson<PrefabNodeSelectorOptions>(request.content); }
                catch { options = null; }

                if (options is null || string.IsNullOrWhiteSpace(options.nodeSelector))
                {
                    return JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = "prefab-create requires content.nodeSelector" });
                }

                var go = ResolveSceneGameObject(options.nodeSelector);
                if (go == null)
                {
                    return JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = $"GameObject not found: {options.nodeSelector}" });
                }

                var assetPath = request.assetPath.Replace('\\', '/');
                var directory = Path.GetDirectoryName(assetPath)?.Replace('\\', '/');
                if (!string.IsNullOrWhiteSpace(directory) && !AssetDatabase.IsValidFolder(directory))
                {
                    return JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = $"invalid target folder: {directory}" });
                }

                var saved = PrefabUtility.SaveAsPrefabAssetAndConnect(go, assetPath, InteractionMode.AutomatedAction);
                if (saved == null)
                {
                    return JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = $"failed to create prefab at: {assetPath}" });
                }

                AssetDatabase.Refresh();
                return JsonUtility.ToJson(new ProjectCommandResponse { ok = true, message = $"created prefab: {assetPath}", kind = "prefab", content = assetPath });
            });
        }

        private static string ExecutePrefabApply(ProjectCommandRequest request)
        {
            PrefabNodeSelectorOptions? options;
            try { options = JsonUtility.FromJson<PrefabNodeSelectorOptions>(request.content); }
            catch { options = null; }

            if (options is null || string.IsNullOrWhiteSpace(options.nodeSelector))
            {
                return JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = "prefab-apply requires content.nodeSelector" });
            }

            var go = ResolveSceneGameObject(options.nodeSelector);
            if (go == null)
            {
                return JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = $"GameObject not found: {options.nodeSelector}" });
            }

            if (!PrefabUtility.IsPartOfPrefabInstance(go))
            {
                return JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = $"'{go.name}' is not a prefab instance" });
            }

            var root = PrefabUtility.GetOutermostPrefabInstanceRoot(go);
            if (root == null)
            {
                return JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = $"could not resolve outermost prefab root for '{go.name}'" });
            }

            PrefabUtility.ApplyPrefabInstance(root, InteractionMode.AutomatedAction);
            var sourcePath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(root);
            return JsonUtility.ToJson(new ProjectCommandResponse { ok = true, message = $"applied overrides to: {sourcePath}", kind = "prefab", content = sourcePath });
        }

        private static string ExecutePrefabRevert(ProjectCommandRequest request)
        {
            PrefabNodeSelectorOptions? options;
            try { options = JsonUtility.FromJson<PrefabNodeSelectorOptions>(request.content); }
            catch { options = null; }

            if (options is null || string.IsNullOrWhiteSpace(options.nodeSelector))
            {
                return JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = "prefab-revert requires content.nodeSelector" });
            }

            var go = ResolveSceneGameObject(options.nodeSelector);
            if (go == null)
            {
                return JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = $"GameObject not found: {options.nodeSelector}" });
            }

            if (!PrefabUtility.IsPartOfPrefabInstance(go))
            {
                return JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = $"'{go.name}' is not a prefab instance" });
            }

            var root = PrefabUtility.GetOutermostPrefabInstanceRoot(go);
            if (root == null)
            {
                return JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = $"could not resolve outermost prefab root for '{go.name}'" });
            }

            PrefabUtility.RevertPrefabInstance(root, InteractionMode.AutomatedAction);
            return JsonUtility.ToJson(new ProjectCommandResponse { ok = true, message = $"reverted prefab instance: {root.name}", kind = "prefab" });
        }

        private static string ExecutePrefabUnpack(ProjectCommandRequest request)
        {
            PrefabNodeSelectorOptions? options;
            try { options = JsonUtility.FromJson<PrefabNodeSelectorOptions>(request.content); }
            catch { options = null; }

            if (options is null || string.IsNullOrWhiteSpace(options.nodeSelector))
            {
                return JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = "prefab-unpack requires content.nodeSelector" });
            }

            var go = ResolveSceneGameObject(options.nodeSelector);
            if (go == null)
            {
                return JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = $"GameObject not found: {options.nodeSelector}" });
            }

            if (!PrefabUtility.IsPartOfPrefabInstance(go))
            {
                return JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = $"'{go.name}' is not a prefab instance" });
            }

            var root = PrefabUtility.GetOutermostPrefabInstanceRoot(go);
            if (root == null)
            {
                return JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = $"could not resolve outermost prefab root for '{go.name}'" });
            }

            var mode = options.completely ? PrefabUnpackMode.Completely : PrefabUnpackMode.OutermostRoot;
            PrefabUtility.UnpackPrefabInstance(root, mode, InteractionMode.AutomatedAction);
            var modeLabel = options.completely ? "completely" : "outermost root";
            return JsonUtility.ToJson(new ProjectCommandResponse { ok = true, message = $"unpacked prefab instance ({modeLabel}): {root.name}", kind = "prefab" });
        }

        private static string ExecutePrefabVariant(ProjectCommandRequest request)
        {
            return ExecuteWithFileSystemCriticalSection(() =>
            {
                if (!IsValidAssetPath(request.assetPath))
                {
                    return JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = "prefab-variant requires a valid source assetPath" });
                }

                if (!IsValidAssetPath(request.newAssetPath))
                {
                    return JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = "prefab-variant requires a valid newAssetPath" });
                }

                var sourcePath = request.assetPath.Replace('\\', '/');
                var newPath = request.newAssetPath.Replace('\\', '/');

                var sourcePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(sourcePath);
                if (sourcePrefab == null)
                {
                    return JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = $"source prefab not found: {sourcePath}" });
                }

                var directory = Path.GetDirectoryName(newPath)?.Replace('\\', '/');
                if (!string.IsNullOrWhiteSpace(directory) && !AssetDatabase.IsValidFolder(directory))
                {
                    return JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = $"invalid target folder: {directory}" });
                }

                var instance = PrefabUtility.InstantiatePrefab(sourcePrefab) as GameObject;
                if (instance == null)
                {
                    return JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = $"failed to instantiate source prefab: {sourcePath}" });
                }

                try
                {
                    var variant = PrefabUtility.SaveAsPrefabAsset(instance, newPath);
                    if (variant == null)
                    {
                        return JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = $"failed to create prefab variant at: {newPath}" });
                    }
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(instance);
                }

                AssetDatabase.Refresh();
                return JsonUtility.ToJson(new ProjectCommandResponse { ok = true, message = $"created prefab variant: {newPath}", kind = "prefab", content = newPath });
            });
        }
    }
}
#nullable restore
#endif
