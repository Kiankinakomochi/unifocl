#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UniFocl.EditorBridge
{
    internal static class DaemonScenePersistenceService
    {
        public static void RecordPrefabInstanceMutation(UnityEngine.Object mutationTarget)
        {
            if (mutationTarget is null)
            {
                return;
            }

            var gameObject = mutationTarget as GameObject ?? (mutationTarget as Component)?.gameObject;
            if (gameObject is null)
            {
                return;
            }

            try
            {
                PrefabUtility.RecordPrefabInstancePropertyModifications(gameObject);
                if (mutationTarget is Component component)
                {
                    PrefabUtility.RecordPrefabInstancePropertyModifications(component);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[unifocl] Failed to record prefab instance mutation: {ex.Message}");
            }
        }

        public static void PersistMutationScenes(string mutationSource, params Scene[] scenes)
        {
            SaveScenes(markDirty: true, mutationSource, scenes);
        }

        public static void SaveScenesWithoutMarkDirty(string source, params Scene[] scenes)
        {
            SaveScenes(markDirty: false, source, scenes);
        }

        private static void SaveScenes(bool markDirty, string source, params Scene[] scenes)
        {
            DaemonHierarchyService.PersistLoadedPrefabSnapshotRootIfAny(source, markDirty);

            var seen = new HashSet<int>();
            foreach (var scene in scenes)
            {
                if (!DaemonSceneManager.TryResolveSceneForPersistence(scene, out var sceneToSave))
                {
                    Debug.LogWarning($"[unifocl] Skipping scene save after {source}: no valid loaded scene is available.");
                    continue;
                }

                if (!seen.Add(sceneToSave.handle))
                {
                    continue;
                }

                if (!IsScenePersistable(sceneToSave))
                {
                    Debug.LogWarning(
                        $"[unifocl] Skipping scene save after {source}: scene '{sceneToSave.name}' is preview/unsaveable (path: '{sceneToSave.path}').");
                    continue;
                }

                try
                {
                    if (markDirty)
                    {
                        EditorSceneManager.MarkSceneDirty(sceneToSave);
                    }

                    if (!EditorSceneManager.SaveScene(sceneToSave))
                    {
                        Debug.LogWarning(
                            $"[unifocl] Failed to save scene '{sceneToSave.name}' (path: '{sceneToSave.path}') after {source}.");
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning(
                        $"[unifocl] Scene persistence threw after {source} for scene '{sceneToSave.name}' (path: '{sceneToSave.path}'): {ex.Message}");
                }
            }
        }

        private static bool IsScenePersistable(Scene scene)
        {
            if (!scene.IsValid() || !scene.isLoaded)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(scene.path))
            {
                return false;
            }

            return !EditorSceneManager.IsPreviewScene(scene);
        }
    }
}
#endif
