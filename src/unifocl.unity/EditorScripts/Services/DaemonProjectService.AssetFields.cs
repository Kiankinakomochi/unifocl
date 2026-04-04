#if UNITY_EDITOR
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEngine;

namespace UniFocl.EditorBridge
{
    internal static partial class DaemonProjectService
    {
        // ──────────────────────────────────────────────────────────────
        //  asset-get
        // ──────────────────────────────────────────────────────────────

        private static string ExecuteAssetGet(ProjectCommandRequest request)
        {
            if (!IsValidAssetPath(request.assetPath))
            {
                return AssetFieldsError("asset-get requires a valid assetPath");
            }

            var payload = string.IsNullOrEmpty(request.content)
                ? null
                : JsonUtility.FromJson<AssetFieldsGetPayload>(request.content);
            var fieldName = payload?.field?.Trim() ?? string.Empty;

            UnityEngine.Object? target;
            bool isImporter;
            if (!TryLoadAssetOrImporter(request.assetPath, out target, out isImporter))
            {
                return AssetFieldsError($"asset-get: could not load asset or importer for: {request.assetPath}");
            }

            try
            {
                var so = new SerializedObject(target!);
                so.Update();

                if (!string.IsNullOrEmpty(fieldName))
                {
                    var prop = AssetFindProperty(so, fieldName);
                    if (prop is null)
                    {
                        return AssetFieldsError($"asset-get: field '{fieldName}' not found");
                    }

                    var single = new AssetFieldEntry
                    {
                        name = prop.propertyPath,
                        type = prop.propertyType.ToString(),
                        value = AssetFormatPropertyValue(prop)
                    };
                    var singleResult = new AssetFieldsGetResult
                    {
                        assetPath = request.assetPath,
                        isImporter = isImporter,
                        fields = new[] { single }
                    };
                    return JsonUtility.ToJson(new ProjectCommandResponse
                    {
                        ok = true,
                        message = $"1 field",
                        kind = "asset-get",
                        content = JsonUtility.ToJson(singleResult)
                    });
                }

                var entries = new List<AssetFieldEntry>();
                var iterator = so.GetIterator();
                var enterChildren = true;
                while (iterator.NextVisible(enterChildren))
                {
                    enterChildren = false;
                    if (string.Equals(iterator.name, "m_Script", StringComparison.Ordinal))
                        continue;

                    entries.Add(new AssetFieldEntry
                    {
                        name = iterator.propertyPath,
                        type = iterator.propertyType.ToString(),
                        value = AssetFormatPropertyValue(iterator)
                    });
                }

                var allResult = new AssetFieldsGetResult
                {
                    assetPath = request.assetPath,
                    isImporter = isImporter,
                    fields = entries.ToArray()
                };
                return JsonUtility.ToJson(new ProjectCommandResponse
                {
                    ok = true,
                    message = $"{entries.Count} field(s)",
                    kind = "asset-get",
                    content = JsonUtility.ToJson(allResult)
                });
            }
            catch (Exception ex)
            {
                return AssetFieldsError($"asset-get: {ex.Message}");
            }
        }

        // ──────────────────────────────────────────────────────────────
        //  asset-set
        // ──────────────────────────────────────────────────────────────

        private static string ExecuteAssetSet(ProjectCommandRequest request)
        {
            if (!IsValidAssetPath(request.assetPath))
            {
                return AssetFieldsError("asset-set requires a valid assetPath");
            }

            var payload = string.IsNullOrEmpty(request.content)
                ? null
                : JsonUtility.FromJson<AssetFieldsSetPayload>(request.content);

            var fieldName = payload?.field?.Trim() ?? string.Empty;
            var rawValue = payload?.value;

            if (string.IsNullOrEmpty(fieldName))
            {
                return AssetFieldsError("asset-set: 'field' is required");
            }

            if (rawValue is null)
            {
                return AssetFieldsError("asset-set: 'value' is required");
            }

            UnityEngine.Object? target;
            bool isImporter;
            if (!TryLoadAssetOrImporter(request.assetPath, out target, out isImporter))
            {
                return AssetFieldsError($"asset-set: could not load asset or importer for: {request.assetPath}");
            }

            try
            {
                var so = new SerializedObject(target!);
                so.Update();

                var prop = AssetFindProperty(so, fieldName);
                if (prop is null)
                {
                    return AssetFieldsError($"asset-set: field '{fieldName}' not found");
                }

                if (!AssetTryAssignPropertyValue(prop, rawValue, out var changed))
                {
                    return AssetFieldsError(
                        $"asset-set: failed to assign field '{prop.propertyPath}' ({prop.propertyType}) from value '{rawValue}'");
                }

                if (changed)
                {
                    so.ApplyModifiedProperties();
                    EditorUtility.SetDirty(target!);
                    if (isImporter)
                    {
                        AssetDatabase.ImportAsset(request.assetPath, ImportAssetOptions.ForceUpdate);
                    }
                    else
                    {
                        AssetDatabase.SaveAssets();
                    }
                }

                return JsonUtility.ToJson(new ProjectCommandResponse
                {
                    ok = true,
                    message = changed ? $"field '{prop.propertyPath}' updated" : $"field '{prop.propertyPath}' unchanged (same value)",
                    kind = "asset-set"
                });
            }
            catch (Exception ex)
            {
                return AssetFieldsError($"asset-set: {ex.Message}");
            }
        }

        // ──────────────────────────────────────────────────────────────
        //  Helpers
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Loads either the asset itself (ScriptableObject / generic .asset) or its AssetImporter,
        /// depending on asset type. Returns false if neither yields a valid target.
        /// </summary>
        private static bool TryLoadAssetOrImporter(string assetPath, out UnityEngine.Object? target, out bool isImporter)
        {
            target = null;
            isImporter = false;

            // Try AssetImporter for media/binary asset types (texture, audio, model, video, font, etc.)
            var ext = System.IO.Path.GetExtension(assetPath).ToLowerInvariant();
            var isMediaAsset = ext is ".png" or ".jpg" or ".jpeg" or ".tga" or ".bmp" or ".psd" or ".gif" or ".hdr" or ".exr"
                or ".wav" or ".mp3" or ".ogg" or ".aiff" or ".aif"
                or ".fbx" or ".obj" or ".dae" or ".3ds" or ".blend"
                or ".mp4" or ".mov" or ".avi" or ".webm"
                or ".ttf" or ".otf";

            if (isMediaAsset)
            {
                var importer = AssetImporter.GetAtPath(assetPath);
                if (importer is not null)
                {
                    target = importer;
                    isImporter = true;
                    return true;
                }
            }

            // Fall back to loading the asset directly (works for .asset / ScriptableObject)
            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
            if (asset is not null)
            {
                target = asset;
                isImporter = false;
                return true;
            }

            // Last resort: try importer anyway
            var fallbackImporter = AssetImporter.GetAtPath(assetPath);
            if (fallbackImporter is not null)
            {
                target = fallbackImporter;
                isImporter = true;
                return true;
            }

            return false;
        }

        private static SerializedProperty? AssetFindProperty(SerializedObject so, string nameOrPath)
        {
            var direct = so.FindProperty(nameOrPath);
            if (direct is not null)
                return direct;

            var iterator = so.GetIterator();
            var enterChildren = true;
            while (iterator.NextVisible(enterChildren))
            {
                enterChildren = false;
                if (string.Equals(iterator.name, "m_Script", StringComparison.Ordinal))
                    continue;

                if (iterator.name.Equals(nameOrPath, StringComparison.Ordinal)
                    || iterator.propertyPath.Equals(nameOrPath, StringComparison.Ordinal))
                {
                    return so.FindProperty(iterator.propertyPath);
                }
            }

            return null;
        }

        private static string AssetFormatPropertyValue(SerializedProperty property)
        {
            switch (property.propertyType)
            {
                case SerializedPropertyType.Boolean:
                    return property.boolValue ? "true" : "false";
                case SerializedPropertyType.Integer:
                    return property.intValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Float:
                    return property.floatValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.String:
                    return property.stringValue ?? string.Empty;
                case SerializedPropertyType.Color:
                    var c = property.colorValue;
                    return string.Format(CultureInfo.InvariantCulture,
                        "({0:0.###}, {1:0.###}, {2:0.###}, {3:0.###})", c.r, c.g, c.b, c.a);
                case SerializedPropertyType.Vector2:
                    var v2 = property.vector2Value;
                    return string.Format(CultureInfo.InvariantCulture, "({0:0.###}, {1:0.###})", v2.x, v2.y);
                case SerializedPropertyType.Vector3:
                    var v3 = property.vector3Value;
                    return string.Format(CultureInfo.InvariantCulture,
                        "({0:0.###}, {1:0.###}, {2:0.###})", v3.x, v3.y, v3.z);
                case SerializedPropertyType.Vector4:
                    var v4 = property.vector4Value;
                    return string.Format(CultureInfo.InvariantCulture,
                        "({0:0.###}, {1:0.###}, {2:0.###}, {3:0.###})", v4.x, v4.y, v4.z, v4.w);
                case SerializedPropertyType.Enum:
                    if (property.enumValueIndex >= 0 && property.enumValueIndex < property.enumDisplayNames.Length)
                        return property.enumDisplayNames[property.enumValueIndex];
                    return property.enumValueIndex.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.ObjectReference:
                    return property.objectReferenceValue is null ? "null" : property.objectReferenceValue.name;
                default:
                    return property.displayName;
            }
        }

        private static bool AssetTryAssignPropertyValue(SerializedProperty property, string rawValue, out bool changed)
        {
            changed = false;
            switch (property.propertyType)
            {
                case SerializedPropertyType.Boolean:
                    if (bool.TryParse(rawValue, out var boolVal))
                    {
                        changed = property.boolValue != boolVal;
                        property.boolValue = boolVal;
                        return true;
                    }
                    return false;

                case SerializedPropertyType.Integer:
                    if (int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intVal))
                    {
                        changed = property.intValue != intVal;
                        property.intValue = intVal;
                        return true;
                    }
                    return false;

                case SerializedPropertyType.Float:
                    if (float.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var floatVal))
                    {
                        if (!float.IsFinite(floatVal))
                            return false;
                        changed = !Mathf.Approximately(property.floatValue, floatVal);
                        property.floatValue = floatVal;
                        return true;
                    }
                    return false;

                case SerializedPropertyType.String:
                    changed = !string.Equals(property.stringValue, rawValue, StringComparison.Ordinal);
                    property.stringValue = rawValue;
                    return true;

                case SerializedPropertyType.Vector2:
                    if (AssetTryParseVector(rawValue, 2, out var vec2))
                    {
                        var nv2 = new Vector2(vec2[0], vec2[1]);
                        changed = property.vector2Value != nv2;
                        property.vector2Value = nv2;
                        return true;
                    }
                    return false;

                case SerializedPropertyType.Vector3:
                    if (AssetTryParseVector(rawValue, 3, out var vec3))
                    {
                        var nv3 = new Vector3(vec3[0], vec3[1], vec3[2]);
                        changed = property.vector3Value != nv3;
                        property.vector3Value = nv3;
                        return true;
                    }
                    return false;

                case SerializedPropertyType.Vector4:
                    if (AssetTryParseVector(rawValue, 4, out var vec4))
                    {
                        var nv4 = new Vector4(vec4[0], vec4[1], vec4[2], vec4[3]);
                        changed = property.vector4Value != nv4;
                        property.vector4Value = nv4;
                        return true;
                    }
                    return false;

                case SerializedPropertyType.Color:
                    if (AssetTryParseVector(rawValue, 4, out var rgba))
                    {
                        var col = new Color(
                            Mathf.Clamp01(rgba[0]),
                            Mathf.Clamp01(rgba[1]),
                            Mathf.Clamp01(rgba[2]),
                            Mathf.Clamp01(rgba[3]));
                        changed = property.colorValue != col;
                        property.colorValue = col;
                        return true;
                    }
                    if (ColorUtility.TryParseHtmlString(rawValue, out var htmlColor))
                    {
                        changed = property.colorValue != htmlColor;
                        property.colorValue = htmlColor;
                        return true;
                    }
                    return false;

                case SerializedPropertyType.Enum:
                    var enumNames = property.enumDisplayNames;
                    for (var i = 0; i < enumNames.Length; i++)
                    {
                        if (enumNames[i].Equals(rawValue, StringComparison.OrdinalIgnoreCase))
                        {
                            changed = property.enumValueIndex != i;
                            property.enumValueIndex = i;
                            return true;
                        }
                    }
                    if (int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var enumIdx)
                        && enumIdx >= 0 && enumIdx < enumNames.Length)
                    {
                        changed = property.enumValueIndex != enumIdx;
                        property.enumValueIndex = enumIdx;
                        return true;
                    }
                    return false;

                default:
                    return false;
            }
        }

        private static bool AssetTryParseVector(string raw, int count, out float[] components)
        {
            components = new float[count];
            var cleaned = raw.Trim().Trim('(', ')');
            var parts = cleaned.Split(',');
            if (parts.Length != count)
                return false;

            for (var i = 0; i < count; i++)
            {
                if (!float.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out components[i]))
                    return false;
            }
            return true;
        }

        private static string AssetFieldsError(string message)
            => JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = message });

        // ──────────────────────────────────────────────────────────────
        //  Serialization models
        // ──────────────────────────────────────────────────────────────

        [Serializable]
        private sealed class AssetFieldsGetPayload
        {
            public string field = string.Empty;
        }

        [Serializable]
        private sealed class AssetFieldsSetPayload
        {
            public string field = string.Empty;
            public string value = string.Empty;
        }

        [Serializable]
        private sealed class AssetFieldEntry
        {
            public string name = string.Empty;
            public string type = string.Empty;
            public string value = string.Empty;
        }

        [Serializable]
        private sealed class AssetFieldsGetResult
        {
            public string assetPath = string.Empty;
            public bool isImporter;
            public AssetFieldEntry[] fields = Array.Empty<AssetFieldEntry>();
        }
    }
}
#endif
