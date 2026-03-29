#if UNITY_EDITOR
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Compilation;
using UnityEngine;

namespace UniFocl.EditorBridge
{
    /// <summary>
    /// Daemon-side handlers for all <c>diag</c> operations (sprints 4 + 5).
    /// </summary>
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

            // Read errors from the event-based compilation state cache.
            // Unity surfaces per-message file/line context inside the message string
            // (e.g. "Assets/Foo.cs(42,5): error CS0246: ..."), so we preserve the
            // full text and let consumers parse as needed.
            var errorTexts = DaemonProjectService.GetLastCompileErrors();
            var messages = errorTexts.Select(e => new DiagCompilerMessage
            {
                message = e,
                type = "Error"
            }).ToArray();

            var result = new DiagCompileErrorsResult
            {
                assemblyCount = assemblies.Length,
                errorCount = messages.Length,
                warningCount = 0,
                messages = messages
            };
            return BuildDiagResponse("compile-errors", JsonUtility.ToJson(result),
                $"{assemblies.Length} assembl(ies), {messages.Length} error(s)");
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

        // ── diag asset-size ──────────────────────────────────────────────────

        public static string ExecuteDiagAssetSize()
        {
            const int MaxAssets = 1000;

            try
            {
                var allPaths = AssetDatabase.GetAllAssetPaths()
                    .Where(p => p.StartsWith("Assets/", StringComparison.Ordinal))
                    .ToArray();

                var entries = new List<AssetSizeEntry>(Math.Min(allPaths.Length, MaxAssets));

                foreach (var assetPath in allPaths)
                {
                    if (entries.Count >= MaxAssets)
                    {
                        break;
                    }

                    var fullPath = Path.GetFullPath(assetPath);
                    if (!File.Exists(fullPath))
                    {
                        continue;
                    }

                    long sizeBytes;
                    try
                    {
                        sizeBytes = new FileInfo(fullPath).Length;
                    }
                    catch
                    {
                        continue;
                    }

                    // Dependency count — GetDependencies is synchronous and can be slow for
                    // large projects; we cap it at 500 assets queried.
                    string[] deps = Array.Empty<string>();
                    if (entries.Count < 500)
                    {
                        try
                        {
                            deps = AssetDatabase.GetDependencies(assetPath, recursive: false);
                        }
                        catch
                        {
                            // best-effort
                        }
                    }

                    entries.Add(new AssetSizeEntry
                    {
                        path = assetPath,
                        sizeBytes = sizeBytes,
                        depCount = deps.Length,
                        deps = deps
                    });
                }

                // Sort largest-first.
                entries.Sort((a, b) => b.sizeBytes.CompareTo(a.sizeBytes));

                long totalBytes = entries.Sum(e => e.sizeBytes);

                var payload = new AssetSizePayload
                {
                    totalAssets = entries.Count,
                    totalSizeBytes = totalBytes,
                    assets = entries.ToArray()
                };

                return JsonUtility.ToJson(new ProjectCommandResponse
                {
                    ok = true,
                    message = $"scanned {entries.Count} asset(s), total {FormatBytes(totalBytes)}",
                    kind = "diag-asset-size",
                    content = JsonUtility.ToJson(payload)
                });
            }
            catch (Exception ex)
            {
                return JsonUtility.ToJson(new ProjectCommandResponse
                {
                    ok = false,
                    message = $"diag asset-size failed: {ex.Message}",
                    kind = "diag-asset-size"
                });
            }
        }

        // ── diag import-hotspots ─────────────────────────────────────────────

        public static string ExecuteDiagImportHotspots()
        {
            const int TopN = 50;

            try
            {
                var store = DaemonImportTimingStore.ReadStore();
                if (store.entries == null || store.entries.Length == 0)
                {
                    return JsonUtility.ToJson(new ProjectCommandResponse
                    {
                        ok = false,
                        message = "no import timing data found — assets must be imported at least once while the daemon is running",
                        kind = "diag-import-hotspots"
                    });
                }

                // Aggregate per asset path: count appearances across batches.
                // batchDurationMs is 0 for most entries (Unity does not expose it directly),
                // so we use importCount as the primary hotspot signal.
                var countMap = new Dictionary<string, int>(StringComparer.Ordinal);

                foreach (var batch in store.entries)
                {
                    if (batch.assetPaths == null)
                    {
                        continue;
                    }

                    foreach (var path in batch.assetPaths)
                    {
                        if (string.IsNullOrEmpty(path))
                        {
                            continue;
                        }

                        countMap.TryGetValue(path, out var existing);
                        countMap[path] = existing + 1;
                    }
                }

                var hotspots = countMap
                    .Select(kv => new ImportHotspotEntry
                    {
                        assetPath = kv.Key,
                        importCount = kv.Value
                    })
                    .OrderByDescending(e => e.importCount)
                    .Take(TopN)
                    .ToArray();

                var payload = new ImportHotspotPayload
                {
                    batchesRecorded = store.entries.Length,
                    uniqueAssetsTracked = countMap.Count,
                    hotspots = hotspots
                };

                return JsonUtility.ToJson(new ProjectCommandResponse
                {
                    ok = true,
                    message = $"analysed {store.entries.Length} import batch(es), {countMap.Count} unique asset(s)",
                    kind = "diag-import-hotspots",
                    content = JsonUtility.ToJson(payload)
                });
            }
            catch (Exception ex)
            {
                return JsonUtility.ToJson(new ProjectCommandResponse
                {
                    ok = false,
                    message = $"diag import-hotspots failed: {ex.Message}",
                    kind = "diag-import-hotspots"
                });
            }
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

        private static string FormatBytes(long bytes)
        {
            if (bytes >= 1_073_741_824) return $"{bytes / 1_073_741_824.0:0.##} GB";
            if (bytes >= 1_048_576)     return $"{bytes / 1_048_576.0:0.##} MB";
            if (bytes >= 1_024)         return $"{bytes / 1_024.0:0.##} KB";
            return $"{bytes} B";
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

        [Serializable]
        private sealed class AssetSizePayload
        {
            public int totalAssets;
            public long totalSizeBytes;
            public AssetSizeEntry[] assets = Array.Empty<AssetSizeEntry>();
        }

        [Serializable]
        internal sealed class AssetSizeEntry
        {
            public string path = "";
            public long sizeBytes;
            public int depCount;
            public string[] deps = Array.Empty<string>();
        }

        [Serializable]
        private sealed class ImportHotspotPayload
        {
            public int batchesRecorded;
            public int uniqueAssetsTracked;
            public ImportHotspotEntry[] hotspots = Array.Empty<ImportHotspotEntry>();
        }

        [Serializable]
        internal sealed class ImportHotspotEntry
        {
            public string assetPath = "";
            public int importCount;
        }
    }
}
#endif
