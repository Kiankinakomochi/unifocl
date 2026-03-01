#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UniFocl.EditorBridge
{
    internal static class DaemonProjectService
    {
        public static string Execute(string payload)
        {
            ProjectCommandRequest? request;
            try
            {
                request = JsonUtility.FromJson<ProjectCommandRequest>(payload);
            }
            catch
            {
                return JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = "invalid project command payload" });
            }

            if (request is null || string.IsNullOrWhiteSpace(request.action))
            {
                return JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = "missing project command payload" });
            }

            try
            {
                return request.action switch
                {
                    "healthcheck" => JsonUtility.ToJson(new ProjectCommandResponse { ok = true, message = "project command endpoint ready", kind = "healthcheck" }),
                    "mk-script" => ExecuteCreateScript(request),
                    "rename-asset" => ExecuteRenameAsset(request),
                    "remove-asset" => ExecuteRemoveAsset(request),
                    "load-asset" => ExecuteLoadAsset(request),
                    _ => JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = $"unsupported action: {request.action}" })
                };
            }
            catch (Exception ex)
            {
                return JsonUtility.ToJson(new ProjectCommandResponse
                {
                    ok = false,
                    message = $"project command exception: {ex.GetType().Name}: {ex.Message}"
                });
            }
        }

        private static string ExecuteCreateScript(ProjectCommandRequest request)
        {
            if (!IsValidAssetPath(request.assetPath) || string.IsNullOrWhiteSpace(request.content))
            {
                return JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = "mk-script requires assetPath and content" });
            }

            var assetPath = request.assetPath;
            if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath) is not null)
            {
                return JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = $"asset already exists: {assetPath}" });
            }

            var absolutePath = Path.Combine(GetProjectRoot(), assetPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(absolutePath) ?? GetProjectRoot());
            File.WriteAllText(absolutePath, request.content);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);

            return JsonUtility.ToJson(new ProjectCommandResponse
            {
                ok = true,
                message = "script created",
                kind = "script"
            });
        }

        private static string ExecuteRenameAsset(ProjectCommandRequest request)
        {
            if (!IsValidAssetPath(request.assetPath) || !IsValidAssetPath(request.newAssetPath))
            {
                return JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = "rename-asset requires assetPath and newAssetPath" });
            }

            if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(request.assetPath) is null)
            {
                return JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = $"asset not found: {request.assetPath}" });
            }

            var error = AssetDatabase.MoveAsset(request.assetPath, request.newAssetPath);
            if (!string.IsNullOrWhiteSpace(error))
            {
                return JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = error });
            }

            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
            return JsonUtility.ToJson(new ProjectCommandResponse { ok = true, message = "asset renamed" });
        }

        private static string ExecuteRemoveAsset(ProjectCommandRequest request)
        {
            if (!IsValidAssetPath(request.assetPath))
            {
                return JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = "remove-asset requires assetPath" });
            }

            if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(request.assetPath) is null
                && !AssetDatabase.IsValidFolder(request.assetPath))
            {
                return JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = $"asset not found: {request.assetPath}" });
            }

            if (!AssetDatabase.MoveAssetToTrash(request.assetPath))
            {
                return JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = $"failed to remove asset: {request.assetPath}" });
            }

            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
            return JsonUtility.ToJson(new ProjectCommandResponse { ok = true, message = "asset removed" });
        }

        private static string ExecuteLoadAsset(ProjectCommandRequest request)
        {
            if (!IsValidAssetPath(request.assetPath))
            {
                return JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = "load-asset requires assetPath" });
            }

            var extension = Path.GetExtension(request.assetPath);
            if (extension.Equals(".unity", StringComparison.OrdinalIgnoreCase))
            {
                var requestedScenePath = request.assetPath.Replace('\\', '/');
                if (!File.Exists(Path.Combine(GetProjectRoot(), requestedScenePath.Replace('/', Path.DirectorySeparatorChar))))
                {
                    return JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = $"scene not found: {request.assetPath}" });
                }

                try
                {
                    var loadedScene = EditorSceneManager.OpenScene(requestedScenePath, OpenSceneMode.Single);
                    if (!loadedScene.IsValid() || !loadedScene.isLoaded)
                    {
                        return JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = $"scene load failed: {requestedScenePath}" });
                    }

                    var loadedScenePath = loadedScene.path.Replace('\\', '/');
                    if (!loadedScenePath.Equals(requestedScenePath, StringComparison.OrdinalIgnoreCase))
                    {
                        return JsonUtility.ToJson(new ProjectCommandResponse
                        {
                            ok = false,
                            message = $"scene load failed: requested '{requestedScenePath}', loaded '{loadedScenePath}'"
                        });
                    }

                    if (!TryEnsureActiveScene(loadedScene, requestedScenePath))
                    {
                        return JsonUtility.ToJson(new ProjectCommandResponse
                        {
                            ok = false,
                            message = $"scene load failed: unable to activate scene '{requestedScenePath}'"
                        });
                    }

                    return JsonUtility.ToJson(new ProjectCommandResponse { ok = true, message = "scene loaded", kind = "scene" });
                }
                catch (Exception ex)
                {
                    return JsonUtility.ToJson(new ProjectCommandResponse
                    {
                        ok = false,
                        message = $"scene load failed: {ex.Message}"
                    });
                }
            }

            if (extension.Equals(".cs", StringComparison.OrdinalIgnoreCase))
            {
                var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(request.assetPath);
                if (asset is null)
                {
                    return JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = $"script not found: {request.assetPath}" });
                }

                AssetDatabase.OpenAsset(asset);
                return JsonUtility.ToJson(new ProjectCommandResponse { ok = true, message = "script opened", kind = "script" });
            }

            return JsonUtility.ToJson(new ProjectCommandResponse
            {
                ok = false,
                message = $"unsupported asset type: {extension} (supported: .unity, .cs)"
            });
        }

        private static bool IsValidAssetPath(string? assetPath)
        {
            return !string.IsNullOrWhiteSpace(assetPath)
                   && assetPath.StartsWith("Assets/", StringComparison.Ordinal)
                   && !assetPath.Contains("..", StringComparison.Ordinal);
        }

        private static bool TryEnsureActiveScene(Scene loadedScene, string requestedScenePath)
        {
            if (!loadedScene.IsValid() || !loadedScene.isLoaded)
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

            // Opening with OpenSceneMode.Single usually makes the scene active already.
            return IsRequestedSceneActive(requestedScenePath);
        }

        private static bool IsRequestedSceneActive(string requestedScenePath)
        {
            var activeScene = SceneManager.GetActiveScene();
            if (!activeScene.IsValid() || !activeScene.isLoaded)
            {
                return false;
            }

            return activeScene.path.Replace('\\', '/')
                .Equals(requestedScenePath, StringComparison.OrdinalIgnoreCase);
        }

        private static string GetProjectRoot()
        {
            return Directory.GetParent(Application.dataPath)?.FullName
                   ?? throw new InvalidOperationException("failed to resolve Unity project root");
        }
    }
}
#endif
