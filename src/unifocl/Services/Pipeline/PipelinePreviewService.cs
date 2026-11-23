using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>What previewing one Pipeline command produced.</summary>
/// <param name="Ok">Whether the sandbox opened and closed; the command itself may still have failed.</param>
/// <param name="Command">The command, as <c>group name</c>.</param>
/// <param name="CommandOk">Whether the previewed command succeeded.</param>
/// <param name="CommandResult">What the command returned, before it was undone.</param>
/// <param name="CommandError">Why the command failed, when it did.</param>
/// <param name="Report">The sandbox report: what changed, what was undone, and what was not.</param>
/// <param name="Error">Why the preview could not run, when <paramref name="Ok"/> is false.</param>
internal sealed record PipelinePreviewOutcome(
    bool Ok,
    string Command,
    bool CommandOk,
    JsonElement? CommandResult,
    string? CommandError,
    JsonElement? Report,
    string? Error)
{
    /// <summary>True when the sandbox undid everything the command changed.</summary>
    public bool Reverted => Report is { ValueKind: JsonValueKind.Object } report
                            && report.TryGetProperty("reverted", out var reverted)
                            && reverted.ValueKind == JsonValueKind.True;
}

/// <summary>
/// Previews any Pipeline command, Unity's own included: runs it inside a unifocl sandbox session in the
/// Editor (<c>unifocl.sandbox begin</c> / <c>end</c>), so its effects are reported and then undone.
/// </summary>
internal static class PipelinePreviewService
{
    internal const string SandboxGroup = "unifocl.sandbox";
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(2);

    public static async Task<PipelinePreviewOutcome> RunAsync(
        string projectPath,
        string command,
        JsonObject? args,
        int ttlSeconds,
        CancellationToken cancellationToken)
    {
        if (!TrySplit(command, out var group, out var name))
        {
            return Fail(command, "name the command as `<group> <name>`, e.g. `gameobject create`");
        }

        var transport = PipelineTransports.For(projectPath, PipelineTransportKind.McpSession, out var transportError);
        if (transport is null)
        {
            return Fail(command, transportError ?? "Unity CLI unavailable");
        }

        var begin = await transport.ExecAsync(
            SandboxGroup,
            "begin",
            new Dictionary<string, object?> { ["command"] = $"{group} {name}", ["ttl"] = ttlSeconds },
            cancellationToken: cancellationToken);
        if (!begin.Ok)
        {
            return Fail(command, begin.Error ?? "the sandbox did not open");
        }

        // Whatever the command does, the session must end: otherwise the Editor stays in sandbox mode,
        // refusing saves, until the session expires.
        PipelineCallResult run;
        try
        {
            var commandArgs = BuildArgs(args, begin.Result);
            run = await transport.ExecAsync(group, name, commandArgs, CommandTimeout, cancellationToken);
            if (!run.Ok && IsHiddenFromMcp(run.Error)
                && PipelineTransports.For(projectPath, PipelineTransportKind.Command, out _) is { } oneShot)
            {
                // The MCP server leaves some commands out of its tools (script eval, for one); `unity
                // command` reaches every command, and the session is the Editor's, so it still applies.
                await using (oneShot)
                {
                    run = await oneShot.ExecAsync(group, name, commandArgs, CommandTimeout, cancellationToken);
                }
            }
        }
        catch (Exception ex)
        {
            run = PipelineCallResult.Failure(ex is OperationCanceledException ? "cancelled" : ex.Message);
        }

        var end = await transport.ExecAsync(SandboxGroup, "end", cancellationToken: CancellationToken.None);
        if (!end.Ok)
        {
            return new PipelinePreviewOutcome(
                false, $"{group} {name}", run.Ok, run.Result, run.Error, null,
                $"the sandbox did not close, so the command's changes may still be in place: {end.Error}");
        }

        return new PipelinePreviewOutcome(true, $"{group} {name}", run.Ok, run.Result, run.Error, end.Result, null);
    }

    internal static bool IsHiddenFromMcp(string? error) =>
        error is not null && error.Contains("has no command named", StringComparison.Ordinal);

    /// <summary>
    /// Splits <c>group name</c>: the group is everything before the last space, since groups use dots
    /// (<c>animation.clip</c>) and neither part contains spaces.
    /// </summary>
    internal static bool TrySplit(string command, out string group, out string name)
    {
        var parts = (command ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        group = parts.Length == 2 ? parts[0] : string.Empty;
        name = parts.Length == 2 ? parts[1] : string.Empty;
        return parts.Length == 2;
    }

    /// <summary>
    /// The previewed command's arguments. A command that asks for <c>confirm</c> before it changes
    /// anything gets it, so the preview shows the real change rather than the command's own description
    /// of it; passing <c>confirm</c> or <c>dry-run</c> explicitly overrides that.
    /// </summary>
    internal static Dictionary<string, object?> BuildArgs(JsonObject? args, JsonElement? beginResult)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (args is not null)
        {
            foreach (var (key, value) in args)
            {
                result[key] = value?.DeepClone();
            }
        }

        var hasConfirm = beginResult is { ValueKind: JsonValueKind.Object } begin
                         && begin.TryGetProperty("target", out var target)
                         && target.ValueKind == JsonValueKind.Object
                         && target.TryGetProperty("hasConfirm", out var confirm)
                         && confirm.ValueKind == JsonValueKind.True;
        if (hasConfirm && !result.ContainsKey("confirm") && !result.ContainsKey("dry-run"))
        {
            result["confirm"] = true;
        }

        return result;
    }

    /// <summary>A readable summary of a preview, one line per entry.</summary>
    internal static IReadOnlyList<string> Describe(PipelinePreviewOutcome outcome)
    {
        var lines = new List<string>();
        if (!outcome.Ok && outcome.Report is null)
        {
            lines.Add($"error: preview {outcome.Command}: {outcome.Error}");
            return lines;
        }

        lines.Add(outcome.CommandOk
            ? $"[+] preview {outcome.Command}: the command ran in the sandbox"
            : $"error: preview {outcome.Command}: the command returned an error: {outcome.CommandError}");
        if (outcome.CommandResult is { } commandResult)
        {
            lines.Add($"    result: {Compact(commandResult)}");
        }

        if (outcome.Report is { ValueKind: JsonValueKind.Object } report)
        {
            AddList(lines, report, "objectsAdded", "added");
            AddList(lines, report, "objectsRemoved", "removed");
            AddList(lines, report, "objectsModified", "modified");
            AddList(lines, report, "filesCreated", "file created");
            AddList(lines, report, "filesRestored", "file changed");
            AddList(lines, report, "blocked", "write refused");
            if (report.TryGetProperty("fingerprintsTruncated", out var truncated) && truncated.ValueKind == JsonValueKind.True)
            {
                lines.Add("    note: the scene is large, so component changes were only compared for its first objects");
            }

            if (outcome.Reverted)
            {
                lines.Add("[+] every change was undone");
            }
            else
            {
                lines.Add("warning: some changes are still in place:");
                AddList(lines, report, "notReverted", "still in place");
            }
        }

        if (!outcome.Ok)
        {
            lines.Add($"error: {outcome.Error}");
        }

        return lines;
    }

    private static void AddList(List<string> lines, JsonElement report, string property, string label)
    {
        if (!report.TryGetProperty(property, out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var item in items.EnumerateArray())
        {
            lines.Add($"    {label}: {item.GetString()}");
        }
    }

    private static string Compact(JsonElement element)
    {
        var text = element.ValueKind == JsonValueKind.String ? element.GetString() ?? string.Empty : element.GetRawText();
        return text.Length <= 400 ? text : text[..400] + "…";
    }

    private static PipelinePreviewOutcome Fail(string command, string error) =>
        new(false, command, false, null, null, null, error);
}
