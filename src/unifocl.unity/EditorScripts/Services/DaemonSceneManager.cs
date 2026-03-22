#if UNITY_EDITOR
using System;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace UniFocl.EditorBridge
{
    internal static class DaemonSceneManager
    {
        public static bool TryGetActiveScene(out Scene scene)
        {
            var activeScene = SceneManager.GetActiveScene();
            if (IsValidLoadedScene(activeScene))
            {
                scene = activeScene;
                return true;
            }

            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var loaded = SceneManager.GetSceneAt(i);
                if (IsValidLoadedScene(loaded))
                {
                    scene = loaded;
                    return true;
                }
            }

            scene = default;
            return false;
        }

        public static bool TryResolveSceneForPersistence(Scene candidate, out Scene scene)
        {
            if (IsValidLoadedScene(candidate))
            {
                scene = candidate;
                return true;
            }

            return TryGetActiveScene(out scene);
        }

        public static bool TryLoadSceneSingleAndActivate(string requestedScenePath, out Scene loadedScene, out string? error)
        {
            loadedScene = default;
            error = null;

            var openedScene = EditorSceneManager.OpenScene(requestedScenePath, OpenSceneMode.Single);
            if (!IsValidLoadedScene(openedScene))
            {
                error = $"scene load failed: {requestedScenePath}";
                return false;
            }

            var loadedScenePath = NormalizePath(openedScene.path);
            if (!loadedScenePath.Equals(requestedScenePath, StringComparison.OrdinalIgnoreCase))
            {
                error = $"scene load failed: requested '{requestedScenePath}', loaded '{loadedScenePath}'";
                return false;
            }

            if (!TryEnsureActiveScene(openedScene, requestedScenePath))
            {
                error = $"scene load failed: unable to activate scene '{requestedScenePath}'";
                return false;
            }

            loadedScene = openedScene;
            return true;
        }

        private static bool TryEnsureActiveScene(Scene loadedScene, string requestedScenePath)
        {
            if (!IsValidLoadedScene(loadedScene))
            {
                return false;
            }

            if (IsRequestedSceneActive(requestedScenePath))
            {
                return true;
            }

            if (SceneManager.SetActiveScene(loadedScene) && IsRequestedSceneActive(requestedScenePath))
            {
                return true;
            }

            if (EditorSceneManager.SetActiveScene(loadedScene) && IsRequestedSceneActive(requestedScenePath))
            {
                return true;
            }

            return IsRequestedSceneActive(requestedScenePath);
        }

        private static bool IsRequestedSceneActive(string requestedScenePath)
        {
            if (!TryGetActiveScene(out var activeScene))
            {
                return false;
            }

            return NormalizePath(activeScene.path)
                .Equals(requestedScenePath, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsValidLoadedScene(Scene scene)
        {
            if (!scene.IsValid() || !scene.isLoaded)
            {
                return false;
            }

            // Treat unsaved/untitled scenes as non-loadable mutation context.
            // This forces callers to load a concrete .unity scene first.
            return !string.IsNullOrWhiteSpace(NormalizePath(scene.path));
        }

        private static string NormalizePath(string? scenePath)
        {
            return (scenePath ?? string.Empty).Replace('\\', '/');
        }
    }
}
#endif
