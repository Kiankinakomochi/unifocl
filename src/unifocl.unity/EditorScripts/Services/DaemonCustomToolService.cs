#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace UniFocl.EditorBridge
{
    /// <summary>
    /// Reflection-based dispatcher for custom tools registered with <see cref="UnifoclCommandAttribute"/>.
    /// Invoked from the MCP bridge when the daemon receives an <c>execute_custom_tool</c> request.
    /// </summary>
    internal static class DaemonCustomToolService
    {
        // Keyed by tool Name (from UnifoclCommandAttribute.Name), populated lazily.
        private static Dictionary<string, MethodInfo>? _methodCache;

        public static string ExecuteCustomTool(string toolName, string argsJson, bool dryRun = false)
        {
            if (string.IsNullOrWhiteSpace(toolName))
            {
                return ErrorResponse("tool name is required");
            }

            var method = FindMethod(toolName);
            if (method is null)
            {
                return ErrorResponse($"no method found for tool '{toolName}'");
            }

            try
            {
                var parameters = method.GetParameters();
                var args = BuildArgs(parameters, argsJson ?? "{}");
                return dryRun
                    ? InvokeWithDryRun(method, args, toolName)
                    : InvokeDirect(method, args);
            }
            catch (TargetInvocationException tie)
            {
                var msg = tie.InnerException?.Message ?? tie.Message;
                Debug.LogWarning($"[unifocl] custom tool '{toolName}' threw: {msg}");
                return ErrorResponse($"tool execution failed: {msg}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[unifocl] custom tool '{toolName}' error: {ex.Message}");
                return ErrorResponse($"tool execution failed: {ex.Message}");
            }
        }

        private static string InvokeDirect(MethodInfo method, object?[] args)
        {
            var raw = method.Invoke(null, args);
            var result = raw?.ToString() ?? string.Empty;
            return JsonUtility.ToJson(new CustomToolBridgeResponse { ok = true, result = result });
        }

        /// <summary>
        /// Executes the method inside a sandboxed Undo group, then immediately reverts all
        /// Unity-tracked changes. Relies on the user's code calling <c>Undo.RecordObject</c>
        /// before modifying components, which is standard practice for Unity editor scripts.
        /// Raw System.IO writes are NOT reverted — this is a documented limitation.
        /// </summary>
        private static string InvokeWithDryRun(MethodInfo method, object?[] args, string toolName)
        {
            // Capture the current Undo group index before execution.
            var undoGroupBefore = Undo.GetCurrentGroup();
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName($"[unifocl dry-run] {toolName}");

            // Enter the ambient dry-run context so any built-in unifocl services
            // (e.g. DaemonScenePersistenceService) also suppress their durable writes.
            IDisposable dryRunScope = DaemonDryRunContext.Enter();
            string? invokeResult = null;

            try
            {
                var raw = method.Invoke(null, args);
                invokeResult = raw?.ToString();
            }
            catch (TargetInvocationException tie)
            {
                var msg = tie.InnerException?.Message ?? tie.Message;
                Debug.LogWarning($"[unifocl] custom tool '{toolName}' dry-run threw: {msg}");

                // Still revert whatever Unity recorded before the exception.
                Undo.RevertAllDownToGroup(undoGroupBefore);
                AssetDatabase.Refresh();
                dryRunScope.Dispose();
                return ErrorResponse($"tool execution failed during dry-run: {msg}");
            }
            finally
            {
                // Always dispose the ambient scope, whether or not we already did above.
                // IDisposable.Dispose is safe to call multiple times on the ExitScope impl.
                dryRunScope.Dispose();
            }

            // Revert all Unity Undo-tracked changes made inside the group.
            Undo.RevertAllDownToGroup(undoGroupBefore);

            // Resync the asset database to a clean state after the undo.
            AssetDatabase.Refresh();

            var summary = $"Dry-run of '{toolName}' completed. Unity Undo-tracked changes were reverted. " +
                          "Raw file I/O (System.IO) is not guaranteed to have been prevented.";
            if (invokeResult is not null)
            {
                summary += $" Tool returned: {invokeResult}";
            }

            return JsonUtility.ToJson(new CustomToolBridgeResponse
            {
                ok = true,
                result = summary,
                message = "dry-run"
            });
        }

        private static MethodInfo? FindMethod(string toolName)
        {
            if (_methodCache is null)
            {
                ScanAssemblies();
            }

            _methodCache!.TryGetValue(toolName, out var method);
            return method;
        }

        private static void ScanAssemblies()
        {
            _methodCache = new Dictionary<string, MethodInfo>(StringComparer.OrdinalIgnoreCase);

            // TypeCache is the fast Unity-native way to find attributed methods.
            var methods = TypeCache.GetMethodsWithAttribute<UnifoclCommandAttribute>();
            foreach (var method in methods)
            {
                var attr = method.GetCustomAttribute<UnifoclCommandAttribute>();
                if (attr is null || string.IsNullOrWhiteSpace(attr.Name))
                {
                    continue;
                }

                if (!_methodCache.ContainsKey(attr.Name))
                {
                    _methodCache[attr.Name] = method;
                }
            }
        }

        /// <summary>
        /// Converts a flat JSON object string into an argument array matching <paramref name="parameters"/>.
        /// Supports string, int, long, float, double, and bool parameter types.
        /// Complex types receive the raw argsJson string.
        /// </summary>
        private static object?[] BuildArgs(ParameterInfo[] parameters, string argsJson)
        {
            if (parameters.Length == 0)
            {
                return Array.Empty<object?>();
            }

            var result = new object?[parameters.Length];
            for (var i = 0; i < parameters.Length; i++)
            {
                var param = parameters[i];
                var raw = ExtractField(argsJson, param.Name ?? string.Empty);

                if (raw is null && param.HasDefaultValue)
                {
                    result[i] = param.DefaultValue;
                    continue;
                }

                result[i] = CoerceValue(raw, param.ParameterType);
            }

            return result;
        }

        private static object? CoerceValue(string? raw, Type targetType)
        {
            if (raw is null)
            {
                return targetType.IsValueType ? Activator.CreateInstance(targetType) : null;
            }

            if (targetType == typeof(string))  return raw;
            if (targetType == typeof(bool))    return raw.Equals("true", StringComparison.OrdinalIgnoreCase);
            if (targetType == typeof(int))     return int.TryParse(raw, out var i) ? i : 0;
            if (targetType == typeof(long))    return long.TryParse(raw, out var l) ? l : 0L;
            if (targetType == typeof(float))   return float.TryParse(raw, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : 0f;
            if (targetType == typeof(double))  return double.TryParse(raw, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0d;

            // Fallback: pass raw JSON string for unknown types
            return raw;
        }

        /// <summary>
        /// Extracts the raw value string for <paramref name="key"/> from a flat JSON object.
        /// Returns null if the key is not found.
        /// </summary>
        private static string? ExtractField(string json, string key)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            var search = $"\"{key}\":";
            var idx = json.IndexOf(search, StringComparison.Ordinal);
            if (idx < 0)
            {
                return null;
            }

            var pos = idx + search.Length;
            while (pos < json.Length && json[pos] == ' ') pos++;
            if (pos >= json.Length) return null;

            if (json[pos] == '"')
            {
                // String value — handle simple escapes
                var end = pos + 1;
                while (end < json.Length)
                {
                    if (json[end] == '\\') { end += 2; continue; }
                    if (json[end] == '"') break;
                    end++;
                }

                return end < json.Length ? json.Substring(pos + 1, end - pos - 1).Replace("\\\"", "\"").Replace("\\\\", "\\").Replace("\\n", "\n").Replace("\\r", "\r").Replace("\\t", "\t") : null;
            }

            // Non-string value: read until delimiter
            var end2 = pos;
            while (end2 < json.Length && json[end2] != ',' && json[end2] != '}' && json[end2] != ']')
            {
                end2++;
            }

            var token = json.Substring(pos, end2 - pos).Trim();
            return token.Length > 0 ? token : null;
        }

        private static string ErrorResponse(string message)
        {
            return JsonUtility.ToJson(new CustomToolBridgeResponse { ok = false, message = message });
        }
    }
}
#endif
