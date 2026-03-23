#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace UniFocl.EditorBridge
{
    [InitializeOnLoad]
    internal static class UnifoclManifestGenerator
    {
        static UnifoclManifestGenerator()
        {
            CompilationPipeline.compilationFinished += OnCompilationFinished;
        }

        private static void OnCompilationFinished(object _)
        {
            GenerateManifest();
        }

        [MenuItem("unifocl/Regenerate Tool Manifest")]
        public static void GenerateManifest()
        {
            try
            {
                var methods = TypeCache.GetMethodsWithAttribute<UnifoclCommandAttribute>();
                var categoryMap = new Dictionary<string, List<ToolEntry>>(StringComparer.OrdinalIgnoreCase);

                foreach (var method in methods)
                {
                    var attr = method.GetCustomAttribute<UnifoclCommandAttribute>();
                    if (attr is null)
                    {
                        continue;
                    }

                    var category = string.IsNullOrWhiteSpace(attr.Category) ? "Default" : attr.Category;
                    if (!categoryMap.TryGetValue(category, out var tools))
                    {
                        tools = new List<ToolEntry>();
                        categoryMap[category] = tools;
                    }

                    tools.Add(BuildToolEntry(method, attr));
                }

                var json = SerializeManifest(categoryMap);
                var outputPath = ResolveManifestPath();
                var dir = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.WriteAllText(outputPath, json, Encoding.UTF8);
                Debug.Log($"[unifocl] manifest written: {outputPath} ({categoryMap.Count} categories)");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[unifocl] manifest generation failed: {ex.Message}");
            }
        }

        private static string ResolveManifestPath()
        {
            var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            return Path.Combine(projectRoot, ".local", "unifocl-manifest.json");
        }

        private static ToolEntry BuildToolEntry(MethodInfo method, UnifoclCommandAttribute attr)
        {
            var parameters = method.GetParameters();
            var required = new List<string>();
            var properties = new StringBuilder();
            var first = true;

            foreach (var param in parameters)
            {
                var jsonType = MapToJsonSchemaType(param.ParameterType, out var extra);
                if (!first)
                {
                    properties.Append(',');
                }

                first = false;
                properties.Append($"\"{EscapeJson(param.Name ?? param.Position.ToString())}\":{{\"type\":\"{jsonType}\"");
                if (extra is not null)
                {
                    properties.Append($",\"x-csharpType\":\"{EscapeJson(extra)}\"");
                }

                properties.Append('}');

                if (!param.IsOptional && !param.HasDefaultValue)
                {
                    required.Add(param.Name ?? param.Position.ToString());
                }
            }

            var requiredJson = required.Count > 0
                ? $",\"required\":[{string.Join(",", required.ConvertAll(r => $"\"{EscapeJson(r)}\""))}]"
                : string.Empty;

            // Always inject dryRun so LLMs can request a dry-run on any custom tool.
            // The dispatcher wraps execution in an Undo group and reverts if dryRun is true.
            var dryRunProp = "\"dryRun\":{\"type\":\"boolean\",\"description\":\"If true, execute without persisting: Unity Undo changes are immediately reverted. Raw file I/O is not guaranteed to be prevented.\"}";
            var allProperties = properties.Length > 0
                ? $"{properties},{dryRunProp}"
                : dryRunProp;

            var inputSchema = $"{{\"type\":\"object\",\"properties\":{{{allProperties}}}{requiredJson}}}";

            return new ToolEntry(
                attr.Name,
                attr.Description,
                method.DeclaringType?.Name ?? string.Empty,
                method.Name,
                inputSchema);
        }

        private static string MapToJsonSchemaType(Type type, out string? csharpTypeAnnotation)
        {
            csharpTypeAnnotation = null;
            if (type == typeof(string))   return "string";
            if (type == typeof(int) || type == typeof(long) || type == typeof(short)) return "integer";
            if (type == typeof(float) || type == typeof(double) || type == typeof(decimal)) return "number";
            if (type == typeof(bool))    return "boolean";

            // Fallback: annotate the C# type name so the LLM knows what was expected
            csharpTypeAnnotation = type.FullName ?? type.Name;
            return "string";
        }

        private static string SerializeManifest(Dictionary<string, List<ToolEntry>> categoryMap)
        {
            var sb = new StringBuilder();
            sb.Append('{');
            sb.Append("\"schemaVersion\":\"1\",");
            sb.Append($"\"generatedAtUtc\":\"{DateTime.UtcNow:O}\",");
            sb.Append("\"categories\":[");

            var firstCat = true;
            foreach (var kv in categoryMap)
            {
                if (!firstCat)
                {
                    sb.Append(',');
                }

                firstCat = false;
                sb.Append('{');
                sb.Append($"\"name\":\"{EscapeJson(kv.Key)}\",");
                sb.Append("\"tools\":[");

                var firstTool = true;
                foreach (var tool in kv.Value)
                {
                    if (!firstTool)
                    {
                        sb.Append(',');
                    }

                    firstTool = false;
                    sb.Append('{');
                    sb.Append($"\"name\":\"{EscapeJson(tool.Name)}\",");
                    sb.Append($"\"description\":\"{EscapeJson(tool.Description)}\",");
                    sb.Append($"\"declaringType\":\"{EscapeJson(tool.DeclaringType)}\",");
                    sb.Append($"\"methodName\":\"{EscapeJson(tool.MethodName)}\",");
                    sb.Append($"\"inputSchema\":{tool.InputSchemaJson}");
                    sb.Append('}');
                }

                sb.Append("]}");
            }

            sb.Append("]}");
            return sb.ToString();
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

        private readonly struct ToolEntry
        {
            public string Name            { get; }
            public string Description     { get; }
            public string DeclaringType   { get; }
            public string MethodName      { get; }
            public string InputSchemaJson { get; }

            public ToolEntry(string name, string description, string declaringType, string methodName, string inputSchemaJson)
            {
                Name            = name;
                Description     = description;
                DeclaringType   = declaringType;
                MethodName      = methodName;
                InputSchemaJson = inputSchemaJson;
            }
        }
    }
}
#endif
