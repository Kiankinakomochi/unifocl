#if UNITY_EDITOR
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UniFocl.EditorBridge
{
    internal static partial class DaemonProjectService
    {
        // ──────────────────────────────────────────────────────────────
        //  DTOs
        // ──────────────────────────────────────────────────────────────

        [Serializable]
        private sealed class MkAssetRequestOptions
        {
            public string type = string.Empty;
            public int count = 1;
            public string name = string.Empty;
        }

        [Serializable]
        private sealed class MkAssetResponsePayload
        {
            public string[] createdPaths = Array.Empty<string>();
        }

        [Serializable]
        private sealed class QueryMkTypesResponsePayload
        {
            public string[] types = Array.Empty<string>();
        }

        [Serializable]
        private sealed class AsmdefTemplate
        {
            public string name = string.Empty;
            public string[] references = Array.Empty<string>();
            public string[] includePlatforms = Array.Empty<string>();
            public string[] excludePlatforms = Array.Empty<string>();
            public bool allowUnsafeCode;
            public bool overrideReferences;
            public string[] precompiledReferences = Array.Empty<string>();
            public bool autoReferenced = true;
            public string[] defineConstraints = Array.Empty<string>();
            public VersionDefineTemplate[] versionDefines = Array.Empty<VersionDefineTemplate>();
            public bool noEngineReferences;
            public string[] optionalUnityReferences = Array.Empty<string>();
        }

        [Serializable]
        private sealed class VersionDefineTemplate
        {
            public string name = string.Empty;
            public string expression = string.Empty;
            public string define = string.Empty;
        }

        [Serializable]
        private sealed class AsmrefTemplate
        {
            public string reference = string.Empty;
        }

        // ──────────────────────────────────────────────────────────────
        //  Top-level command handlers
        // ──────────────────────────────────────────────────────────────

        private static string ExecuteCreateScript(ProjectCommandRequest request)
        {
            return ExecuteWithFileSystemCriticalSection(() =>
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

                if (request.intent is not null && request.intent.flags.dryRun)
                {
                    var payload = BuildFileDiffPayloadWithVcs(
                        request,
                        "project script create preview",
                        new[]
                        {
                            new MutationPathChange
                            {
                                action = "create",
                                path = assetPath,
                                nextPath = assetPath,
                                metaPath = assetPath + ".meta"
                            }
                        });
                    return JsonUtility.ToJson(new ProjectCommandResponse
                    {
                        ok = true,
                        message = "dry-run preview",
                        kind = "dry-run",
                        content = payload
                    });
                }

                if (!EnsureVcsWriteAccess(request, new[] { assetPath, assetPath + ".meta" }, out var vcsError))
                {
                    return JsonUtility.ToJson(new ProjectCommandResponse
                    {
                        ok = false,
                        message = vcsError ?? $"failed to acquire UVCS checkout for {assetPath}"
                    });
                }

                var absolutePath = Path.Combine(GetProjectRoot(), assetPath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(absolutePath) ?? GetProjectRoot());
                File.WriteAllText(absolutePath, request.content);
                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);

                return JsonUtility.ToJson(new ProjectCommandResponse
                {
                    ok = true,
                    message = "script created",
                    kind = "script",
                    content = assetPath
                });
            });
        }

        private static string ExecuteCreateAsset(ProjectCommandRequest request)
        {
            return ExecuteWithFileSystemCriticalSection(() =>
            {
                if (!IsValidAssetPath(request.assetPath) || string.IsNullOrWhiteSpace(request.content))
                {
                    return JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = "mk-asset requires assetPath and content" });
                }

                MkAssetRequestOptions? options;
                try
                {
                    options = JsonUtility.FromJson<MkAssetRequestOptions>(request.content);
                }
                catch
                {
                    options = null;
                }

                if (options is null || string.IsNullOrWhiteSpace(options.type))
                {
                    return JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = "mk-asset requires type" });
                }

                var parentPath = request.assetPath.Replace('\\', '/').TrimEnd('/');
                if (!AssetDatabase.IsValidFolder(parentPath))
                {
                    return JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = $"invalid target folder: {parentPath}" });
                }

                var canonicalType = NormalizeMkAssetType(options.type);
                var count = options.count <= 0 ? 1 : Math.Min(options.count, 100);

                if (request.intent is not null && request.intent.flags.dryRun)
                {
                    return BuildMkAssetDryRunResponse(request, parentPath, canonicalType, count, options.name);
                }

                var createdPaths = new List<string>(count);
                string? createError = null;
                var editingStarted = false;

                try
                {
                    if (count > 1)
                    {
                        AssetDatabase.StartAssetEditing();
                        editingStarted = true;
                    }

                    for (var i = 0; i < count; i++)
                    {
                        var displayName = ResolveMkAssetName(canonicalType, options.name, i, count);
                        if (!TryCreateAssetByType(canonicalType, parentPath, displayName, out var createdPath, out var error))
                        {
                            createError = error ?? $"mk-asset failed for type {canonicalType}";
                            break;
                        }

                        if (!string.IsNullOrWhiteSpace(createdPath))
                        {
                            createdPaths.Add(createdPath);
                        }
                    }
                }
                catch (Exception ex)
                {
                    createError = $"mk-asset exception: {ex.Message}";
                }
                finally
                {
                    if (editingStarted)
                    {
                        try
                        {
                            AssetDatabase.StopAssetEditing();
                        }
                        catch (Exception ex)
                        {
                            createError ??= $"mk-asset finalize failed: {ex.Message}";
                        }
                    }
                }

                if (!string.IsNullOrWhiteSpace(createError))
                {
                    return JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = createError });
                }

                // Targeted imports are preferred over full Refresh when we know exact paths.
                foreach (var path in createdPaths
                             .Where(path => !string.IsNullOrWhiteSpace(path))
                             .Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                    }
                    catch (Exception ex)
                    {
                        return JsonUtility.ToJson(new ProjectCommandResponse
                        {
                            ok = false,
                            message = $"mk-asset import failed at {path}: {ex.Message}"
                        });
                    }
                }

                AssetDatabase.SaveAssets();

                var content = JsonUtility.ToJson(new MkAssetResponsePayload
                {
                    createdPaths = createdPaths.Where(path => !string.IsNullOrWhiteSpace(path)).ToArray()
                });
                return JsonUtility.ToJson(new ProjectCommandResponse
                {
                    ok = true,
                    message = $"created {createdPaths.Count} asset(s)",
                    kind = "asset",
                    content = content
                });
            });
        }

        // ──────────────────────────────────────────────────────────────
        //  Asset type dispatcher
        // ──────────────────────────────────────────────────────────────

        private static bool TryCreateAssetByType(
            string canonicalType,
            string parentPath,
            string displayName,
            out string? createdPath,
            out string? error)
        {
            createdPath = null;
            error = null;
            switch (canonicalType)
            {
                case "folder":
                    return TryCreateFolder(parentPath, displayName, out createdPath, out error);
                case "scene":
                    return TryCreateSceneAsset(parentPath, displayName, out createdPath, out error);
                case "scenetemplate":
                    return TryCreateTextAsset(parentPath, displayName, ".scenetemplate", "{}", out createdPath, out error);
                case "prefab":
                    return TryCreatePrefabAsset(parentPath, displayName, isVariant: false, out createdPath, out error);
                case "prefabvariant":
                    return TryCreatePrefabAsset(parentPath, displayName, isVariant: true, out createdPath, out error);
                case "assemblydefinition":
                    return TryCreateTextAsset(parentPath, displayName, ".asmdef", BuildAsmdefJson(displayName, includeTestAssemblies: false), out createdPath, out error);
                case "testingassemblydefinition":
                    return TryCreateTextAsset(parentPath, displayName, ".asmdef", BuildAsmdefJson(displayName, includeTestAssemblies: true), out createdPath, out error);
                case "assemblydefinitionreference":
                    return TryCreateTextAsset(parentPath, displayName, ".asmref", BuildAsmrefJson("Assembly-CSharp"), out createdPath, out error);
                case "testingassemblydefinitionreference":
                    return TryCreateTextAsset(parentPath, displayName, ".asmref", BuildAsmrefJson("Assembly-CSharp-Editor-tests"), out createdPath, out error);
                case "roslynanalyzer":
                    return TryCreateTextAsset(parentPath, displayName, ".txt", "Add your analyzer DLL and configure asmdef precompiledReferences.", out createdPath, out error);
                case "shader":
                    return TryCreateTextAsset(parentPath, displayName, ".shader", BuildShaderTemplate(displayName), out createdPath, out error);
                case "computeshader":
                    return TryCreateTextAsset(parentPath, displayName, ".compute", BuildComputeShaderTemplate(displayName), out createdPath, out error);
                case "shadervariantcollection":
                    return TryCreateScriptableObjectAsset(typeof(ShaderVariantCollection), parentPath, displayName, ".shadervariants", out createdPath, out error);
                case "shaderincludefile":
                    return TryCreateTextAsset(parentPath, displayName, ".hlsl", "// HLSL include", out createdPath, out error);
                case "material":
                    return TryCreateMaterialAsset(parentPath, displayName, out createdPath, out error);
                case "rendertexture":
                    return TryCreateRenderTextureAsset(parentPath, displayName, isCustom: false, out createdPath, out error);
                case "customrendertexture":
                    return TryCreateRenderTextureAsset(parentPath, displayName, isCustom: true, out createdPath, out error);
                case "animatorcontroller":
                    return TryCreateAnimatorControllerAsset(parentPath, displayName, out createdPath, out error);
                case "animatoroverridecontroller":
                    return TryCreateScriptableObjectAsset(typeof(AnimatorOverrideController), parentPath, displayName, ".overrideController", out createdPath, out error);
                case "avatarmask":
                    return TryCreateScriptableObjectAsset(typeof(AvatarMask), parentPath, displayName, ".mask", out createdPath, out error);
                case "animationclip":
                    return TryCreateScriptableObjectAsset(typeof(AnimationClip), parentPath, displayName, ".anim", out createdPath, out error);
                case "timeline":
                    return TryCreateReflectionScriptableObjectAsset("UnityEngine.Timeline.TimelineAsset", parentPath, displayName, ".playable", out createdPath, out error);
                case "audiomixer":
                    return TryCreateReflectionScriptableObjectAsset("UnityEditor.Audio.AudioMixerController", parentPath, displayName, ".mixer", out createdPath, out error);
                case "physicsmaterial":
                    return TryCreateScriptableObjectAsset(typeof(PhysicsMaterial), parentPath, displayName, ".physicMaterial", out createdPath, out error);
                case "physicsmaterial2d":
                    return TryCreateScriptableObjectAsset(typeof(PhysicsMaterial2D), parentPath, displayName, ".physicsMaterial2D", out createdPath, out error);
                case "spriteatlas":
                    return TryCreateReflectionScriptableObjectAsset("UnityEngine.U2D.SpriteAtlas", parentPath, displayName, ".spriteatlas", out createdPath, out error);
                case "tile":
                    return TryCreateReflectionScriptableObjectAsset("UnityEngine.Tilemaps.Tile", parentPath, displayName, ".asset", out createdPath, out error);
                case "tilepalette":
                    return TryCreateReflectionScriptableObjectAsset("UnityEditor.GridPalette", parentPath, displayName, ".asset", out createdPath, out error);
                case "ruletile":
                    return TryCreateReflectionScriptableObjectAsset("UnityEngine.RuleTile", parentPath, displayName, ".asset", out createdPath, out error);
                case "animatedtile":
                    return TryCreateReflectionScriptableObjectAsset("UnityEngine.AnimatedTile", parentPath, displayName, ".asset", out createdPath, out error);
                case "isometrictile":
                    return TryCreateReflectionScriptableObjectAsset("UnityEngine.IsometricRuleTile", parentPath, displayName, ".asset", out createdPath, out error);
                case "hexagonaltile":
                    return TryCreateReflectionScriptableObjectAsset("UnityEngine.HexagonalRuleTile", parentPath, displayName, ".asset", out createdPath, out error);
                case "inputactions":
                    return TryCreateTextAsset(parentPath, displayName, ".inputactions", BuildInputActionsTemplate(displayName), out createdPath, out error);
                case "uitoolkitpanelsettings":
                    return TryCreateReflectionScriptableObjectAsset("UnityEngine.UIElements.PanelSettings", parentPath, displayName, ".asset", out createdPath, out error);
                case "uidocument":
                case "uxmldocument":
                    return TryCreateTextAsset(parentPath, displayName, ".uxml", "<ui:UXML xmlns:ui=\"UnityEngine.UIElements\"></ui:UXML>", out createdPath, out error);
                case "ussstylesheet":
                    return TryCreateTextAsset(parentPath, displayName, ".uss", ":root { }", out createdPath, out error);
                case "lightingsettings":
                    return TryCreateScriptableObjectAsset(typeof(LightingSettings), parentPath, displayName, ".lighting", out createdPath, out error);
                case "lensflare":
                    return TryCreateReflectionScriptableObjectAsset("UnityEngine.Rendering.LensFlareDataSRP", parentPath, displayName, ".asset", out createdPath, out error);
                case "cubemap":
                    return TryCreateCubemapAsset(parentPath, displayName, out createdPath, out error);
                case "texture":
                    return TryCreateTextureAsset(parentPath, displayName, out createdPath, out error);
                case "renderpipelineasset":
                    return TryCreateFirstAvailableRenderPipelineAsset(parentPath, displayName, out createdPath, out error);
                case "universalrenderpipelineasset":
                    return TryCreateReflectionScriptableObjectAsset("UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset", parentPath, displayName, ".asset", out createdPath, out error);
                case "highdefinitionrenderpipelineasset":
                    return TryCreateReflectionScriptableObjectAsset("UnityEngine.Rendering.HighDefinition.HDRenderPipelineAsset", parentPath, displayName, ".asset", out createdPath, out error);
                case "postprocessingprofile":
                case "volumeprofile":
                    return TryCreateReflectionScriptableObjectAsset("UnityEngine.Rendering.VolumeProfile", parentPath, displayName, ".asset", out createdPath, out error);
                case "addressablesgroup":
                    return TryCreateAddressablesGroup(parentPath, displayName, out createdPath, out error);
                case "addressablesassetgrouptemplate":
                    return TryCreateReflectionScriptableObjectAsset("UnityEditor.AddressableAssets.Settings.GroupSchemas.AddressableAssetGroupSchema", parentPath, displayName, ".asset", out createdPath, out error);
                case "shadergraph":
                    return TryCreateTextAsset(parentPath, displayName, ".shadergraph", "{}", out createdPath, out error);
                case "subgraph":
                    return TryCreateTextAsset(parentPath, displayName, ".shadersubgraph", "{}", out createdPath, out error);
                case "vfxgraph":
                    return TryCreateTextAsset(parentPath, displayName, ".vfx", "{}", out createdPath, out error);
                case "visualscriptingscriptgraph":
                case "visualscriptingstategraph":
                    return TryCreateTextAsset(parentPath, displayName, ".asset", "Visual Scripting graph placeholder.", out createdPath, out error);
                case "playableasset":
                    return TryCreateReflectionScriptableObjectAsset("UnityEngine.Playables.PlayableAsset", parentPath, displayName, ".asset", out createdPath, out error);
                case "playablegraphasset":
                    return TryCreateTextAsset(parentPath, displayName, ".asset", "Playable graph placeholder.", out createdPath, out error);
                case "localizationtable":
                case "stringtable":
                case "assettable":
                    return TryCreateReflectionScriptableObjectAsset("UnityEngine.Localization.Tables.StringTableCollection", parentPath, displayName, ".asset", out createdPath, out error);
                case "locale":
                    return TryCreateReflectionScriptableObjectAsset("UnityEngine.Localization.Locale", parentPath, displayName, ".asset", out createdPath, out error);
                case "terrainlayer":
                    return TryCreateScriptableObjectAsset(typeof(TerrainLayer), parentPath, displayName, ".terrainlayer", out createdPath, out error);
                case "navmeshdata":
                    return TryCreateScriptableObjectAsset(typeof(UnityEngine.AI.NavMeshData), parentPath, displayName, ".asset", out createdPath, out error);
                case "playmodetestasset":
                    return TryCreateTextAsset(parentPath, displayName, ".asset", "Play mode test asset placeholder.", out createdPath, out error);
                case "preset":
                    return TryCreatePresetAsset(parentPath, displayName, out createdPath, out error);
                case "searchindex":
                    return TryCreateTextAsset(parentPath, displayName, ".index", "{}", out createdPath, out error);
                default:
                    return TryCreateScriptableObjectViaTypeCache(canonicalType, parentPath, displayName, out createdPath, out error);
            }
        }

        // ──────────────────────────────────────────────────────────────
        //  Per-type creation helpers
        // ──────────────────────────────────────────────────────────────

        private static bool TryCreateFolder(string parentPath, string folderName, out string? createdPath, out string? error)
        {
            createdPath = null;
            error = null;
            var resolvedName = string.IsNullOrWhiteSpace(folderName) ? "NewFolder" : folderName.Trim();
            var path = $"{parentPath}/{resolvedName}";
            var uniquePath = GenerateUniqueFolderPath(path);
            var parent = Path.GetDirectoryName(uniquePath)?.Replace('\\', '/') ?? parentPath;
            var leaf = Path.GetFileName(uniquePath);
            if (!AssetDatabase.IsValidFolder(parent))
            {
                error = $"invalid parent folder: {parent}";
                return false;
            }

            var guid = AssetDatabase.CreateFolder(parent, leaf);
            if (string.IsNullOrWhiteSpace(guid))
            {
                error = $"failed to create folder: {uniquePath}";
                return false;
            }

            createdPath = uniquePath;
            return true;
        }

        private static string GenerateUniqueFolderPath(string path)
        {
            var candidate = path.Replace('\\', '/');
            var suffix = 1;
            while (AssetDatabase.IsValidFolder(candidate))
            {
                candidate = $"{path}_{suffix++}".Replace('\\', '/');
            }

            return candidate;
        }

        private static bool TryCreateSceneAsset(string parentPath, string displayName, out string? createdPath, out string? error)
        {
            createdPath = null;
            error = null;
            var path = GenerateUniqueAssetPathWithSuffix(parentPath, displayName, ".unity");
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            if (!EditorSceneManager.SaveScene(scene, path))
            {
                error = $"failed to create scene: {path}";
                return false;
            }

            createdPath = path;
            return true;
        }

        private static bool TryCreatePrefabAsset(string parentPath, string displayName, bool isVariant, out string? createdPath, out string? error)
        {
            createdPath = null;
            error = null;
            var path = GenerateUniqueAssetPathWithSuffix(parentPath, displayName, ".prefab");
            var go = new GameObject(displayName);
            try
            {
                var saved = PrefabUtility.SaveAsPrefabAsset(go, path);
                if (saved is null)
                {
                    error = $"failed to create {(isVariant ? "prefab variant" : "prefab")}: {path}";
                    return false;
                }

                createdPath = path;
                return true;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        private static bool TryCreateAnimatorControllerAsset(string parentPath, string displayName, out string? createdPath, out string? error)
        {
            createdPath = null;
            error = null;
            var path = GenerateUniqueAssetPathWithSuffix(parentPath, displayName, ".controller");
            var controller = UnityEditor.Animations.AnimatorController.CreateAnimatorControllerAtPath(path);
            if (controller is null)
            {
                error = $"failed to create animator controller: {path}";
                return false;
            }

            createdPath = path;
            return true;
        }

        private static bool TryCreateMaterialAsset(string parentPath, string displayName, out string? createdPath, out string? error)
        {
            createdPath = null;
            error = null;
            var shader = Shader.Find("Standard")
                         ?? Shader.Find("Universal Render Pipeline/Lit")
                         ?? Shader.Find("HDRP/Lit");
            if (shader is null)
            {
                error = "failed to resolve a shader for material creation";
                return false;
            }

            var material = new Material(shader);
            return TryCreateObjectAsset(material, parentPath, displayName, ".mat", out createdPath, out error);
        }

        private static bool TryCreateRenderTextureAsset(string parentPath, string displayName, bool isCustom, out string? createdPath, out string? error)
        {
            var texture = isCustom
                ? new CustomRenderTexture(256, 256)
                : new RenderTexture(256, 256, 16);
            return TryCreateObjectAsset(texture, parentPath, displayName, ".renderTexture", out createdPath, out error);
        }

        private static bool TryCreateTextureAsset(string parentPath, string displayName, out string? createdPath, out string? error)
        {
            var texture = new Texture2D(16, 16, TextureFormat.RGBA32, false);
            return TryCreateObjectAsset(texture, parentPath, displayName, ".asset", out createdPath, out error);
        }

        private static bool TryCreateCubemapAsset(string parentPath, string displayName, out string? createdPath, out string? error)
        {
            var cubemap = new Cubemap(16, TextureFormat.RGBA32, false);
            return TryCreateObjectAsset(cubemap, parentPath, displayName, ".cubemap", out createdPath, out error);
        }

        private static bool TryCreatePresetAsset(string parentPath, string displayName, out string? createdPath, out string? error)
        {
            createdPath = null;
            error = null;
            var presetType = ResolveType("UnityEditor.Presets.Preset");
            if (presetType is null)
            {
                error = "Preset API is unavailable in this Unity editor";
                return false;
            }

            var shader = Shader.Find("Standard") ?? Shader.Find("Universal Render Pipeline/Lit");
            if (shader is null)
            {
                error = "failed to resolve a shader for preset creation";
                return false;
            }

            var material = new Material(shader);
            try
            {
                var preset = Activator.CreateInstance(presetType, material) as UnityEngine.Object;
                if (preset is null)
                {
                    error = "failed to create preset";
                    return false;
                }

                return TryCreateObjectAsset(preset, parentPath, displayName, ".preset", out createdPath, out error);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(material);
            }
        }

        private static bool TryCreateFirstAvailableRenderPipelineAsset(string parentPath, string displayName, out string? createdPath, out string? error)
        {
            if (TryCreateReflectionScriptableObjectAsset("UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset", parentPath, displayName, ".asset", out createdPath, out error))
            {
                return true;
            }

            if (TryCreateReflectionScriptableObjectAsset("UnityEngine.Rendering.HighDefinition.HDRenderPipelineAsset", parentPath, displayName, ".asset", out createdPath, out error))
            {
                return true;
            }

            error = "no render pipeline package is installed (URP/HDRP)";
            return false;
        }

        private static bool TryCreateAddressablesGroup(string parentPath, string displayName, out string? createdPath, out string? error)
        {
            createdPath = null;
            error = null;
            var settingsType = ResolveType("UnityEditor.AddressableAssets.AddressableAssetSettingsDefaultObject")
                ?? ResolveType("UnityEditor.AddressableAssets.Settings.AddressableAssetSettingsDefaultObject");
            var settingsProperty = settingsType?.GetProperty("Settings", BindingFlags.Public | BindingFlags.Static);
            var settings = settingsProperty?.GetValue(null);
            if (settings is null)
            {
                error = "Addressables settings were not found (install com.unity.addressables and create settings first)";
                return false;
            }

            var createGroup = settings.GetType().GetMethod(
                "CreateGroup",
                BindingFlags.Public | BindingFlags.Instance,
                null,
                new[] { typeof(string), typeof(bool), typeof(bool), typeof(bool), typeof(List<UnityEngine.Object>), typeof(Type[]) },
                null);
            if (createGroup is null)
            {
                error = "Addressables CreateGroup API is unavailable";
                return false;
            }

            var group = createGroup.Invoke(settings, new object?[] { displayName, false, false, false, null, null });
            if (group is not UnityEngine.Object groupObject)
            {
                error = "failed to create Addressables group";
                return false;
            }

            createdPath = AssetDatabase.GetAssetPath(groupObject);
            if (string.IsNullOrWhiteSpace(createdPath))
            {
                error = "Addressables group was created but asset path could not be resolved";
                return false;
            }

            return true;
        }

        private static bool TryCreateReflectionScriptableObjectAsset(
            string typeName,
            string parentPath,
            string displayName,
            string extension,
            out string? createdPath,
            out string? error)
        {
            var type = ResolveType(typeName);
            if (type is null)
            {
                createdPath = null;
                error = $"required type is unavailable: {typeName}";
                return false;
            }

            if (!typeof(UnityEngine.Object).IsAssignableFrom(type))
            {
                createdPath = null;
                error = $"type is not a Unity asset type: {typeName}";
                return false;
            }

            UnityEngine.Object? instance;
            try
            {
                instance = ScriptableObject.CreateInstance(type);
            }
            catch (Exception ex)
            {
                createdPath = null;
                error = $"failed to create {typeName}: {ex.Message}";
                return false;
            }

            if (instance is null)
            {
                createdPath = null;
                error = $"failed to create {typeName}";
                return false;
            }

            return TryCreateObjectAsset(instance, parentPath, displayName, extension, out createdPath, out error);
        }

        private static bool TryCreateScriptableObjectAsset(
            Type type,
            string parentPath,
            string displayName,
            string extension,
            out string? createdPath,
            out string? error)
        {
            UnityEngine.Object? instance;
            try
            {
                instance = ScriptableObject.CreateInstance(type);
            }
            catch (Exception ex)
            {
                createdPath = null;
                error = $"failed to create {type.Name}: {ex.Message}";
                return false;
            }

            if (instance is null)
            {
                createdPath = null;
                error = $"failed to create {type.Name}";
                return false;
            }

            return TryCreateObjectAsset(instance, parentPath, displayName, extension, out createdPath, out error);
        }

        private static bool TryCreateObjectAsset(
            UnityEngine.Object asset,
            string parentPath,
            string displayName,
            string extension,
            out string? createdPath,
            out string? error)
        {
            createdPath = null;
            error = null;
            var path = GenerateUniqueAssetPathWithSuffix(parentPath, displayName, extension);
            try
            {
                asset.name = displayName;
                AssetDatabase.CreateAsset(asset, path);
                createdPath = path;
                return true;
            }
            catch (Exception ex)
            {
                error = $"failed to create asset at {path}: {ex.Message}";
                UnityEngine.Object.DestroyImmediate(asset);
                return false;
            }
        }

        private static bool TryCreateTextAsset(
            string parentPath,
            string displayName,
            string extension,
            string content,
            out string? createdPath,
            out string? error)
        {
            createdPath = null;
            error = null;
            var path = GenerateUniqueAssetPathWithSuffix(parentPath, displayName, extension);
            var absolute = Path.Combine(GetProjectRoot(), path.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(absolute) ?? GetProjectRoot());
                File.WriteAllText(absolute, content);
                createdPath = path;
                return true;
            }
            catch (Exception ex)
            {
                error = $"failed to create text asset at {path}: {ex.Message}";
                return false;
            }
        }

        // ──────────────────────────────────────────────────────────────
        //  Template builders
        // ──────────────────────────────────────────────────────────────

        private static string BuildAsmdefJson(string name, bool includeTestAssemblies)
        {
            return JsonUtility.ToJson(new AsmdefTemplate
            {
                name = name,
                references = Array.Empty<string>(),
                includePlatforms = Array.Empty<string>(),
                excludePlatforms = Array.Empty<string>(),
                allowUnsafeCode = false,
                overrideReferences = false,
                precompiledReferences = Array.Empty<string>(),
                autoReferenced = true,
                defineConstraints = Array.Empty<string>(),
                versionDefines = Array.Empty<VersionDefineTemplate>(),
                noEngineReferences = false,
                optionalUnityReferences = includeTestAssemblies
                    ? new[] { "TestAssemblies" }
                    : Array.Empty<string>()
            }, prettyPrint: true);
        }

        private static string BuildAsmrefJson(string referenceName)
        {
            return JsonUtility.ToJson(new AsmrefTemplate
            {
                reference = referenceName
            }, prettyPrint: true);
        }

        private static string BuildShaderTemplate(string name)
        {
            return
$"Shader \"Custom/{name}\"{{\n    SubShader{{\n        Pass{{\n        }}\n    }}\n}}";
        }

        private static string BuildComputeShaderTemplate(string name)
        {
            return
$"#pragma kernel CSMain\n\nRWTexture2D<float4> Result;\n\n[numthreads(8,8,1)]\nvoid CSMain(uint3 id : SV_DispatchThreadID)\n{{\n    Result[id.xy] = float4(0,0,0,1);\n}}";
        }

        private static string BuildInputActionsTemplate(string name)
        {
            return
$"{{\n  \"name\": \"{name}\",\n  \"maps\": [],\n  \"controlSchemes\": []\n}}";
        }

        // ──────────────────────────────────────────────────────────────
        //  Naming and path utilities
        // ──────────────────────────────────────────────────────────────

        private static string ResolveMkAssetName(string canonicalType, string? requestedName, int index, int count)
        {
            var baseName = string.IsNullOrWhiteSpace(requestedName)
                ? $"New{canonicalType}"
                : requestedName.Trim();
            return count <= 1 ? baseName : $"{baseName}_{index + 1}";
        }

        private static string GenerateUniqueAssetPathWithSuffix(string parentPath, string baseName, string extension)
        {
            var normalizedParent = parentPath.TrimEnd('/', '\\');
            var normalizedExtension = string.IsNullOrWhiteSpace(extension)
                ? string.Empty
                : extension.StartsWith(".", StringComparison.Ordinal) ? extension : $".{extension}";
            var candidateName = baseName;
            var suffix = 1;
            while (true)
            {
                var candidatePath = $"{normalizedParent}/{candidateName}{normalizedExtension}".Replace('\\', '/');
                if (!DoesAssetPathExist(candidatePath))
                {
                    return candidatePath;
                }

                candidateName = $"{baseName}_{suffix++}";
            }
        }

        private static bool DoesAssetPathExist(string assetPath)
        {
            if (AssetDatabase.IsValidFolder(assetPath))
            {
                return true;
            }

            if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath) is not null)
            {
                return true;
            }

            var absolutePath = Path.Combine(GetProjectRoot(), assetPath.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(absolutePath) || Directory.Exists(absolutePath);
        }

        private static string NormalizeMkAssetType(string raw)
        {
            var normalized = new string(raw
                .Where(char.IsLetterOrDigit)
                .Select(char.ToLowerInvariant)
                .ToArray());
            return normalized switch
            {
                "dir" or "directory" => "folder",
                "unityscene" => "scene",
                "script" or "cscript" or "csharpscript" => "csharpscript",
                "scriptableobject" or "soscript" => "scriptableobjectscript",
                "asmdef" => "assemblydefinition",
                "asmref" => "assemblydefinitionreference",
                "testasmdef" => "testingassemblydefinition",
                "testasmref" => "testingassemblydefinitionreference",
                "cginc" or "shaderinclude" or "hlslinclude" => "shaderincludefile",
                "controller" => "animatorcontroller",
                "animclip" => "animationclip",
                "timelineasset" => "timeline",
                "mixer" => "audiomixer",
                "physicsmat" or "physicmaterial" => "physicsmaterial",
                "physicsmat2d" => "physicsmaterial2d",
                "isometricruletile" => "isometrictile",
                "hexagonalruletile" => "hexagonaltile",
                "inputactionasset" => "inputactions",
                "panelsettings" => "uitoolkitpanelsettings",
                "uxml" => "uxmldocument",
                "uss" => "ussstylesheet",
                "texture2d" => "texture",
                "rpasset" => "renderpipelineasset",
                "urpasset" => "universalrenderpipelineasset",
                "hdrpasset" => "highdefinitionrenderpipelineasset",
                "addressablesgrouptemplate" => "addressablesassetgrouptemplate",
                "playablegraph" or "playablesasset" => "playablegraphasset",
                _ => normalized
            };
        }
    }
}
#endif
