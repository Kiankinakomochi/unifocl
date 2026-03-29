#if UNITY_EDITOR
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UniFocl.EditorBridge
{
    internal static class DaemonValidateService
    {
        // ── validate-scene-list ──────────────────────────────────────────

        public static string ExecuteValidateSceneList()
        {
            var diagnostics = new List<ValidateDiagnosticEntry>();

            var scenes = EditorBuildSettings.scenes;
            if (scenes == null || scenes.Length == 0)
            {
                diagnostics.Add(new ValidateDiagnosticEntry
                {
                    severity = "Warning",
                    errorCode = "VSC001",
                    message = "EditorBuildSettings.scenes is empty — no scenes will be included in builds"
                });
            }
            else
            {
                for (var i = 0; i < scenes.Length; i++)
                {
                    var scene = scenes[i];
                    if (string.IsNullOrWhiteSpace(scene.path))
                    {
                        diagnostics.Add(new ValidateDiagnosticEntry
                        {
                            severity = "Error",
                            errorCode = "VSC002",
                            message = $"Build scene index {i} has an empty path",
                            assetPath = ""
                        });
                        continue;
                    }

                    var guid = AssetDatabase.AssetPathToGUID(scene.path);
                    if (string.IsNullOrEmpty(guid) || !File.Exists(scene.path))
                    {
                        diagnostics.Add(new ValidateDiagnosticEntry
                        {
                            severity = "Error",
                            errorCode = "VSC003",
                            message = $"Build scene '{scene.path}' (index {i}) does not exist on disk",
                            assetPath = scene.path,
                            fixable = true
                        });
                    }
                    else if (!scene.enabled)
                    {
                        diagnostics.Add(new ValidateDiagnosticEntry
                        {
                            severity = "Info",
                            errorCode = "VSC004",
                            message = $"Build scene '{scene.path}' (index {i}) is disabled",
                            assetPath = scene.path
                        });
                    }
                }
            }

            return BuildResultJson("scene-list", diagnostics);
        }

        // ── validate-missing-scripts ─────────────────────────────────────

        public static string ExecuteValidateMissingScripts()
        {
            var diagnostics = new List<ValidateDiagnosticEntry>();

            // 1. Scan all loaded scenes
            for (var s = 0; s < SceneManager.sceneCount; s++)
            {
                var scene = SceneManager.GetSceneAt(s);
                if (!scene.isLoaded) continue;

                var rootObjects = scene.GetRootGameObjects();
                foreach (var root in rootObjects)
                {
                    ScanGameObjectForMissingScripts(root, diagnostics, scene.path);
                }
            }

            // 2. Scan all prefab assets
            var prefabGuids = AssetDatabase.FindAssets("t:Prefab");
            foreach (var guid in prefabGuids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path)) continue;

                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null) continue;

                ScanGameObjectForMissingScripts(prefab, diagnostics, null, path);
            }

            return BuildResultJson("missing-scripts", diagnostics);
        }

        private static void ScanGameObjectForMissingScripts(
            GameObject go,
            List<ValidateDiagnosticEntry> diagnostics,
            string? sceneContext,
            string? prefabAssetPath = null)
        {
            var components = go.GetComponents<Component>();
            for (var c = 0; c < components.Length; c++)
            {
                if (components[c] == null)
                {
                    diagnostics.Add(new ValidateDiagnosticEntry
                    {
                        severity = "Error",
                        errorCode = "VMS001",
                        message = $"Missing script on '{GetGameObjectPath(go)}' (component index {c})",
                        objectPath = GetGameObjectPath(go),
                        sceneContext = sceneContext ?? "",
                        assetPath = prefabAssetPath ?? "",
                        fixable = true
                    });
                }
            }

            for (var i = 0; i < go.transform.childCount; i++)
            {
                ScanGameObjectForMissingScripts(go.transform.GetChild(i).gameObject, diagnostics, sceneContext, prefabAssetPath);
            }
        }

        private static string GetGameObjectPath(GameObject go)
        {
            var path = go.name;
            var current = go.transform.parent;
            while (current != null)
            {
                path = current.name + "/" + path;
                current = current.parent;
            }
            return path;
        }

        // ── validate-build-settings ──────────────────────────────────────

        public static string ExecuteValidateBuildSettings()
        {
            var diagnostics = new List<ValidateDiagnosticEntry>();

            // Bundle identifier
            var bundleId = PlayerSettings.applicationIdentifier;
            if (string.IsNullOrWhiteSpace(bundleId))
            {
                diagnostics.Add(new ValidateDiagnosticEntry
                {
                    severity = "Error",
                    errorCode = "VBS001",
                    message = "Player Settings: application identifier (bundle ID) is empty"
                });
            }
            else if (bundleId.Contains(" "))
            {
                diagnostics.Add(new ValidateDiagnosticEntry
                {
                    severity = "Error",
                    errorCode = "VBS002",
                    message = $"Player Settings: application identifier '{bundleId}' contains spaces"
                });
            }

            // Product name
            if (string.IsNullOrWhiteSpace(PlayerSettings.productName))
            {
                diagnostics.Add(new ValidateDiagnosticEntry
                {
                    severity = "Warning",
                    errorCode = "VBS003",
                    message = "Player Settings: productName is empty"
                });
            }

            // Company name
            if (string.IsNullOrWhiteSpace(PlayerSettings.companyName) ||
                PlayerSettings.companyName == "DefaultCompany")
            {
                diagnostics.Add(new ValidateDiagnosticEntry
                {
                    severity = "Warning",
                    errorCode = "VBS004",
                    message = $"Player Settings: companyName is '{PlayerSettings.companyName}' (default or empty)"
                });
            }

            // Bundle version
            var version = PlayerSettings.bundleVersion;
            if (string.IsNullOrWhiteSpace(version))
            {
                diagnostics.Add(new ValidateDiagnosticEntry
                {
                    severity = "Warning",
                    errorCode = "VBS005",
                    message = "Player Settings: bundleVersion is empty"
                });
            }
            else if (version == "0.1" || version == "1.0")
            {
                diagnostics.Add(new ValidateDiagnosticEntry
                {
                    severity = "Info",
                    errorCode = "VBS006",
                    message = $"Player Settings: bundleVersion is '{version}' (likely default)"
                });
            }

            // Active build target sanity
            var activeTarget = EditorUserBuildSettings.activeBuildTarget;
            var activeGroup = EditorUserBuildSettings.selectedBuildTargetGroup;
            if (activeGroup == BuildTargetGroup.Unknown)
            {
                diagnostics.Add(new ValidateDiagnosticEntry
                {
                    severity = "Warning",
                    errorCode = "VBS007",
                    message = $"Active build target group is Unknown (target: {activeTarget})"
                });
            }

            // Check scene list consistency
            var enabledScenes = EditorBuildSettings.scenes?.Where(s => s.enabled).ToArray() ?? Array.Empty<EditorBuildSettingsScene>();
            if (enabledScenes.Length == 0)
            {
                diagnostics.Add(new ValidateDiagnosticEntry
                {
                    severity = "Warning",
                    errorCode = "VBS008",
                    message = "No enabled scenes in build settings — builds will produce empty players"
                });
            }

            // Scripting backend info
            var scriptingBackend = PlayerSettings.GetScriptingBackend(NamedBuildTarget.FromBuildTargetGroup(activeGroup));
            diagnostics.Add(new ValidateDiagnosticEntry
            {
                severity = "Info",
                errorCode = "VBS009",
                message = $"Active config: target={activeTarget}, group={activeGroup}, scripting={scriptingBackend}, scenes={enabledScenes.Length}"
            });

            return BuildResultJson("build-settings", diagnostics);
        }

        // ── validate-asset-refs ──────────────────────────────────────────

        public static string ExecuteValidateAssetRefs()
        {
            var diagnostics = new List<ValidateDiagnosticEntry>();
            var extensions = new[] { ".unity", ".prefab", ".asset", ".mat", ".controller" };
            var allPaths = AssetDatabase.GetAllAssetPaths()
                .Where(p => p.StartsWith("Assets/", StringComparison.Ordinal) && extensions.Any(e => p.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
                .ToArray();

            diagnostics.Add(new ValidateDiagnosticEntry
            {
                severity = "Info",
                errorCode = "VAR002",
                message = $"scanned {allPaths.Length} asset file(s)"
            });

            var brokenGuids = new Dictionary<string, string>();
            var guidPattern = new Regex(@"guid: ([0-9a-f]{32})", RegexOptions.Compiled);

            foreach (var assetPath in allPaths)
            {
                string text;
                try
                {
                    text = File.ReadAllText(assetPath);
                }
                catch
                {
                    continue;
                }

                foreach (Match match in guidPattern.Matches(text))
                {
                    var guid = match.Groups[1].Value;
                    if (brokenGuids.ContainsKey(guid))
                        continue;
                    var resolved = AssetDatabase.GUIDToAssetPath(guid);
                    if (string.IsNullOrEmpty(resolved))
                    {
                        brokenGuids[guid] = assetPath;
                    }
                }

                if (diagnostics.Count(d => d.severity == "Error") >= 500)
                    break;
            }

            foreach (var kvp in brokenGuids)
            {
                if (diagnostics.Count(d => d.severity == "Error") >= 500)
                {
                    diagnostics.Add(new ValidateDiagnosticEntry
                    {
                        severity = "Warning",
                        errorCode = "VAR000",
                        message = "too many broken refs — showing first 500"
                    });
                    break;
                }

                diagnostics.Add(new ValidateDiagnosticEntry
                {
                    severity = "Error",
                    errorCode = "VAR001",
                    message = $"broken guid reference: {kvp.Key}",
                    assetPath = kvp.Value
                });
            }

            return BuildResultJson("asset-refs", diagnostics);
        }

        // ── validate-addressables ────────────────────────────────────────

        public static string ExecuteValidateAddressables()
        {
            var diagnostics = new List<ValidateDiagnosticEntry>();
            var manifestPath = Path.Combine(Application.dataPath, "..", "Packages", "manifest.json");

            if (!File.Exists(manifestPath))
            {
                diagnostics.Add(new ValidateDiagnosticEntry
                {
                    severity = "Info",
                    errorCode = "VADR000",
                    message = "not installed — skipping"
                });
                return BuildResultJson("addressables", diagnostics);
            }

            var manifestText = File.ReadAllText(manifestPath);
            if (!manifestText.Contains("\"com.unity.addressables\"", StringComparison.Ordinal))
            {
                diagnostics.Add(new ValidateDiagnosticEntry
                {
                    severity = "Info",
                    errorCode = "VADR000",
                    message = "not installed — skipping"
                });
                return BuildResultJson("addressables", diagnostics);
            }

            const string settingsPath = "Assets/AddressableAssetsData/AddressableAssetSettings.asset";
            const string groupsDir = "Assets/AddressableAssetsData/AssetGroups";

            if (!File.Exists(Path.Combine(Application.dataPath, "..", settingsPath)))
            {
                diagnostics.Add(new ValidateDiagnosticEntry
                {
                    severity = "Error",
                    errorCode = "VADR001",
                    message = "AddressableAssetSettings.asset not found",
                    assetPath = settingsPath
                });
            }
            else
            {
                var groupsDirFull = Path.Combine(Application.dataPath, "..", groupsDir);
                if (!Directory.Exists(groupsDirFull))
                {
                    diagnostics.Add(new ValidateDiagnosticEntry
                    {
                        severity = "Warning",
                        errorCode = "VADR002",
                        message = "AssetGroups directory not found",
                        assetPath = groupsDir
                    });
                }
                else
                {
                    var assetGroupCount = Directory.GetFiles(groupsDirFull, "*.asset").Length;
                    diagnostics.Add(new ValidateDiagnosticEntry
                    {
                        severity = "Info",
                        errorCode = "VADR003",
                        message = $"{assetGroupCount} asset group(s) found",
                        assetPath = groupsDir
                    });
                }

                var loaded = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(settingsPath);
                if (loaded == null)
                {
                    diagnostics.Add(new ValidateDiagnosticEntry
                    {
                        severity = "Warning",
                        errorCode = "VADR004",
                        message = "AddressableAssetSettings.asset could not be loaded",
                        assetPath = settingsPath
                    });
                }
            }

            return BuildResultJson("addressables", diagnostics);
        }

        // ── shared helpers ───────────────────────────────────────────────

        private static string BuildResultJson(string validator, List<ValidateDiagnosticEntry> diagnostics)
        {
            var errorCount = diagnostics.Count(d => d.severity == "Error");
            var warningCount = diagnostics.Count(d => d.severity == "Warning");
            var result = new ValidateResultEnvelope
            {
                validator = validator,
                passed = errorCount == 0,
                errorCount = errorCount,
                warningCount = warningCount,
                diagnostics = diagnostics.ToArray()
            };

            var resultJson = JsonUtility.ToJson(result);
            var response = new ProjectCommandResponse
            {
                ok = true,
                message = errorCount == 0
                    ? $"{validator} valid"
                    : $"{validator}: {errorCount} error(s), {warningCount} warning(s)",
                kind = "validate",
                content = resultJson
            };

            return JsonUtility.ToJson(response);
        }

        // ── serialization models (JsonUtility requires plain classes) ────

        [Serializable]
        internal sealed class ValidateDiagnosticEntry
        {
            public string severity = "Info";
            public string errorCode = "";
            public string message = "";
            public string assetPath = "";
            public string objectPath = "";
            public string sceneContext = "";
            public bool fixable;
        }

        [Serializable]
        internal sealed class ValidateResultEnvelope
        {
            public string validator = "";
            public bool passed;
            public int errorCount;
            public int warningCount;
            public ValidateDiagnosticEntry[] diagnostics = Array.Empty<ValidateDiagnosticEntry>();
        }
    }
}
#endif
