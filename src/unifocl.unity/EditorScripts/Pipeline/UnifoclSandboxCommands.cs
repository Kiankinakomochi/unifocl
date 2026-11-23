#if UNITY_EDITOR && UNIFOCL_PIPELINE
#nullable enable
using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using Unity.Pipeline.Commands;
using UnityEngine;

namespace UniFocl.EditorBridge.Pipeline
{
    /// <summary>
    /// Preview any Pipeline command, Unity's or a third party's: open a sandbox session, run the command
    /// as usual, then end the session to undo it and get a report of what it changed
    /// (<see cref="DaemonSandboxSession"/>). The unifocl CLI's <c>/preview</c> does all three in one call.
    /// </summary>
    internal static class UnifoclSandboxCommands
    {
        private const string Group = "unifocl.sandbox";

        // Groups whose work outlives the call or leaves the project (builds, package installs, Play Mode,
        // bakes, test runs), so ending the session could not take it back.
        private static readonly string[] UnsandboxableGroups =
        {
            "build", "build.target", "package", "editor", "editor.playmode", "test", "job", "lighting",
            "navmesh", "occlusion", "multiplayer.role", "script.methodreload", Group,
        };

        // Settings changes stay in memory and are saved when the Editor quits, which the session can't
        // undo, so only their read-only commands are allowed.
        private const string SettingsGroupPrefix = "settings";

        [CliCommand(Group, "begin",
            "Open a unifocl sandbox session. Commands run until `unifocl.sandbox end` are undone when it ends, " +
            "and their AssetDatabase saves, moves and deletes are refused meanwhile. Pass the command to preview " +
            "to check that it can be sandboxed and learn how to call it. The session ends by itself after ttl seconds.",
            Effect = CommandEffect.Idempotent)]
        internal static ExitCode Begin(
            CommandContext cmd,
            [CliArg("command", "The command to preview, as `group name` (e.g. `gameobject create`); optional")] string command = "",
            [CliArg("ttl", "Seconds before the session ends by itself")] int ttl = 300)
        {
            JObject? target = null;
            if (!string.IsNullOrWhiteSpace(command))
            {
                var info = CommandRegistry.DiscoverCommands()
                    .FirstOrDefault(c => string.Equals(c.FullName, command.Trim(), StringComparison.Ordinal));
                if (info is null)
                {
                    cmd.Log.Error($"No command '{command}'. List them with `unity command --help`.");
                    return ExitCode.NotFound;
                }

                if (WhyNotSandboxable(info) is { } reason)
                {
                    cmd.Log.Error($"'{info.FullName}' can't be previewed: {reason}");
                    return ExitCode.InvalidUsage;
                }

                target = new JObject
                {
                    ["command"] = info.FullName,
                    ["effect"] = info.Effect.ToString(),
                    ["hasConfirm"] = info.Parameters.Any(p => p.Name == "confirm"),
                    ["hasDryRun"] = info.Parameters.Any(p => p.Name == "dry-run"),
                    ["parameters"] = new JArray(info.Parameters.Select(p => p.Name)),
                };
            }

            var error = DaemonSandboxSession.Begin(command ?? string.Empty, ttl, out var session);
            if (error is not null)
            {
                cmd.Log.Error(error);
                return ExitCode.Failed;
            }

            CLIDaemon.MarkActivity();
            cmd.Result.Write(new JObject
            {
                ["sandboxId"] = session.id,
                ["expiresAtUtc"] = session.expiresAtUtc,
                ["target"] = target,
            });
            return ExitCode.Success;
        }

        [CliCommand(Group, "end",
            "End the unifocl sandbox session: undo what ran in it and report what it changed and anything that " +
            "could not be undone",
            Effect = CommandEffect.Idempotent)]
        internal static ExitCode End(CommandContext cmd)
        {
            var report = DaemonSandboxSession.End(out var error);
            if (report is null)
            {
                cmd.Log.Error(error ?? "the sandbox session could not be ended");
                return ExitCode.Failed;
            }

            CLIDaemon.MarkActivity();
            cmd.Result.Write(JObject.Parse(JsonUtility.ToJson(report)));
            return ExitCode.Success;
        }

        [CliCommand(Group, "status", "Whether a unifocl sandbox session is open, and for what",
            Effect = CommandEffect.ReadOnly)]
        internal static ExitCode Status(CommandContext cmd)
        {
            cmd.Result.Write(JObject.Parse(JsonUtility.ToJson(DaemonSandboxSession.Status())));
            return ExitCode.Success;
        }

        private static string? WhyNotSandboxable(CommandInfo info)
        {
            var group = info.Group ?? string.Empty;
            if (UnsandboxableGroups.Contains(group, StringComparer.Ordinal))
            {
                return $"commands in '{group}' do work the sandbox can't undo (builds, packages, Play Mode, bakes, tests)";
            }

            if ((group == SettingsGroupPrefix || group.StartsWith(SettingsGroupPrefix + ".", StringComparison.Ordinal))
                && info.Effect != CommandEffect.ReadOnly)
            {
                return "project settings changes are kept in memory and saved on quit, which the sandbox can't undo";
            }

            if (group == "unifocl.bridge")
            {
                return "unifocl.bridge is the unifocl CLI's own link; preview unifocl operations with their dry-run instead";
            }

            return null;
        }
    }
}
#endif
