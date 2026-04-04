using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace UniFocl.Runtime
{
    /// <summary>Risk level for runtime commands, mirroring the CLI-side ExecRiskLevel.</summary>
    public enum RuntimeRiskLevel
    {
        SafeRead,
        PrivilegedExec
    }

    /// <summary>Kind discriminator for runtime capabilities.</summary>
    public enum RuntimeCommandKind
    {
        Query,
        Command,
        Stream
    }

    /// <summary>Metadata about a discovered runtime command.</summary>
    public struct RuntimeCommandInfo
    {
        public string name;
        public string description;
        public string category;
        public RuntimeCommandKind kind;
        public RuntimeRiskLevel risk;
        public string argsSchemaJson;
        public string resultSchemaJson;
    }

    /// <summary>
    /// Discovers <see cref="UnifoclRuntimeCommandAttribute"/>-marked methods at player startup,
    /// registers their handlers with <see cref="UnifoclRuntimeClient"/>, and builds a typed
    /// manifest for capability discovery.
    /// </summary>
    public static class RuntimeCommandRegistry
    {
        private static readonly Dictionary<string, RuntimeCommandInfo> Commands =
            new(StringComparer.OrdinalIgnoreCase);

        private static bool _discovered;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Discover()
        {
            if (_discovered) return;
            _discovered = true;

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch
                {
                    continue;
                }

                foreach (var type in types)
                {
                    foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                    {
                        var attr = method.GetCustomAttribute<UnifoclRuntimeCommandAttribute>();
                        if (attr == null) continue;

                        var info = new RuntimeCommandInfo
                        {
                            name = attr.Name,
                            description = attr.Description,
                            category = string.IsNullOrEmpty(attr.Category) ? "default" : attr.Category,
                            kind = attr.Kind,
                            risk = attr.Risk,
                            argsSchemaJson = BuildArgsSchema(method),
                            resultSchemaJson = "{\"type\":\"object\"}"
                        };

                        Commands[info.name] = info;

                        // Capture method for closure
                        var capturedMethod = method;
                        UnifoclRuntimeClient.RegisterHandler(info.name, argsJson =>
                        {
                            var parameters = capturedMethod.GetParameters();
                            object[] invokeArgs;

                            if (parameters.Length == 0)
                            {
                                invokeArgs = Array.Empty<object>();
                            }
                            else if (parameters.Length == 1 && parameters[0].ParameterType == typeof(string))
                            {
                                invokeArgs = new object[] { argsJson };
                            }
                            else
                            {
                                invokeArgs = new object[] { argsJson };
                            }

                            var result = capturedMethod.Invoke(null, invokeArgs);
                            return result == null ? "{}" : JsonUtility.ToJson(result);
                        });
                    }
                }
            }

            Debug.Log($"[unifocl.runtime] discovered {Commands.Count} runtime commands");
        }

        public static RuntimeCommandInfo? GetCommandInfo(string commandName)
        {
            return Commands.TryGetValue(commandName, out var info) ? info : null;
        }

        /// <summary>Build the full runtime manifest as JSON.</summary>
        public static string BuildManifestJson()
        {
            var sb = new StringBuilder();
            sb.Append("{\"schemaVersion\":\"1\",\"categories\":[");

            var categoryMap = new Dictionary<string, List<RuntimeCommandInfo>>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in Commands)
            {
                if (!categoryMap.TryGetValue(kv.Value.category, out var list))
                {
                    list = new List<RuntimeCommandInfo>();
                    categoryMap[kv.Value.category] = list;
                }
                list.Add(kv.Value);
            }

            var firstCat = true;
            foreach (var kv in categoryMap)
            {
                if (!firstCat) sb.Append(',');
                firstCat = false;

                sb.Append("{\"name\":\"");
                sb.Append(EscapeJson(kv.Key));
                sb.Append("\",\"source\":\"runtime\",\"tools\":[");

                var firstTool = true;
                foreach (var cmd in kv.Value)
                {
                    if (!firstTool) sb.Append(',');
                    firstTool = false;

                    sb.Append("{\"name\":\"");
                    sb.Append(EscapeJson(cmd.name));
                    sb.Append("\",\"description\":\"");
                    sb.Append(EscapeJson(cmd.description));
                    sb.Append("\",\"kind\":\"");
                    sb.Append(cmd.kind.ToString().ToLowerInvariant());
                    sb.Append("\",\"risk\":\"");
                    sb.Append(cmd.risk == RuntimeRiskLevel.SafeRead ? "SafeRead" : "PrivilegedExec");
                    sb.Append("\",\"argsSchema\":");
                    sb.Append(cmd.argsSchemaJson);
                    sb.Append(",\"resultSchema\":");
                    sb.Append(cmd.resultSchemaJson);
                    sb.Append('}');
                }

                sb.Append("]}");
            }

            sb.Append("]}");
            return sb.ToString();
        }

        private static string BuildArgsSchema(MethodInfo method)
        {
            var parameters = method.GetParameters();
            if (parameters.Length == 0)
            {
                return "{\"type\":\"object\",\"properties\":{}}";
            }

            // If the method takes a single string (raw JSON), schema is open
            if (parameters.Length == 1 && parameters[0].ParameterType == typeof(string))
            {
                return "{\"type\":\"object\"}";
            }

            var sb = new StringBuilder();
            sb.Append("{\"type\":\"object\",\"properties\":{");
            var required = new List<string>();
            var first = true;

            foreach (var param in parameters)
            {
                if (!first) sb.Append(',');
                first = false;

                var name = param.Name ?? param.Position.ToString();
                sb.Append('"');
                sb.Append(EscapeJson(name));
                sb.Append("\":{\"type\":\"");
                sb.Append(MapType(param.ParameterType));
                sb.Append("\"}");

                if (!param.IsOptional && !param.HasDefaultValue)
                {
                    required.Add(name);
                }
            }

            sb.Append('}');
            if (required.Count > 0)
            {
                sb.Append(",\"required\":[");
                for (var i = 0; i < required.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('"');
                    sb.Append(EscapeJson(required[i]));
                    sb.Append('"');
                }
                sb.Append(']');
            }

            sb.Append('}');
            return sb.ToString();
        }

        private static string MapType(Type type)
        {
            if (type == typeof(string)) return "string";
            if (type == typeof(int) || type == typeof(long) || type == typeof(short)) return "integer";
            if (type == typeof(float) || type == typeof(double) || type == typeof(decimal)) return "number";
            if (type == typeof(bool)) return "boolean";
            return "string";
        }

        private static string EscapeJson(string value)
        {
            return value
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r")
                .Replace("\t", "\\t");
        }
    }
}
