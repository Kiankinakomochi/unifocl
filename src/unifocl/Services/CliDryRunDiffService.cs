using System.Text.Json;
using System.Threading;

internal static class CliDryRunDiffService
{
    private static readonly AsyncLocal<AgenticDiffPayload?> CurrentDiff = new();

    public static AgenticDiffPayload? ConsumeCurrentDiff()
    {
        var diff = CurrentDiff.Value;
        CurrentDiff.Value = null;
        return diff;
    }

    public static void Reset()
    {
        CurrentDiff.Value = null;
    }

    public static bool TryStripDryRunFlag(List<string> tokens, out bool dryRun)
    {
        dryRun = false;
        if (tokens.Count == 0)
        {
            return false;
        }

        for (var i = tokens.Count - 1; i >= 0; i--)
        {
            if (!tokens[i].Equals("--dry-run", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            tokens.RemoveAt(i);
            dryRun = true;
        }

        return dryRun;
    }

    public static bool TryCaptureDiffFromContent(string? content, out AgenticDiffPayload? payload)
    {
        payload = null;
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            if (!root.TryGetProperty("lines", out var linesElement) || linesElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var lines = new List<string>();
            foreach (var line in linesElement.EnumerateArray())
            {
                if (line.ValueKind == JsonValueKind.String)
                {
                    var value = line.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        lines.Add(value);
                    }
                }
            }

            if (lines.Count == 0)
            {
                return false;
            }

            var summary = root.TryGetProperty("summary", out var summaryElement) && summaryElement.ValueKind == JsonValueKind.String
                ? summaryElement.GetString()
                : null;
            var format = root.TryGetProperty("format", out var formatElement) && formatElement.ValueKind == JsonValueKind.String
                ? formatElement.GetString() ?? "unified"
                : "unified";

            payload = new AgenticDiffPayload(format, lines, summary);
            CurrentDiff.Value = payload;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void AppendUnifiedDiffToLog(List<string> output, AgenticDiffPayload diff)
    {
        if (!string.IsNullOrWhiteSpace(diff.Summary))
        {
            output.Add($"[i] dry-run: {diff.Summary}");
        }

        output.Add("[i] --- dry-run diff ---");
        foreach (var line in diff.Lines)
        {
            output.Add(line);
        }
        output.Add("[i] --- end dry-run diff ---");
    }
}
