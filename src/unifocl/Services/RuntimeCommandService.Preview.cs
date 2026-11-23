using System.Text.Json.Nodes;
using Spectre.Console;

internal sealed partial class RuntimeCommandService
{
    private const string PreviewUsage = "usage: /preview <group> <command> [argsJson]  (e.g. /preview gameobject create {\"name\":\"Probe\"})";

    /// <summary>
    /// <c>/preview &lt;group&gt; &lt;command&gt; [argsJson]</c>: runs a Pipeline command in a sandbox and
    /// reports what it changed, after undoing it.
    /// </summary>
    public async Task HandlePreviewCommandAsync(string input, CliSessionState session, Action<string> log)
    {
        if (!RequireProject(session, log, "preview")) return;

        if (!TryParsePreview(input, out var command, out var args, out var error))
        {
            log($"error: preview: {Markup.Escape(error)}");
            log(Markup.Escape(PreviewUsage));
            return;
        }

        var outcome = await PipelinePreviewService.RunAsync(
            session.CurrentProjectPath!, command, args, ttlSeconds: 300, CancellationToken.None);
        foreach (var line in PipelinePreviewService.Describe(outcome))
        {
            log(Markup.Escape(line));
        }
    }

    /// <summary>
    /// Splits <c>/preview group name {json}</c>. The JSON is everything after the command name, taken as
    /// written, so it may contain spaces.
    /// </summary>
    internal static bool TryParsePreview(string input, out string command, out JsonObject? args, out string error)
    {
        command = string.Empty;
        args = null;
        error = string.Empty;

        var rest = input.Trim();
        if (rest.StartsWith("/preview", StringComparison.OrdinalIgnoreCase))
        {
            rest = rest["/preview".Length..].Trim();
        }
        else if (rest.StartsWith("preview", StringComparison.OrdinalIgnoreCase))
        {
            rest = rest["preview".Length..].Trim();
        }

        var parts = rest.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts[0].StartsWith('{') || parts[1].StartsWith('{'))
        {
            error = "name the command to preview";
            return false;
        }

        command = $"{parts[0]} {parts[1]}";
        if (parts.Length == 3)
        {
            try
            {
                args = JsonNode.Parse(parts[2]) as JsonObject;
            }
            catch (System.Text.Json.JsonException ex)
            {
                error = $"argsJson is not valid JSON: {ex.Message}";
                return false;
            }

            if (args is null)
            {
                error = "argsJson must be a JSON object";
                return false;
            }
        }

        return true;
    }
}
