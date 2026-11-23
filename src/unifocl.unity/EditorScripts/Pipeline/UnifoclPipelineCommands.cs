#if UNITY_EDITOR && UNIFOCL_PIPELINE
#nullable enable
using System;
using Newtonsoft.Json.Linq;
using Unity.Pipeline.Commands;

namespace UniFocl.EditorBridge.Pipeline
{
    /// <summary>
    /// Exposes unifocl's Editor-side operations as Unity Pipeline commands, so an agent can reach them
    /// through the official Unity CLI (<c>unity command unifocl ...</c>) or its MCP server
    /// (<c>unity mcp</c>) instead of unifocl's own bridge. This assembly only compiles when the
    /// project has <c>com.unity.pipeline</c>; without it nothing here exists and unifocl is unaffected.
    ///
    /// Each command is a thin wrapper: the work is done by the same services the unifocl daemon
    /// calls, and their JSON is handed back as-is (parsed into a token so Pipeline embeds it as an
    /// object rather than an escaped string).
    /// </summary>
    internal static class UnifoclPipelineCommands
    {
        private const string Group = "unifocl";

        [CliCommand(Group, "validate",
            "Run a unifocl project validator: scene-list, missing-scripts, build-settings, asset-refs or addressables",
            Effect = CommandEffect.ReadOnly)]
        public static JToken Validate(
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
            return ParseResult(json);
        }

        // The same [UnifoclCommand] tool is exposed twice so the Unity CLI's own safety filters work on
        // it: `tool_preview` is read-only (unifocl runs it inside its dry-run sandbox and reverts every
        // change), so it survives `unity mcp --exclude-annotations destructive`, while `tool_run`
        // carries the destructive hint that clients use to ask before running it.

        [CliCommand(Group, "tool_preview",
            "Dry-run a project [UnifoclCommand] tool: execute it, report what changed, then revert every change",
            Effect = CommandEffect.ReadOnly)]
        public static JToken ToolPreview(
            [CliArg("name", "Name of the [UnifoclCommand] tool", Required = true)] string name,
            [CliArg("args", "Tool arguments as a flat JSON object, e.g. {\"count\":3}")] string args = "{}")
        {
            return ParseResult(DaemonCustomToolService.ExecuteCustomTool(name, args, dryRun: true));
        }

        [CliCommand(Group, "tool_run",
            "Run a project [UnifoclCommand] tool for real. Preview it first with `unifocl tool_preview`",
            Effect = CommandEffect.Destructive)]
        public static JToken ToolRun(
            [CliArg("name", "Name of the [UnifoclCommand] tool", Required = true)] string name,
            [CliArg("args", "Tool arguments as a flat JSON object, e.g. {\"count\":3}")] string args = "{}")
        {
            return ParseResult(DaemonCustomToolService.ExecuteCustomTool(name, args, dryRun: false));
        }

        private static JToken ParseResult(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return JValue.CreateNull();
            }

            try
            {
                return JToken.Parse(json);
            }
            catch (Newtonsoft.Json.JsonException)
            {
                // Not every unifocl service answers in JSON; keep the text rather than failing the call.
                return new JValue(json);
            }
        }
    }
}
#endif
