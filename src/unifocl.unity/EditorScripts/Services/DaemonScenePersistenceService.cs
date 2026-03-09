#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UniFocl.EditorBridge
{
    internal static class DaemonScenePersistenceService
    {
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
        }
    }
}
#endif
