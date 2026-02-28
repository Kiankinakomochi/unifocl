#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace UniFocl.EditorBridge
{
    internal static class DaemonAssetIndexService
    {
        private static bool _dirty = true;
        private static int _revision = 1;
        private static readonly object Sync = new();

        public static string BuildPayload(int? knownRevision)
        {
            lock (Sync)
            {
                if (_dirty)
                {
                    _revision++;
                }

                if (knownRevision.HasValue && knownRevision.Value == _revision && !_dirty)
                {
                    return JsonUtility.ToJson(new AssetIndexSyncResponse
                    {
                        revision = _revision,
                        unchanged = true,
                        entries = Array.Empty<AssetIndexEntry>()
                    });
                }

                var paths = CollectPathsFromSearchService();
                if (paths.Count == 0)
                {
                    paths = FallbackEnumerateAssetPaths();
                }

                var entries = new AssetIndexEntry[paths.Count];
                for (var i = 0; i < paths.Count; i++)
                {
                    var path = paths[i];
                    entries[i] = new AssetIndexEntry
                    {
                        instanceId = StablePathId(path),
                        path = path
                    };
                }

                _dirty = false;
                return JsonUtility.ToJson(new AssetIndexSyncResponse
                {
                    revision = _revision,
                    unchanged = false,
                    entries = entries
                });
            }
        }

        public static void MarkDirty()
        {
            lock (Sync)
            {
                _dirty = true;
            }
        }

        private static List<string> CollectPathsFromSearchService()
        {
            // Reflection keeps this package resilient across Unity versions where SearchService signatures differ.
            var paths = new List<string>();
            try
            {
                var editorAssembly = typeof(EditorApplication).Assembly;
                var searchServiceType = editorAssembly.GetType("UnityEditor.Search.SearchService");
                if (searchServiceType is null)
                {
                    return paths;
                }

                var requestMethod = searchServiceType
                    .GetMethods()
                    .FirstOrDefault(m => m.Name == "Request" && m.GetParameters().Length >= 1 && m.GetParameters()[0].ParameterType == typeof(string));
                if (requestMethod is null)
                {
                    return paths;
                }

                var requestResult = requestMethod.Invoke(null, new object[] { "p:" });
                if (requestResult is not IEnumerable enumerable)
                {
                    return paths;
                }

                foreach (var item in enumerable)
                {
                    if (item is null)
                    {
                        continue;
                    }

                    var itemType = item.GetType();
                    var idProp = itemType.GetProperty("id");
                    var value = idProp?.GetValue(item) as string;
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        continue;
                    }

                    if (!value.StartsWith("Assets/", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (value.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    paths.Add(value);
                }
            }
            catch
            {
            }

            return paths
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static List<string> FallbackEnumerateAssetPaths()
        {
            var list = new List<string>();
            var root = Application.dataPath;
            if (!Directory.Exists(root))
            {
                return list;
            }

            foreach (var absolutePath in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                if (absolutePath.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var relative = "Assets/" + Path.GetRelativePath(root, absolutePath).Replace('\\', '/');
                list.Add(relative);
            }

            return list
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static int StablePathId(string path)
        {
            unchecked
            {
                uint hash = 2166136261;
                foreach (var ch in path)
                {
                    hash ^= char.ToLowerInvariant(ch);
                    hash *= 16777619;
                }

                return (int)(hash & 0x7FFFFFFF);
            }
        }
    }
}
#endif
