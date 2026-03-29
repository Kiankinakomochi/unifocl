#if UNITY_EDITOR
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Compilation;

namespace UniFocl.EditorBridge
{
    internal static class DaemonDiagService
    {
        // ── diag-script-defines ─────────────────────────────────────────

        public static string ExecuteDiagScriptDefines()
        {
            var groups = new[]
            {
                BuildTargetGroup.Standalone,
                BuildTargetGroup.iOS,
                BuildTargetGroup.Android,
                BuildTargetGroup.WebGL,
                BuildTargetGroup.tvOS,
                BuildTargetGroup.PS4,
                BuildTargetGroup.PS5,
                BuildTargetGroup.XboxOne,
                BuildTargetGroup.Switch,
            };

            var entries = new List<DiagScriptDefinesEntry>();
            foreach (var group in groups)
            {
                string defines;
                try
                {
                    defines = PlayerSettings.GetScriptingDefineSymbolsForGroup(group) ?? string.Empty;
                }
                catch
                {
                    defines = string.Empty;
                }

                entries.Add(new DiagScriptDefinesEntry
                {
                    buildTarget = group.ToString(),
                    group = group.ToString(),
                    defines = defines
                });
            }

            var result = new DiagScriptDefinesResult
            {
                targetCount = entries.Count,
                targets = entries.ToArray()
            };
            return BuildDiagResponse("script-defines", JsonUtility.ToJson(result), $"{entries.Count} build target(s)");
        }

        // ── diag-compile-errors ─────────────────────────────────────────

        public static string ExecuteDiagCompileErrors()
        {
            var assemblies = CompilationPipeline.GetAssemblies();
            var messages = new List<DiagCompilerMessage>();

            foreach (var assembly in assemblies)
            {
                if (assembly.compilerMessages == null) continue;
                foreach (var msg in assembly.compilerMessages)
                {
                    messages.Add(new DiagCompilerMessage
                    {
                        assembly = assembly.name,
                        file = msg.file ?? string.Empty,
                        line = msg.line,
                        message = msg.message ?? string.Empty,
                        type = msg.type.ToString()
                    });
                }
            }

            var errorCount = messages.Count(m => m.type == "Error");
            var warningCount = messages.Count(m => m.type == "Warning");
            var result = new DiagCompileErrorsResult
            {
                assemblyCount = assemblies.Length,
                errorCount = errorCount,
                warningCount = warningCount,
                messages = messages.ToArray()
            };
            return BuildDiagResponse("compile-errors", JsonUtility.ToJson(result),
                $"{assemblies.Length} assembl(ies), {errorCount} error(s), {warningCount} warning(s)");
        }

        // ── diag-assembly-graph ─────────────────────────────────────────

        public static string ExecuteDiagAssemblyGraph()
        {
            var assemblies = CompilationPipeline.GetAssemblies();
            var entries = new List<DiagAssemblyEntry>();

            foreach (var assembly in assemblies)
            {
                var refs = assembly.assemblyReferences != null
                    ? assembly.assemblyReferences
                        .Select(a => a.name)
                        .Where(n => !string.IsNullOrEmpty(n))
                        .OrderBy(n => n)
                        .ToArray()
                    : Array.Empty<string>();

                entries.Add(new DiagAssemblyEntry
                {
                    name = assembly.name,
                    refs = string.Join(";", refs)
                });
            }

            entries.Sort((a, b) => string.Compare(a.name, b.name, StringComparison.Ordinal));

            var result = new DiagAssemblyGraphResult
            {
                assemblyCount = entries.Count,
                assemblies = entries.ToArray()
            };
            return BuildDiagResponse("assembly-graph", JsonUtility.ToJson(result), $"{entries.Count} assembl(ies)");
        }

        // ── diag-scene-deps ─────────────────────────────────────────────

        public static string ExecuteDiagSceneDeps()
        {
            var scenes = EditorBuildSettings.scenes
                .Where(s => s.enabled && !string.IsNullOrEmpty(s.path))
                .ToArray();

            var entries = new List<DiagDepEntry>();
            foreach (var scene in scenes)
            {
                string[] deps;
                try
                {
                    deps = AssetDatabase.GetDependencies(scene.path, true)
                        .Where(d => d != scene.path)
                        .OrderBy(d => d)
                        .ToArray();
                }
                catch
                {
                    deps = Array.Empty<string>();
                }

                entries.Add(new DiagDepEntry
                {
                    path = scene.path,
                    depCount = deps.Length,
                    topDeps = string.Join(";", deps.Take(20))
                });
            }

            var result = new DiagSceneDepsResult
            {
                sceneCount = entries.Count,
                scenes = entries.ToArray()
            };
            return BuildDiagResponse("scene-deps", JsonUtility.ToJson(result), $"{entries.Count} scene(s)");
        }

        // ── diag-prefab-deps ────────────────────────────────────────────

        public static string ExecuteDiagPrefabDeps()
        {
            const int maxPrefabs = 100;
            var prefabGuids = AssetDatabase.FindAssets("t:Prefab", new[] { "Assets" });
            var totalCount = prefabGuids.Length;

            var entries = new List<DiagDepEntry>();
            foreach (var guid in prefabGuids.Take(maxPrefabs))
            {
                var prefabPath = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(prefabPath)) continue;

                string[] deps;
                try
                {
                    deps = AssetDatabase.GetDependencies(prefabPath, true)
                        .Where(d => d != prefabPath)
                        .OrderBy(d => d)
                        .ToArray();
                }
                catch
                {
                    deps = Array.Empty<string>();
                }

                entries.Add(new DiagDepEntry
                {
                    path = prefabPath,
                    depCount = deps.Length,
                    topDeps = string.Join(";", deps.Take(20))
                });
            }

            var result = new DiagPrefabDepsResult
            {
                prefabCount = totalCount,
                prefabs = entries.ToArray()
            };
            var summary = totalCount > maxPrefabs
                ? $"{entries.Count} prefab(s) shown (of {totalCount} total, capped at {maxPrefabs})"
                : $"{entries.Count} prefab(s)";
            return BuildDiagResponse("prefab-deps", JsonUtility.ToJson(result), summary);
        }

        // ── shared helpers ───────────────────────────────────────────────

        private static string BuildDiagResponse(string op, string contentJson, string message)
        {
            var response = new ProjectCommandResponse
            {
                ok = true,
                message = message,
                kind = "diag",
                content = contentJson
            };
            return JsonUtility.ToJson(response);
        }

        // ── serialization models (JsonUtility requires plain classes) ────

        [Serializable]
        internal sealed class DiagScriptDefinesEntry
        {
            public string buildTarget = "";
            public string group = "";
            public string defines = "";
        }

        [Serializable]
        internal sealed class DiagScriptDefinesResult
        {
            public string op = "script-defines";
            public int targetCount;
            public DiagScriptDefinesEntry[] targets = Array.Empty<DiagScriptDefinesEntry>();
        }

        [Serializable]
        internal sealed class DiagCompilerMessage
        {
            public string assembly = "";
            public string file = "";
            public int line;
            public string message = "";
            public string type = "";
        }

        [Serializable]
        internal sealed class DiagCompileErrorsResult
        {
            public string op = "compile-errors";
            public int assemblyCount;
            public int errorCount;
            public int warningCount;
            public DiagCompilerMessage[] messages = Array.Empty<DiagCompilerMessage>();
        }

        [Serializable]
        internal sealed class DiagAssemblyEntry
        {
            public string name = "";
            public string refs = "";
        }

        [Serializable]
        internal sealed class DiagAssemblyGraphResult
        {
            public string op = "assembly-graph";
            public int assemblyCount;
            public DiagAssemblyEntry[] assemblies = Array.Empty<DiagAssemblyEntry>();
        }

        [Serializable]
        internal sealed class DiagDepEntry
        {
            public string path = "";
            public int depCount;
            public string topDeps = "";
        }

        [Serializable]
        internal sealed class DiagSceneDepsResult
        {
            public string op = "scene-deps";
            public int sceneCount;
            public DiagDepEntry[] scenes = Array.Empty<DiagDepEntry>();
        }

        [Serializable]
        internal sealed class DiagPrefabDepsResult
        {
            public string op = "prefab-deps";
            public int prefabCount;
            public DiagDepEntry[] prefabs = Array.Empty<DiagDepEntry>();
        }
    }
}
#endif
