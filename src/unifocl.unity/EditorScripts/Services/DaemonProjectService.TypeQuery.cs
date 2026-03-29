#if UNITY_EDITOR
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace UniFocl.EditorBridge
{
    internal static partial class DaemonProjectService
    {
        private static string ExecuteQueryMkTypes()
        {
            var builtInTypes = new[]
            {
                "Folder", "Scene", "SceneTemplate", "Prefab", "PrefabVariant",
                "CSharpScript", "ScriptableObjectScript",
                "AssemblyDefinition", "AssemblyDefinitionReference",
                "TestingAssemblyDefinition", "TestingAssemblyDefinitionReference",
                "RoslynAnalyzer",
                "Shader", "ComputeShader", "ShaderVariantCollection", "ShaderIncludeFile",
                "Material", "RenderTexture", "CustomRenderTexture",
                "AnimatorController", "AnimatorOverrideController", "AvatarMask",
                "AnimationClip", "Timeline", "AudioMixer",
                "PhysicsMaterial", "PhysicsMaterial2D",
                "SpriteAtlas", "Tile", "TilePalette", "RuleTile", "AnimatedTile",
                "IsometricTile", "HexagonalTile",
                "InputActions", "UIToolkitPanelSettings", "UIDocument",
                "UXMLDocument", "USSStyleSheet",
                "LightingSettings", "LensFlare", "Cubemap", "Texture",
                "RenderPipelineAsset", "UniversalRenderPipelineAsset", "HighDefinitionRenderPipelineAsset",
                "PostProcessingProfile", "VolumeProfile",
                "AddressablesGroup", "AddressablesAssetGroupTemplate",
                "ShaderGraph", "SubGraph", "VFXGraph",
                "VisualScriptingScriptGraph", "VisualScriptingStateGraph",
                "PlayableAsset", "PlayableGraphAsset",
                "LocalizationTable", "StringTable", "AssetTable", "Locale",
                "TerrainLayer", "NavMeshData", "PlayModeTestAsset", "Preset", "SearchIndex"
            };

            var typeNames = new HashSet<string>(builtInTypes, StringComparer.OrdinalIgnoreCase);

            foreach (var kvp in ScriptableObjectTypeLookup.Value)
            {
                var type = kvp.Value;
                if (!string.IsNullOrWhiteSpace(type.Name) && !typeNames.Contains(type.Name))
                {
                    typeNames.Add(type.Name);
                }
            }

            var sorted = typeNames.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
            var content = JsonUtility.ToJson(new QueryMkTypesResponsePayload { types = sorted.ToArray() });
            return JsonUtility.ToJson(new ProjectCommandResponse
            {
                ok = true,
                message = $"{sorted.Count} creatable types",
                kind = "query-mk-types",
                content = content
            });
        }

        private static string ExecuteQueryHierarchyMkTypes()
        {
            var builtInTypes = new[]
            {
                "Canvas", "Panel", "Text", "Tmp", "Image", "Button", "Toggle", "Slider",
                "Scrollbar", "ScrollView", "EventSystem",
                "Cube", "Sphere", "Capsule", "Cylinder", "Plane", "Quad",
                "DirLight", "DirectionalLight", "PointLight", "SpotLight", "AreaLight", "ReflectionProbe",
                "Sprite", "SpriteMask",
                "Camera", "AudioSource", "Empty", "EmptyParent", "EmptyChild"
            };

            var typeNames = new HashSet<string>(builtInTypes, StringComparer.OrdinalIgnoreCase);

            // Include concrete Component subclasses from loaded assemblies so custom
            // MonoBehaviours (e.g. PlayerController) can be created via mk.
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types.Where(t => t is not null).Cast<Type>().ToArray();
                }
                catch
                {
                    continue;
                }

                foreach (var type in types)
                {
                    if (type.IsAbstract || !typeof(Component).IsAssignableFrom(type))
                    {
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(type.Name) && !typeNames.Contains(type.Name))
                    {
                        typeNames.Add(type.Name);
                    }
                }
            }

            var sorted = typeNames.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
            var content = JsonUtility.ToJson(new QueryMkTypesResponsePayload { types = sorted.ToArray() });
            return JsonUtility.ToJson(new ProjectCommandResponse
            {
                ok = true,
                message = $"{sorted.Count} hierarchy mk types",
                kind = "query-hierarchy-mk-types",
                content = content
            });
        }

        private static string ExecuteQueryComponentTypes()
        {
            var typeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types.Where(t => t is not null).Cast<Type>().ToArray();
                }
                catch
                {
                    continue;
                }

                foreach (var type in types)
                {
                    if (type.IsAbstract || !typeof(Component).IsAssignableFrom(type))
                    {
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(type.Name))
                    {
                        typeNames.Add(type.Name);
                    }
                }
            }

            var sorted = typeNames.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
            var content = JsonUtility.ToJson(new QueryMkTypesResponsePayload { types = sorted.ToArray() });
            return JsonUtility.ToJson(new ProjectCommandResponse
            {
                ok = true,
                message = $"{sorted.Count} component types",
                kind = "query-component-types",
                content = content
            });
        }

        private static bool TryCreateScriptableObjectViaTypeCache(
            string rawType,
            string parentPath,
            string displayName,
            out string? createdPath,
            out string? error)
        {
            if (!TryResolveScriptableObjectType(rawType, out var resolvedType))
            {
                createdPath = null;
                error = $"unsupported mk type: {rawType} (not found in catalog or TypeCache)";
                return false;
            }

            return TryCreateScriptableObjectAsset(resolvedType, parentPath, displayName, ".asset", out createdPath, out error);
        }

        private static bool TryResolveScriptableObjectType(string token, out Type resolvedType)
        {
            resolvedType = typeof(ScriptableObject);
            if (string.IsNullOrWhiteSpace(token))
            {
                return false;
            }

            var lookup = ScriptableObjectTypeLookup.Value;

            if (lookup.TryGetValue(token.Trim(), out var exact))
            {
                resolvedType = exact;
                return true;
            }

            var normalized = NormalizeTypeLookupKey(token);
            if (lookup.TryGetValue(normalized, out var matched))
            {
                resolvedType = matched;
                return true;
            }

            return false;
        }

        private static Dictionary<string, Type> BuildScriptableObjectTypeLookup()
        {
            var lookup = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types.Where(type => type is not null).Cast<Type>().ToArray();
                }
                catch
                {
                    continue;
                }

                foreach (var type in types)
                {
                    if (type.IsAbstract || !typeof(ScriptableObject).IsAssignableFrom(type))
                    {
                        continue;
                    }

                    lookup[type.FullName ?? type.Name] = type;
                    lookup[type.Name] = type;
                    lookup[NormalizeTypeLookupKey(type.Name)] = type;
                    if (!string.IsNullOrWhiteSpace(type.FullName))
                    {
                        lookup[NormalizeTypeLookupKey(type.FullName)] = type;
                    }
                }
            }

            return lookup;
        }

        private static string NormalizeTypeLookupKey(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var chars = value.Where(char.IsLetterOrDigit)
                .Select(char.ToLowerInvariant)
                .ToArray();
            return new string(chars);
        }
    }
}
#nullable restore
#endif
