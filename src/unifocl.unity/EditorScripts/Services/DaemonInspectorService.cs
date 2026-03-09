#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UniFocl.EditorBridge
{
    internal static class DaemonInspectorService
    {
        public static string Execute(string payload)
        {
            InspectorBridgeRequest? request;
            try
            {
                request = JsonUtility.FromJson<InspectorBridgeRequest>(payload);
            }
            catch
            {
                return JsonUtility.ToJson(new InspectorMutationResponse { ok = false });
            }

            if (request is null || string.IsNullOrWhiteSpace(request.action))
            {
                return JsonUtility.ToJson(new InspectorMutationResponse { ok = false });
            }

            switch (request.action)
            {
                case "list-components":
                    return JsonUtility.ToJson(new InspectorComponentsResponse
                    {
                        ok = true,
                        components = GetComponents(request.targetPath)
                    });

                case "list-fields":
                    return JsonUtility.ToJson(new InspectorFieldsResponse
                    {
                        ok = true,
                        fields = GetFields(request.targetPath, request.componentIndex, request.componentName)
                    });

                case "find":
                    return JsonUtility.ToJson(new InspectorSearchResponse
                    {
                        ok = true,
                        results = Find(request.targetPath, request.query, request.componentName)
                    });

                case "toggle-component":
                    return JsonUtility.ToJson(new InspectorMutationResponse
                    {
                        ok = ToggleComponent(request.targetPath, request.componentIndex, request.componentName)
                    });

                case "toggle-field":
                    return JsonUtility.ToJson(new InspectorMutationResponse
                    {
                        ok = ToggleField(request.targetPath, request.componentIndex, request.componentName, request.fieldName)
                    });

                case "set-field":
                    return JsonUtility.ToJson(new InspectorMutationResponse
                    {
                        ok = SetField(request.targetPath, request.componentIndex, request.componentName, request.fieldName, request.value)
                    });

                default:
                    return JsonUtility.ToJson(new InspectorMutationResponse { ok = false });
            }
        }

        private static InspectorComponentEntry[] GetComponents(string? targetPath)
        {
            var target = ResolveTarget(targetPath);
            if (target is null)
            {
                return Array.Empty<InspectorComponentEntry>();
            }

            var components = target.GetComponents<Component>();
            var result = new List<InspectorComponentEntry>(components.Length);
            for (var i = 0; i < components.Length; i++)
            {
                var component = components[i];
                if (component is null)
                {
                    continue;
                }

                result.Add(new InspectorComponentEntry
                {
                    index = i,
                    name = component.GetType().Name,
                    enabled = GetComponentEnabled(component)
                });
            }

            return result.ToArray();
        }

        private static InspectorFieldEntry[] GetFields(string? targetPath, int componentIndex, string? componentName)
        {
            var component = ResolveComponent(targetPath, componentIndex, componentName);
            if (component is null)
            {
                return Array.Empty<InspectorFieldEntry>();
            }

            var serializedObject = new SerializedObject(component);
            var iterator = serializedObject.GetIterator();
            var entries = new List<InspectorFieldEntry>();
            var enterChildren = true;

            while (iterator.NextVisible(enterChildren))
            {
                enterChildren = false;
                if (string.Equals(iterator.name, "m_Script", StringComparison.Ordinal))
                {
                    continue;
                }

                entries.Add(new InspectorFieldEntry
                {
                    name = iterator.propertyPath,
                    value = FormatPropertyValue(iterator),
                    type = iterator.propertyType.ToString(),
                    isBoolean = iterator.propertyType == SerializedPropertyType.Boolean,
                    enumOptions = GetEnumOptions(iterator)
                });
            }

            return entries.ToArray();
        }

        private static InspectorSearchResult[] Find(string? targetPath, string? query, string? componentName)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return Array.Empty<InspectorSearchResult>();
            }

            var target = ResolveTarget(targetPath);
            if (target is null)
            {
                return Array.Empty<InspectorSearchResult>();
            }

            var results = new List<InspectorSearchResult>();
            var components = target.GetComponents<Component>();
            for (var i = 0; i < components.Length; i++)
            {
                var component = components[i];
                if (component is null)
                {
                    continue;
                }

                var name = component.GetType().Name;
                if (TryScore(query, name, out var componentScore))
                {
                    results.Add(new InspectorSearchResult
                    {
                        scope = "component",
                        componentIndex = i,
                        name = name,
                        path = name,
                        score = componentScore
                    });
                }

                if (!string.IsNullOrWhiteSpace(componentName)
                    && !name.Equals(componentName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (var field in EnumerateFields(component))
                {
                    if (!TryScore(query, field.name, out var fieldScore))
                    {
                        continue;
                    }

                    results.Add(new InspectorSearchResult
                    {
                        scope = "field",
                        componentIndex = i,
                        name = field.name,
                        path = $"{name}.{field.name}",
                        score = fieldScore
                    });
                }
            }

            return results
                .OrderByDescending(result => result.score)
                .ThenBy(result => result.path, StringComparer.OrdinalIgnoreCase)
                .Take(20)
                .ToArray();
        }

        private static bool ToggleComponent(string? targetPath, int componentIndex, string? componentName)
        {
            var component = ResolveComponent(targetPath, componentIndex, componentName);
            if (component is null)
            {
                return false;
            }

            if (component is Behaviour behaviour)
            {
                Undo.RecordObject(behaviour, "unifocl toggle component enabled");
                behaviour.enabled = !behaviour.enabled;
                EditorUtility.SetDirty(behaviour);
                PersistComponentSceneChanges(behaviour);
                return true;
            }

            if (component is Renderer renderer)
            {
                Undo.RecordObject(renderer, "unifocl toggle renderer enabled");
                renderer.enabled = !renderer.enabled;
                EditorUtility.SetDirty(renderer);
                PersistComponentSceneChanges(renderer);
                return true;
            }

            if (component is Collider collider)
            {
                Undo.RecordObject(collider, "unifocl toggle collider enabled");
                collider.enabled = !collider.enabled;
                EditorUtility.SetDirty(collider);
                PersistComponentSceneChanges(collider);
                return true;
            }

            return false;
        }

        private static bool ToggleField(string? targetPath, int componentIndex, string? componentName, string? fieldName)
        {
            var component = ResolveComponent(targetPath, componentIndex, componentName);
            if (component is null || string.IsNullOrWhiteSpace(fieldName))
            {
                return false;
            }

            var serializedObject = new SerializedObject(component);
            var property = FindPropertyByNameOrPath(serializedObject, fieldName);
            if (property is null || property.propertyType != SerializedPropertyType.Boolean)
            {
                return false;
            }

            Undo.RecordObject(component, "unifocl toggle field");
            serializedObject.Update();
            property.boolValue = !property.boolValue;
            serializedObject.ApplyModifiedProperties();
            EditorUtility.SetDirty(component);
            PersistComponentSceneChanges(component);
            return true;
        }

        private static bool SetField(string? targetPath, int componentIndex, string? componentName, string? fieldName, string? rawValue)
        {
            var component = ResolveComponent(targetPath, componentIndex, componentName);
            if (component is null || string.IsNullOrWhiteSpace(fieldName) || rawValue is null)
            {
                return false;
            }

            var serializedObject = new SerializedObject(component);
            var property = FindPropertyByNameOrPath(serializedObject, fieldName);
            if (property is null)
            {
                return false;
            }

            Undo.RecordObject(component, "unifocl set field");
            serializedObject.Update();
            if (!TryAssignPropertyValue(property, rawValue))
            {
                return false;
            }

            serializedObject.ApplyModifiedProperties();
            EditorUtility.SetDirty(component);
            PersistComponentSceneChanges(component);
            return true;
        }

        private static void PersistComponentSceneChanges(Component component)
        {
            var scene = component.gameObject.scene;
            if (!scene.IsValid())
            {
                return;
            }

            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene))
            {
                Debug.LogWarning($"[unifocl] Failed to save scene '{scene.name}' (path: '{scene.path}') after inspector mutation.");
            }
        }

        private static Component? ResolveComponent(string? targetPath, int componentIndex, string? componentName)
        {
            var target = ResolveTarget(targetPath);
            if (target is null)
            {
                return null;
            }

            var components = target.GetComponents<Component>();
            if (componentIndex >= 0 && componentIndex < components.Length)
            {
                return components[componentIndex];
            }

            if (!string.IsNullOrWhiteSpace(componentName))
            {
                return components.FirstOrDefault(component =>
                    component is not null && component.GetType().Name.Equals(componentName, StringComparison.OrdinalIgnoreCase));
            }

            return null;
        }

        private static GameObject? ResolveTarget(string? targetPath)
        {
            var scene = SceneManager.GetActiveScene();
            if (!scene.IsValid())
            {
                return null;
            }

            var roots = scene.GetRootGameObjects();
            if (roots.Length == 0)
            {
                return null;
            }

            var normalized = (targetPath ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalized) || normalized == "/")
            {
                return Selection.activeGameObject ?? roots[0];
            }

            var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
            if (segments.Count == 0)
            {
                return Selection.activeGameObject ?? roots[0];
            }

            if (segments[0].Equals(scene.name, StringComparison.OrdinalIgnoreCase))
            {
                segments.RemoveAt(0);
            }

            if (segments.Count == 0)
            {
                return Selection.activeGameObject ?? roots[0];
            }

            var current = roots.FirstOrDefault(root => root.name.Equals(segments[0], StringComparison.Ordinal));
            if (current is null)
            {
                return FindByName(roots, segments[^1]);
            }

            for (var i = 1; i < segments.Count; i++)
            {
                var next = FindDirectChildByName(current.transform, segments[i]);
                if (next is null)
                {
                    return FindByName(roots, segments[^1]);
                }

                current = next;
            }

            return current;
        }

        private static GameObject? FindDirectChildByName(Transform parent, string name)
        {
            for (var i = 0; i < parent.childCount; i++)
            {
                var child = parent.GetChild(i);
                if (child.name.Equals(name, StringComparison.Ordinal))
                {
                    return child.gameObject;
                }
            }

            return null;
        }

        private static GameObject? FindByName(IEnumerable<GameObject> roots, string name)
        {
            foreach (var root in roots)
            {
                if (root.name.Equals(name, StringComparison.Ordinal))
                {
                    return root;
                }

                var match = FindByNameRecursive(root.transform, name);
                if (match is not null)
                {
                    return match;
                }
            }

            return null;
        }

        private static GameObject? FindByNameRecursive(Transform node, string name)
        {
            for (var i = 0; i < node.childCount; i++)
            {
                var child = node.GetChild(i);
                if (child.name.Equals(name, StringComparison.Ordinal))
                {
                    return child.gameObject;
                }

                var nested = FindByNameRecursive(child, name);
                if (nested is not null)
                {
                    return nested;
                }
            }

            return null;
        }

        private static IEnumerable<InspectorFieldEntry> EnumerateFields(Component component)
        {
            var serializedObject = new SerializedObject(component);
            var iterator = serializedObject.GetIterator();
            var enterChildren = true;
            while (iterator.NextVisible(enterChildren))
            {
                enterChildren = false;
                if (string.Equals(iterator.name, "m_Script", StringComparison.Ordinal))
                {
                    continue;
                }

                yield return new InspectorFieldEntry
                {
                    name = iterator.propertyPath,
                    value = FormatPropertyValue(iterator),
                    type = iterator.propertyType.ToString(),
                    isBoolean = iterator.propertyType == SerializedPropertyType.Boolean,
                    enumOptions = GetEnumOptions(iterator)
                };
            }
        }

        private static string[] GetEnumOptions(SerializedProperty property)
        {
            if (property.propertyType != SerializedPropertyType.Enum || property.enumDisplayNames is null)
            {
                return Array.Empty<string>();
            }

            return property.enumDisplayNames
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToArray();
        }

        private static SerializedProperty? FindPropertyByNameOrPath(SerializedObject serializedObject, string nameOrPath)
        {
            var direct = serializedObject.FindProperty(nameOrPath);
            if (direct is not null)
            {
                return direct;
            }

            var iterator = serializedObject.GetIterator();
            var enterChildren = true;
            while (iterator.NextVisible(enterChildren))
            {
                enterChildren = false;
                if (string.Equals(iterator.name, "m_Script", StringComparison.Ordinal))
                {
                    continue;
                }

                if (iterator.name.Equals(nameOrPath, StringComparison.Ordinal)
                    || iterator.propertyPath.Equals(nameOrPath, StringComparison.Ordinal))
                {
                    return serializedObject.FindProperty(iterator.propertyPath);
                }
            }

            return null;
        }

        private static bool TryAssignPropertyValue(SerializedProperty property, string rawValue)
        {
            switch (property.propertyType)
            {
                case SerializedPropertyType.Boolean:
                    if (bool.TryParse(rawValue, out var boolValue))
                    {
                        property.boolValue = boolValue;
                        return true;
                    }

                    return false;

                case SerializedPropertyType.Integer:
                    if (int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue))
                    {
                        property.intValue = intValue;
                        return true;
                    }

                    return false;

                case SerializedPropertyType.Float:
                    if (float.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var floatValue))
                    {
                        property.floatValue = floatValue;
                        return true;
                    }

                    return false;

                case SerializedPropertyType.String:
                    property.stringValue = rawValue;
                    return true;

                case SerializedPropertyType.Vector2:
                    if (TryParseVector(rawValue, 2, out var vector2))
                    {
                        property.vector2Value = new Vector2(vector2[0], vector2[1]);
                        return true;
                    }

                    return false;

                case SerializedPropertyType.Vector3:
                    if (TryParseVector(rawValue, 3, out var vector3))
                    {
                        property.vector3Value = new Vector3(vector3[0], vector3[1], vector3[2]);
                        return true;
                    }

                    return false;

                case SerializedPropertyType.Vector4:
                    if (TryParseVector(rawValue, 4, out var vector4))
                    {
                        property.vector4Value = new Vector4(vector4[0], vector4[1], vector4[2], vector4[3]);
                        return true;
                    }

                    return false;

                case SerializedPropertyType.Color:
                    if (TryParseVector(rawValue, 4, out var rgba))
                    {
                        property.colorValue = new Color(rgba[0], rgba[1], rgba[2], rgba[3]);
                        return true;
                    }

                    if (ColorUtility.TryParseHtmlString(rawValue, out var htmlColor))
                    {
                        property.colorValue = htmlColor;
                        return true;
                    }

                    return false;

                case SerializedPropertyType.Enum:
                    {
                        var enumNames = property.enumDisplayNames;
                        for (var i = 0; i < enumNames.Length; i++)
                        {
                            if (enumNames[i].Equals(rawValue, StringComparison.OrdinalIgnoreCase))
                            {
                                property.enumValueIndex = i;
                                return true;
                            }
                        }

                        if (int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var enumIndex)
                            && enumIndex >= 0
                            && enumIndex < enumNames.Length)
                        {
                            property.enumValueIndex = enumIndex;
                            return true;
                        }

                        return false;
                    }

                default:
                    return false;
            }
        }

        private static string FormatPropertyValue(SerializedProperty property)
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
                    return string.Format(CultureInfo.InvariantCulture, "({0:0.###}, {1:0.###}, {2:0.###}, {3:0.###})", c.r, c.g, c.b, c.a);
                case SerializedPropertyType.Vector2:
                    var v2 = property.vector2Value;
                    return string.Format(CultureInfo.InvariantCulture, "({0:0.###}, {1:0.###})", v2.x, v2.y);
                case SerializedPropertyType.Vector3:
                    var v3 = property.vector3Value;
                    return string.Format(CultureInfo.InvariantCulture, "({0:0.###}, {1:0.###}, {2:0.###})", v3.x, v3.y, v3.z);
                case SerializedPropertyType.Vector4:
                    var v4 = property.vector4Value;
                    return string.Format(CultureInfo.InvariantCulture, "({0:0.###}, {1:0.###}, {2:0.###}, {3:0.###})", v4.x, v4.y, v4.z, v4.w);
                case SerializedPropertyType.Enum:
                    if (property.enumValueIndex >= 0 && property.enumValueIndex < property.enumDisplayNames.Length)
                    {
                        return property.enumDisplayNames[property.enumValueIndex];
                    }

                    return property.enumValueIndex.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.ObjectReference:
                    return property.objectReferenceValue is null ? "null" : property.objectReferenceValue.name;
                default:
                    return property.displayName;
            }
        }

        private static bool GetComponentEnabled(Component component)
        {
            if (component is Behaviour behaviour)
            {
                return behaviour.enabled;
            }

            if (component is Renderer renderer)
            {
                return renderer.enabled;
            }

            if (component is Collider collider)
            {
                return collider.enabled;
            }

            return true;
        }

        private static bool TryScore(string query, string candidate, out double score)
        {
            score = 0;
            if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(candidate))
            {
                return false;
            }

            var q = query.Trim().ToLowerInvariant();
            var c = candidate.ToLowerInvariant();
            var containsIndex = c.IndexOf(q, StringComparison.Ordinal);
            if (containsIndex >= 0)
            {
                score = 1.0 - (containsIndex / (double)Math.Max(1, c.Length));
                return true;
            }

            var qi = 0;
            var ci = 0;
            var hits = 0;
            while (qi < q.Length && ci < c.Length)
            {
                if (q[qi] == c[ci])
                {
                    hits++;
                    qi++;
                }

                ci++;
            }

            if (qi != q.Length)
            {
                return false;
            }

            score = hits / (double)Math.Max(q.Length, c.Length);
            return true;
        }

        private static bool TryParseVector(string raw, int size, out float[] values)
        {
            values = Array.Empty<float>();
            var normalized = raw.Trim();
            if (normalized.StartsWith("(", StringComparison.Ordinal) && normalized.EndsWith(")", StringComparison.Ordinal) && normalized.Length >= 2)
            {
                normalized = normalized[1..^1];
            }

            var parts = normalized
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Trim())
                .Where(part => part.Length > 0)
                .ToArray();
            if (parts.Length != size)
            {
                return false;
            }

            values = new float[size];
            for (var i = 0; i < parts.Length; i++)
            {
                if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
#endif
