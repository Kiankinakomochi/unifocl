#if UNITY_EDITOR && UNIFOCL_PIPELINE
#nullable enable
using System;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Unity.Pipeline.Commands;

namespace UniFocl.EditorBridge.Pipeline
{
    /// <summary>
    /// The unifocl CLI's link to the Editor, carried by the official Unity CLI. Each command matches one
    /// endpoint of unifocl's former HTTP bridge: it takes the same JSON request and returns the same JSON
    /// response, so the CLI keeps its request and response models and only its transport changes.
    ///
    /// These are a wire protocol for unifocl, not a human-facing surface: a response that reports a
    /// failure (<c>ok: false</c>) is still a successful call, because the CLI reads the failure from the
    /// payload the way it always has. The agent-facing commands live in <see cref="UnifoclPipelineCommands"/>.
    /// </summary>
    internal static class UnifoclBridgeCommands
    {
        private const string Group = "unifocl.bridge";
        private const string PayloadHelp = "Request as JSON, in the shape the unifocl CLI sends";

        [CliCommand(Group, "ping", "unifocl CLI link: bridge identity, for detecting an Editor restart",
            Effect = CommandEffect.ReadOnly, MainThreadRequired = false)]
        internal static ExitCode Ping(CommandContext cmd)
        {
            CLIDaemon.MarkActivity();
            cmd.Result.Write(new JObject
            {
                ["runtimeId"] = CLIDaemon.RuntimeInstanceId,
                ["bridge"] = "pipeline",
            });
            return ExitCode.Success;
        }

        // ── Hierarchy and inspector ─────────────────────────────────────────────────────────

        [CliCommand(Group, "hierarchy_snapshot", "unifocl CLI link: snapshot of the open scene hierarchy",
            Effect = CommandEffect.ReadOnly)]
        internal static ExitCode HierarchySnapshot(CommandContext cmd) =>
            Reply(cmd, DaemonHierarchyService.BuildSnapshotPayload());

        [CliCommand(Group, "hierarchy_find", "unifocl CLI link: search the scene hierarchy",
            Effect = CommandEffect.ReadOnly)]
        internal static ExitCode HierarchyFind(CommandContext cmd, [CliArg("payload", PayloadHelp, Required = true)] string payload) =>
            Reply(cmd, DaemonHierarchyService.ExecuteSearch(payload));

        [CliCommand(Group, "hierarchy_command", "unifocl CLI link: create, rename, move, toggle, duplicate or remove scene objects",
            Effect = CommandEffect.Destructive)]
        internal static ExitCode HierarchyCommand(CommandContext cmd, [CliArg("payload", PayloadHelp, Required = true)] string payload) =>
            Reply(cmd, DaemonHierarchyService.ExecuteCommand(payload));

        [CliCommand(Group, "inspect", "unifocl CLI link: read or edit components and fields of a scene object",
            Effect = CommandEffect.Destructive)]
        internal static ExitCode Inspect(CommandContext cmd, [CliArg("payload", PayloadHelp, Required = true)] string payload) =>
            Reply(cmd, DaemonInspectorService.Execute(payload));

        // ── Project commands ────────────────────────────────────────────────────────────────

        [CliCommand(Group, "project_command", "unifocl CLI link: run a project action (assets, scenes, prefabs, builds, tests, diagnostics, ...)",
            Effect = CommandEffect.Destructive)]
        internal static async Task<ExitCode> ProjectCommand(
            CommandContext cmd,
            [CliArg("payload", PayloadHelp, Required = true)] string payload,
            [CliArg("timeout", "Timeout in seconds")] double timeoutInSeconds = 600)
        {
            var response = await OnMainThread(CLIDaemon.RunProjectCommandAsync(payload));
            return Reply(cmd, response);
        }

        [CliCommand(Group, "project_command_status", "unifocl CLI link: status of a running or finished project command",
            Effect = CommandEffect.ReadOnly, MainThreadRequired = false)]
        internal static ExitCode ProjectCommandStatus(CommandContext cmd, [CliArg("request-id", "Request id; empty for the current command")] string requestId = "") =>
            Reply(cmd, CLIDaemon.ReadProjectCommandStatus(requestId));

        [CliCommand(Group, "mutation_submit", "unifocl CLI link: queue a durable project mutation that survives domain reloads",
            Effect = CommandEffect.Destructive)]
        internal static ExitCode MutationSubmit(CommandContext cmd, [CliArg("payload", PayloadHelp, Required = true)] string payload)
        {
            CLIDaemon.MarkActivity();
            return Reply(cmd, DaemonProjectService.SubmitMutationPayload(payload));
        }

        [CliCommand(Group, "mutation_status", "unifocl CLI link: stage of a durable project mutation",
            Effect = CommandEffect.ReadOnly)]
        internal static ExitCode MutationStatus(CommandContext cmd, [CliArg("request-id", "Request id", Required = true)] string requestId)
        {
            CLIDaemon.MarkActivity();
            return Reply(cmd, DaemonProjectService.GetMutationStatusPayload(requestId));
        }

        [CliCommand(Group, "mutation_result", "unifocl CLI link: result of a finished durable project mutation",
            Effect = CommandEffect.ReadOnly)]
        internal static ExitCode MutationResult(CommandContext cmd, [CliArg("request-id", "Request id", Required = true)] string requestId) =>
            Reply(cmd, DaemonProjectService.GetMutationResultPayload(requestId));

        [CliCommand(Group, "mutation_cancel", "unifocl CLI link: cancel a queued durable project mutation",
            Effect = CommandEffect.Idempotent)]
        internal static ExitCode MutationCancel(CommandContext cmd, [CliArg("request-id", "Request id", Required = true)] string requestId) =>
            Reply(cmd, DaemonProjectService.CancelMutationPayload(requestId));

        [CliCommand(Group, "custom_tool", "unifocl CLI link: run a [UnifoclCommand] tool or a durable command operation",
            Effect = CommandEffect.Destructive)]
        internal static ExitCode CustomTool(CommandContext cmd, [CliArg("payload", PayloadHelp, Required = true)] string payload)
        {
            CLIDaemon.MarkActivity();
            return Reply(cmd, CLIDaemon.HandleUnifoclMcpToolPayload(payload));
        }

        // ── Status polling ──────────────────────────────────────────────────────────────────
        // These read state caches and run off the main thread, so they answer while a build or a
        // compile holds it.

        [CliCommand(Group, "build_status", "unifocl CLI link: state of the running or last player build",
            Effect = CommandEffect.ReadOnly, MainThreadRequired = false)]
        internal static ExitCode BuildStatus(CommandContext cmd) =>
            Reply(cmd, DaemonProjectService.GetBuildStatusPayload());

        [CliCommand(Group, "build_log", "unifocl CLI link: a chunk of the player build log",
            Effect = CommandEffect.ReadOnly, MainThreadRequired = false)]
        internal static ExitCode BuildLog(
            CommandContext cmd,
            [CliArg("offset", "Byte offset to read from")] long offset = 0,
            [CliArg("limit", "Maximum lines to return; 0 for the default")] int limit = 0,
            [CliArg("errors-only", "Only error lines")] bool errorsOnly = false) =>
            Reply(cmd, DaemonProjectService.ReadBuildLogPayload(offset, limit, errorsOnly));

        [CliCommand(Group, "compile_status", "unifocl CLI link: script compilation state",
            Effect = CommandEffect.ReadOnly, MainThreadRequired = false)]
        internal static ExitCode CompileStatus(CommandContext cmd) =>
            Reply(cmd, DaemonProjectService.GetCompilationStatusPayload());

        [CliCommand(Group, "asset_index", "unifocl CLI link: the project's asset index, or nothing new since a known revision",
            Effect = CommandEffect.ReadOnly, MainThreadRequired = false)]
        internal static ExitCode AssetIndex(CommandContext cmd, [CliArg("revision", "Revision the caller already holds; 0 for a full index")] int revision = 0)
        {
            CLIDaemon.MarkActivity();
            return Reply(cmd, DaemonAssetIndexService.BuildPayload(revision > 0 ? revision : (int?)null));
        }

        // ── Helpers ─────────────────────────────────────────────────────────────────────────

        private static ExitCode Reply(CommandContext cmd, string json)
        {
            cmd.Result.Write(Parse(json));
            return ExitCode.Success;
        }

        private static JToken Parse(string json)
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
                return new JValue(json);
            }
        }

        /// <summary>
        /// Waits for a task by giving frames back to the Editor, so the command resumes on the main
        /// thread even when the task completes on another one (some project actions finish from a
        /// wall-clock timer).
        /// </summary>
        private static async Task<T> OnMainThread<T>(Task<T> task)
        {
            while (!task.IsCompleted)
            {
                await Task.Yield();
            }

            return await task;
        }
    }
}
#endif
