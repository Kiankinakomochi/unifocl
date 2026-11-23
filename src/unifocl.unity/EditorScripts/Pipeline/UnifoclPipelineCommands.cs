#if UNITY_EDITOR && UNIFOCL_PIPELINE
#nullable enable
using System;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using Unity.Pipeline.Commands;
using UnityEditor;
using UnityEngine;

namespace UniFocl.EditorBridge.Pipeline
{
    /// <summary>
    /// Exposes unifocl's Editor-side operations as Unity Pipeline commands, so an agent can reach them
    /// through the official Unity CLI (<c>unity command unifocl ...</c>) or its MCP server
    /// (<c>unity mcp</c>) instead of unifocl's own bridge. This assembly only compiles when the
    /// project has <c>com.unity.pipeline</c>; without it nothing here exists and unifocl is unaffected.
    ///
    /// Each command is a thin wrapper over the services the unifocl daemon calls. Their
    /// <c>{ ok, message, content }</c> envelope is unpacked into Pipeline's own conventions: the
    /// payload becomes the result, and a failure becomes a logged error with a non-zero exit code.
    /// </summary>
    internal static class UnifoclPipelineCommands
    {
        private const string Group = "unifocl";
        private const string ToolGroup = "unifocl.tool";

        [CliCommand(Group, "validate",
            "Run a unifocl project validator: scene-list, missing-scripts, build-settings, asset-refs or addressables. " +
            "Exits non-zero when the validator reports errors.",
            Effect = CommandEffect.ReadOnly)]
        internal static ExitCode Validate(
            CommandContext cmd,
            [CliArg("check", "Validator to run: scene-list | missing-scripts | build-settings | asset-refs | addressables", Required = true)]
            string check)
        {
            var json = (check ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                "scene-list" => DaemonValidateService.ExecuteValidateSceneList(),
                "missing-scripts" => DaemonValidateService.ExecuteValidateMissingScripts(),
                "build-settings" => DaemonValidateService.ExecuteValidateBuildSettings(),
                "asset-refs" => DaemonValidateService.ExecuteValidateAssetRefs(),
                "addressables" => DaemonValidateService.ExecuteValidateAddressables(),
                _ => throw new ArgumentException(
                    $"unknown check '{check}'; expected scene-list, missing-scripts, build-settings, asset-refs or addressables"),
            };

            var envelope = JObject.Parse(json);
            if (envelope.Value<bool?>("ok") != true)
            {
                cmd.Log.Error(envelope.Value<string>("message") ?? "validator failed");
                return ExitCode.Failed;
            }

            var result = ParseJson(envelope.Value<string>("content"));
            cmd.Result.Write(result);
            if (result is JObject report && report.Value<bool?>("passed") == false)
            {
                cmd.Log.Error(envelope.Value<string>("message") ?? "validation failed");
                return ExitCode.Failed;
            }

            return ExitCode.Success;
        }

        [CliCommand(Group, "tool_list",
            "List the project's [UnifoclCommand] tools and the Pipeline command each one is registered as",
            Effect = CommandEffect.ReadOnly)]
        internal static ExitCode ToolList(CommandContext cmd)
        {
            foreach (var pair in DaemonCustomToolService.ListTools().OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                var attr = pair.Value.GetCustomAttribute<UnifoclCommandAttribute>();
                var (group, name) = ToolCommandName(pair.Key);
                cmd.Result.WriteRecord(new
                {
                    tool = pair.Key,
                    command = $"{group} {name}",
                    category = attr?.Category,
                    description = attr?.Description,
                    parameters = DescribeParameters(pair.Value),
                });
            }

            return ExitCode.Success;
        }

        // Read-only so it survives `unity mcp --exclude-annotations destructive`: an agent restricted to
        // safe tools can still see what a tool would do. The per-tool commands below carry the
        // destructive hint because their confirm=true path really runs the tool.
        [CliCommand(Group, "tool_preview",
            "Dry-run a project [UnifoclCommand] tool and revert every Undo-tracked change. " +
            "Raw file writes (System.IO) made by the tool are not reverted.",
            Effect = CommandEffect.ReadOnly)]
        internal static ExitCode ToolPreview(
            CommandContext cmd,
            [CliArg("name", "Name of the [UnifoclCommand] tool", Required = true)] string name,
            [CliArg("args", "Tool arguments as a flat JSON object, e.g. {\"count\":3}")] string args = "{}")
        {
            return RunTool(cmd, name, args, dryRun: true);
        }

        // ── One command per [UnifoclCommand] tool ──────────────────────────────────────────────

        [InitializeOnLoadMethod]
        private static void RegisterToolCommands()
        {
            // Registrations die with the domain, so this runs again after every reload.
            foreach (var pair in DaemonCustomToolService.ListTools())
            {
                var (group, name) = ToolCommandName(pair.Key);
                var attr = pair.Value.GetCustomAttribute<UnifoclCommandAttribute>();
                var parameters = DescribeParameters(pair.Value);
                var description =
                    $"{attr?.Description ?? pair.Key} " +
                    $"[unifocl tool '{pair.Key}'; args: {(parameters.Length == 0 ? "none" : string.Join(", ", parameters))}] " +
                    "Requires confirm=true to run; dry-run=true previews it and reverts Undo-tracked changes.";

                try
                {
                    CommandRegistry.RegisterCommand(
                        group, name, description,
                        new Func<CommandContext, string, bool, bool, ExitCode>(new ToolCommand(pair.Key).Run),
                        mainThreadRequired: true,
                        tags: new[] { "unifocl", "unifocl/tool" },
                        effect: CommandEffect.Destructive);
                }
                catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException)
                {
                    Debug.LogWarning($"[unifocl] tool '{pair.Key}' was not registered as a Pipeline command: {ex.Message}");
                }
            }
        }

        /// <summary>Handler bound to one tool, so each tool gets its own command and catalog entry.</summary>
        private sealed class ToolCommand
        {
            private readonly string m_ToolName;

            public ToolCommand(string toolName) => m_ToolName = toolName;

            // Pipeline's confirm / dry-run convention for commands that change the project.
            public ExitCode Run(
                CommandContext cmd,
                [CliArg("args", "Tool arguments as a flat JSON object, e.g. {\"count\":3}")] string args = "{}",
                [CliArg("confirm", "Run the tool. Without it (and without dry-run) the call is refused.")] bool confirm = false,
                [CliArg("dry-run", "Run the tool, then revert every Undo-tracked change.")] bool dryRun = false)
            {
                if (!dryRun && !confirm)
                {
                    cmd.Log.Error($"Refusing to run '{m_ToolName}'. Pass confirm=true to run it, or dry-run=true to preview it.");
                    return ExitCode.InvalidUsage;
                }

                return RunTool(cmd, m_ToolName, args, dryRun);
            }
        }

        // "spike.spawn_cubes" -> ("unifocl.tool.spike", "spawn_cubes"): a Pipeline name can't hold a dot,
        // so the tool's dotted prefix becomes a subgroup.
        private static (string Group, string Name) ToolCommandName(string toolName)
        {
            var cleaned = new string(toolName.Select(c => char.IsWhiteSpace(c) ? '_' : c).ToArray());
            var dot = cleaned.LastIndexOf('.');
            return dot <= 0
                ? (ToolGroup, cleaned)
                : ($"{ToolGroup}.{cleaned.Substring(0, dot)}", cleaned.Substring(dot + 1));
        }

        private static string[] DescribeParameters(MethodInfo method)
        {
            return method.GetParameters()
                .Select(p => p.HasDefaultValue
                    ? $"{p.Name}:{p.ParameterType.Name}={p.DefaultValue ?? "null"}"
                    : $"{p.Name}:{p.ParameterType.Name}")
                .ToArray();
        }

        private static ExitCode RunTool(CommandContext cmd, string toolName, string args, bool dryRun)
        {
            if (DaemonCustomToolService.ListTools().ContainsKey(toolName) == false)
            {
                cmd.Log.Error($"No [UnifoclCommand] tool named '{toolName}'. Run `unity command unifocl tool_list`.");
                return ExitCode.NotFound;
            }

            var envelope = JObject.Parse(DaemonCustomToolService.ExecuteCustomTool(toolName, args, dryRun));
            if (envelope.Value<bool?>("ok") != true)
            {
                cmd.Log.Error(envelope.Value<string>("message") ?? $"tool '{toolName}' failed");
                return ExitCode.Failed;
            }

            var output = envelope.Value<string>("result");
            cmd.Result.Write(dryRun
                ? new JObject { ["tool"] = toolName, ["dryRun"] = true, ["summary"] = output }
                : new JObject { ["tool"] = toolName, ["dryRun"] = false, ["result"] = ParseJson(output) });
            return ExitCode.Success;
        }

        /// <summary>A JSON value as a token, so Pipeline embeds it as an object; anything else stays text.</summary>
        private static JToken ParseJson(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return JValue.CreateNull();
            }

            var trimmed = text!.TrimStart();
            if (trimmed.StartsWith("{") || trimmed.StartsWith("["))
            {
                try
                {
                    return JToken.Parse(text);
                }
                catch (Newtonsoft.Json.JsonException)
                {
                    // Looked like JSON but wasn't; fall through and keep the text.
                }
            }

            return new JValue(text);
        }
    }
}
#endif
