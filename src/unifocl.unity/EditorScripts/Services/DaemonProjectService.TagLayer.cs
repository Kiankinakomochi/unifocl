#if UNITY_EDITOR
#nullable enable
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UniFocl.EditorBridge
{
    internal static partial class DaemonProjectService
    {
        // Built-in Unity tags that cannot be removed
        private static readonly string[] BuiltInTagNames =
        {
            "Untagged", "Respawn", "Finish", "EditorOnly", "MainCamera", "Player", "GameController"
        };

        // ── tag-list ─────────────────────────────────────────────────────

        private static string ExecuteTagList()
        {
            try
            {
                var so = LoadTagManagerSo();
                var tagsProp = so.FindProperty("tags");

                var entries = new List<TagLayerTagEntry>();
                foreach (var bi in BuiltInTagNames)
                    entries.Add(new TagLayerTagEntry { name = bi, builtIn = true });

                if (tagsProp != null)
                {
                    for (var i = 0; i < tagsProp.arraySize; i++)
                    {
                        var tagName = tagsProp.GetArrayElementAtIndex(i).stringValue;
                        if (!string.IsNullOrEmpty(tagName))
                            entries.Add(new TagLayerTagEntry { name = tagName, builtIn = false });
                    }
                }

                var result = new TagLayerTagListResult
                {
                    tags = entries.ToArray(),
                    total = entries.Count,
                    builtIn = BuiltInTagNames.Length,
                    custom = tagsProp?.arraySize ?? 0
                };

                return TagLayerOkResponse("tag-list", JsonUtility.ToJson(result), $"{result.total} tag(s)");
            }
            catch (Exception ex)
            {
                return TagLayerErrorResponse($"tag-list: {ex.Message}");
            }
        }

        // ── tag-add ──────────────────────────────────────────────────────

        private static string ExecuteTagAdd(ProjectCommandRequest request)
        {
            try
            {
                var payload = JsonUtility.FromJson<TagLayerTagMutatePayload>(request.content ?? "{}");
                var name = payload?.name?.Trim() ?? string.Empty;
                if (string.IsNullOrEmpty(name))
                    return TagLayerErrorResponse("tag-add: 'name' is required");

                foreach (var bi in BuiltInTagNames)
                    if (string.Equals(bi, name, StringComparison.OrdinalIgnoreCase))
                        return TagLayerErrorResponse($"tag-add: '{name}' is a built-in tag and already exists");

                var so = LoadTagManagerSo();
                var tagsProp = so.FindProperty("tags");
                if (tagsProp == null)
                    return TagLayerErrorResponse("tag-add: could not access tags property in TagManager");

                for (var i = 0; i < tagsProp.arraySize; i++)
                    if (string.Equals(tagsProp.GetArrayElementAtIndex(i).stringValue, name, StringComparison.OrdinalIgnoreCase))
                        return TagLayerErrorResponse($"tag-add: tag '{name}' already exists");

                so.Update();
                tagsProp.arraySize++;
                tagsProp.GetArrayElementAtIndex(tagsProp.arraySize - 1).stringValue = name;
                so.ApplyModifiedProperties();
                AssetDatabase.SaveAssets();

                return TagLayerOkResponse("tag-add", null, $"tag '{name}' added");
            }
            catch (Exception ex)
            {
                return TagLayerErrorResponse($"tag-add: {ex.Message}");
            }
        }

        // ── tag-remove ───────────────────────────────────────────────────

        private static string ExecuteTagRemove(ProjectCommandRequest request)
        {
            try
            {
                var payload = JsonUtility.FromJson<TagLayerTagMutatePayload>(request.content ?? "{}");
                var name = payload?.name?.Trim() ?? string.Empty;
                if (string.IsNullOrEmpty(name))
                    return TagLayerErrorResponse("tag-remove: 'name' is required");

                foreach (var bi in BuiltInTagNames)
                    if (string.Equals(bi, name, StringComparison.OrdinalIgnoreCase))
                        return TagLayerErrorResponse($"tag-remove: '{name}' is a built-in tag and cannot be removed");

                var so = LoadTagManagerSo();
                var tagsProp = so.FindProperty("tags");
                if (tagsProp == null)
                    return TagLayerErrorResponse("tag-remove: could not access tags property in TagManager");

                var idx = -1;
                for (var i = 0; i < tagsProp.arraySize; i++)
                    if (string.Equals(tagsProp.GetArrayElementAtIndex(i).stringValue, name, StringComparison.OrdinalIgnoreCase))
                    { idx = i; break; }

                if (idx < 0)
                    return TagLayerErrorResponse($"tag-remove: tag '{name}' not found");

                so.Update();
                tagsProp.DeleteArrayElementAtIndex(idx);
                so.ApplyModifiedProperties();
                AssetDatabase.SaveAssets();

                return TagLayerOkResponse("tag-remove", null, $"tag '{name}' removed");
            }
            catch (Exception ex)
            {
                return TagLayerErrorResponse($"tag-remove: {ex.Message}");
            }
        }

        // ── layer-list ───────────────────────────────────────────────────

        private static string ExecuteLayerList()
        {
            try
            {
                var so = LoadTagManagerSo();
                var layersProp = so.FindProperty("layers");
                if (layersProp == null)
                    return TagLayerErrorResponse("layer-list: could not access layers property in TagManager");

                var entries = new List<TagLayerLayerEntry>();
                for (var i = 0; i < layersProp.arraySize && i < 32; i++)
                {
                    var layerName = layersProp.GetArrayElementAtIndex(i).stringValue;
                    if (!string.IsNullOrEmpty(layerName))
                        entries.Add(new TagLayerLayerEntry { index = i, name = layerName });
                }

                var result = new TagLayerLayerListResult
                {
                    layers = entries.ToArray(),
                    total = entries.Count
                };

                return TagLayerOkResponse("layer-list", JsonUtility.ToJson(result), $"{entries.Count} layer(s)");
            }
            catch (Exception ex)
            {
                return TagLayerErrorResponse($"layer-list: {ex.Message}");
            }
        }

        // ── layer-add ────────────────────────────────────────────────────

        private static string ExecuteLayerAdd(ProjectCommandRequest request)
        {
            try
            {
                var payload = JsonUtility.FromJson<TagLayerLayerAddPayload>(request.content ?? "{}");
                var name = payload?.name?.Trim() ?? string.Empty;
                if (string.IsNullOrEmpty(name))
                    return TagLayerErrorResponse("layer-add: 'name' is required");

                var so = LoadTagManagerSo();
                var layersProp = so.FindProperty("layers");
                if (layersProp == null)
                    return TagLayerErrorResponse("layer-add: could not access layers property in TagManager");

                // Reject duplicate names across all 32 slots
                for (var i = 0; i < layersProp.arraySize && i < 32; i++)
                    if (string.Equals(layersProp.GetArrayElementAtIndex(i).stringValue, name, StringComparison.OrdinalIgnoreCase))
                        return TagLayerErrorResponse($"layer-add: layer '{name}' already exists at index {i}");

                // Determine target slot
                int targetIndex;
                if (payload != null && payload.index > 0)
                {
                    targetIndex = payload.index;
                    if (targetIndex < 8 || targetIndex > 31)
                        return TagLayerErrorResponse($"layer-add: --index must be 8-31 (got {targetIndex})");
                    var existing = layersProp.GetArrayElementAtIndex(targetIndex).stringValue;
                    if (!string.IsNullOrEmpty(existing))
                        return TagLayerErrorResponse($"layer-add: slot {targetIndex} is already occupied by '{existing}'");
                }
                else
                {
                    targetIndex = -1;
                    for (var i = 8; i < 32 && i < layersProp.arraySize; i++)
                    {
                        if (string.IsNullOrEmpty(layersProp.GetArrayElementAtIndex(i).stringValue))
                        { targetIndex = i; break; }
                    }

                    if (targetIndex < 0)
                        return TagLayerErrorResponse("layer-add: no empty user layer slots available (8-31)");
                }

                so.Update();
                layersProp.GetArrayElementAtIndex(targetIndex).stringValue = name;
                so.ApplyModifiedProperties();
                AssetDatabase.SaveAssets();

                var resultContent = JsonUtility.ToJson(new TagLayerLayerMutateResult { index = targetIndex });
                return TagLayerOkResponse("layer-add", resultContent, $"layer '{name}' added at index {targetIndex}");
            }
            catch (Exception ex)
            {
                return TagLayerErrorResponse($"layer-add: {ex.Message}");
            }
        }

        // ── layer-rename ─────────────────────────────────────────────────

        private static string ExecuteLayerRename(ProjectCommandRequest request)
        {
            try
            {
                var payload = JsonUtility.FromJson<TagLayerLayerRenamePayload>(request.content ?? "{}");
                var nameOrIndex = payload?.nameOrIndex?.Trim() ?? string.Empty;
                var newName = payload?.newName?.Trim() ?? string.Empty;
                if (string.IsNullOrEmpty(nameOrIndex))
                    return TagLayerErrorResponse("layer-rename: 'nameOrIndex' is required");
                if (string.IsNullOrEmpty(newName))
                    return TagLayerErrorResponse("layer-rename: 'newName' is required");

                var so = LoadTagManagerSo();
                var layersProp = so.FindProperty("layers");
                if (layersProp == null)
                    return TagLayerErrorResponse("layer-rename: could not access layers property in TagManager");

                var idx = ResolveLayerIndex(layersProp, nameOrIndex);
                if (idx < 0)
                    return TagLayerErrorResponse($"layer-rename: layer '{nameOrIndex}' not found");
                if (idx < 8)
                    return TagLayerErrorResponse($"layer-rename: cannot rename built-in layer at index {idx}");

                so.Update();
                layersProp.GetArrayElementAtIndex(idx).stringValue = newName;
                so.ApplyModifiedProperties();
                AssetDatabase.SaveAssets();

                return TagLayerOkResponse("layer-rename", null, $"layer {idx} renamed to '{newName}'");
            }
            catch (Exception ex)
            {
                return TagLayerErrorResponse($"layer-rename: {ex.Message}");
            }
        }

        // ── layer-remove ─────────────────────────────────────────────────

        private static string ExecuteLayerRemove(ProjectCommandRequest request)
        {
            try
            {
                var payload = JsonUtility.FromJson<TagLayerSelectorPayload>(request.content ?? "{}");
                var nameOrIndex = payload?.nameOrIndex?.Trim() ?? string.Empty;
                if (string.IsNullOrEmpty(nameOrIndex))
                    return TagLayerErrorResponse("layer-remove: 'nameOrIndex' is required");

                var so = LoadTagManagerSo();
                var layersProp = so.FindProperty("layers");
                if (layersProp == null)
                    return TagLayerErrorResponse("layer-remove: could not access layers property in TagManager");

                var idx = ResolveLayerIndex(layersProp, nameOrIndex);
                if (idx < 0)
                    return TagLayerErrorResponse($"layer-remove: layer '{nameOrIndex}' not found");
                if (idx < 8)
                    return TagLayerErrorResponse($"layer-remove: cannot remove built-in layer at index {idx}");

                so.Update();
                layersProp.GetArrayElementAtIndex(idx).stringValue = string.Empty;
                so.ApplyModifiedProperties();
                AssetDatabase.SaveAssets();

                return TagLayerOkResponse("layer-remove", null, $"layer slot {idx} cleared");
            }
            catch (Exception ex)
            {
                return TagLayerErrorResponse($"layer-remove: {ex.Message}");
            }
        }

        // ── private helpers ───────────────────────────────────────────────

        private static SerializedObject LoadTagManagerSo()
        {
            var assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset");
            if (assets == null || assets.Length == 0)
                throw new InvalidOperationException("ProjectSettings/TagManager.asset not found");
            return new SerializedObject(assets[0]);
        }

        private static int ResolveLayerIndex(SerializedProperty layersProp, string nameOrIndex)
        {
            if (int.TryParse(nameOrIndex, out var idx))
            {
                if (idx < 0 || idx > 31)
                    return -1;
                return string.IsNullOrEmpty(layersProp.GetArrayElementAtIndex(idx).stringValue) ? -1 : idx;
            }

            for (var i = 0; i < layersProp.arraySize && i < 32; i++)
                if (string.Equals(layersProp.GetArrayElementAtIndex(i).stringValue, nameOrIndex, StringComparison.OrdinalIgnoreCase))
                    return i;

            return -1;
        }

        private static string TagLayerOkResponse(string kind, string? contentJson, string message)
            => JsonUtility.ToJson(new ProjectCommandResponse
            {
                ok = true,
                message = message,
                kind = kind,
                content = contentJson ?? string.Empty
            });

        private static string TagLayerErrorResponse(string message)
            => JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = message });

        // ── serialization models ──────────────────────────────────────────

        [Serializable]
        private sealed class TagLayerTagListResult
        {
            public TagLayerTagEntry[] tags = Array.Empty<TagLayerTagEntry>();
            public int total;
            public int builtIn;
            public int custom;
        }

        [Serializable]
        private sealed class TagLayerTagEntry
        {
            public string name = string.Empty;
            public bool builtIn;
        }

        [Serializable]
        private sealed class TagLayerTagMutatePayload
        {
            public string name = string.Empty;
        }

        [Serializable]
        private sealed class TagLayerLayerListResult
        {
            public TagLayerLayerEntry[] layers = Array.Empty<TagLayerLayerEntry>();
            public int total;
        }

        [Serializable]
        private sealed class TagLayerLayerEntry
        {
            public int index;
            public string name = string.Empty;
        }

        [Serializable]
        private sealed class TagLayerLayerAddPayload
        {
            public string name = string.Empty;
            public int index;  // 0 = auto-detect first empty user slot (8-31)
        }

        [Serializable]
        private sealed class TagLayerLayerRenamePayload
        {
            public string nameOrIndex = string.Empty;
            public string newName = string.Empty;
        }

        [Serializable]
        private sealed class TagLayerSelectorPayload
        {
            public string nameOrIndex = string.Empty;
        }

        [Serializable]
        private sealed class TagLayerLayerMutateResult
        {
            public int index;
        }
    }
}
#endif
