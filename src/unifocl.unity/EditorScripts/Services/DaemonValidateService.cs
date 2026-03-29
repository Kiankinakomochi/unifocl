#if UNITY_EDITOR
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
