#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

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
                    return JsonUtility.ToJson(new InspectorComponentsResponse { ok = true, components = GetComponents() });
                case "list-fields":
                    {
                        var componentName = request.componentName;
                        if (string.IsNullOrWhiteSpace(componentName) && request.componentIndex >= 0)
                        {
                            componentName = GetComponents().FirstOrDefault(c => c.index == request.componentIndex)?.name;
                        }

                        return JsonUtility.ToJson(new InspectorFieldsResponse
                        {
                            ok = true,
                            fields = GetFields(componentName)
                        });
                    }
                case "find":
                    return JsonUtility.ToJson(new InspectorSearchResponse
                    {
                        ok = true,
                        results = Find(request.query, request.componentName)
                    });
                case "toggle-component":
                case "toggle-field":
                case "set-field":
                    return JsonUtility.ToJson(new InspectorMutationResponse { ok = true });
                default:
                    return JsonUtility.ToJson(new InspectorMutationResponse { ok = false });
            }
        }

        private static InspectorComponentEntry[] GetComponents()
        {
            return new[]
            {
                new InspectorComponentEntry { index = 0, name = "Transform", enabled = true },
                new InspectorComponentEntry { index = 1, name = "Rigidbody", enabled = true },
                new InspectorComponentEntry { index = 2, name = "CapsuleCollider", enabled = true },
                new InspectorComponentEntry { index = 3, name = "PlayerController", enabled = true }
            };
        }

        private static InspectorFieldEntry[] GetFields(string? componentName)
        {
            if (string.Equals(componentName, "PlayerController", StringComparison.OrdinalIgnoreCase))
            {
                return new[]
                {
                    new InspectorFieldEntry { name = "speed", value = "6.5", type = "float", isBoolean = false },
                    new InspectorFieldEntry { name = "jumpForce", value = "12", type = "int", isBoolean = false },
                    new InspectorFieldEntry { name = "grounded", value = "false", type = "bool", isBoolean = true },
                    new InspectorFieldEntry { name = "playerColor", value = "RGBA(1.0, 0.0, 0.0, 1.0)", type = "Color", isBoolean = false },
                    new InspectorFieldEntry { name = "startPos", value = "(0.0, 1.0, 0.0)", type = "Vector3", isBoolean = false }
                };
            }

            return new[]
            {
                new InspectorFieldEntry { name = "enabled", value = "true", type = "bool", isBoolean = true }
            };
        }

        private static InspectorSearchResult[] Find(string? query, string? componentName)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return Array.Empty<InspectorSearchResult>();
            }

            var results = new List<InspectorSearchResult>();
            foreach (var component in GetComponents())
            {
                if (TryScore(query, component.name, out var componentScore))
                {
                    results.Add(new InspectorSearchResult
                    {
                        scope = "component",
                        componentIndex = component.index,
                        name = component.name,
                        path = component.name,
                        score = componentScore
                    });
                }
            }

            foreach (var field in GetFields(componentName))
            {
                if (TryScore(query, field.name, out var fieldScore))
                {
                    var path = string.IsNullOrWhiteSpace(componentName) ? field.name : $"{componentName}.{field.name}";
                    results.Add(new InspectorSearchResult
                    {
                        scope = "field",
                        componentIndex = -1,
                        name = field.name,
                        path = path,
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
    }
}
#endif
