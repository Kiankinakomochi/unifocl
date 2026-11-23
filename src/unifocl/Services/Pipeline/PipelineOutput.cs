using System.Globalization;
using System.Text;
using System.Text.Json;

/// <summary>
/// Converts between unifocl's values and the Unity CLI's text forms: arguments going out, results and
/// errors coming back. Shared by both transports so they agree on every shape.
/// </summary>
internal static class PipelineOutput
{
    /// <summary>
    /// A command result as JSON. One JSON value is returned as-is. Several lines of JSON, which is how
    /// <c>--format json</c> prints a record list, become an array. Anything else is kept as a string.
    /// Empty output is <c>null</c>.
    /// </summary>
    public static JsonElement? ParseResult(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var trimmed = text.Trim();
        if (TryParse(trimmed, out var whole))
        {
            return whole;
        }

        var lines = trimmed.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length > 1)
        {
            var records = new List<JsonElement>(lines.Length);
            foreach (var line in lines)
            {
                if (!TryParse(line, out var record))
                {
                    return StringElement(trimmed);
                }

                records.Add(record);
            }

            return JsonSerializer.SerializeToElement(records);
        }

        return StringElement(trimmed);
    }

    /// <summary>
    /// The message of a failed call, from the Unity CLI's stderr or an MCP error text: the
    /// <c>error:</c> lines, without the prefixes the CLI and its MCP server add.
    /// </summary>
    public static string ParseError(string? text, string fallback)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        var errors = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("error:", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(line["error:".Length..].Trim());
            }
        }

        var message = errors.Count > 0 ? string.Join(Environment.NewLine, errors) : text.Trim();
        return StripPrefixes(message);
    }

    /// <summary>
    /// One argument as the Unity CLI takes it on the command line: booleans as <c>true</c>/<c>false</c>,
    /// numbers in invariant culture, strings verbatim, anything else as JSON text.
    /// </summary>
    public static string FormatArgument(object? value)
    {
        return value switch
        {
            null => string.Empty,
            string s => s,
            bool b => b ? "true" : "false",
            JsonElement { ValueKind: JsonValueKind.String } e => e.GetString() ?? string.Empty,
            JsonElement e => e.GetRawText(),
            IFormattable f when value is not Enum => f.ToString(null, CultureInfo.InvariantCulture),
            Enum e => e.ToString(),
            _ => JsonSerializer.Serialize(value),
        };
    }

    // `unity mcp` reports failures as "Error: Pipeline server returned 400 Bad Request: Parameter
    // Validation Failed. <the command's own message>". The command's message is the useful part.
    private static string StripPrefixes(string message)
    {
        var result = message;
        if (result.StartsWith("Error:", StringComparison.OrdinalIgnoreCase))
        {
            result = result["Error:".Length..].TrimStart();
        }

        const string serverPrefix = "Pipeline server returned ";
        if (result.StartsWith(serverPrefix, StringComparison.Ordinal))
        {
            var colon = result.IndexOf(": ", serverPrefix.Length, StringComparison.Ordinal);
            if (colon > 0)
            {
                // Drop the short error category ("Parameter Validation Failed.") that precedes the message.
                var rest = result[(colon + 2)..];
                var period = rest.IndexOf(". ", StringComparison.Ordinal);
                var isCategory = period > 0 && rest[..period].Split(' ').Length <= 4;
                result = isCategory ? rest[(period + 2)..] : rest;
            }
        }

        return result.Trim();
    }

    private static bool TryParse(string text, out JsonElement element)
    {
        element = default;
        if (text.Length == 0)
        {
            return false;
        }

        var first = text[0];
        if (first != '{' && first != '[' && first != '"' && first != '-' && !char.IsDigit(first)
            && !text.StartsWith("true", StringComparison.Ordinal)
            && !text.StartsWith("false", StringComparison.Ordinal)
            && !text.StartsWith("null", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            element = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static JsonElement StringElement(string text) => JsonSerializer.SerializeToElement(text);

    internal static string Describe(IEnumerable<string> argv)
    {
        var builder = new StringBuilder("unity");
        foreach (var arg in argv)
        {
            builder.Append(' ');
            builder.Append(arg.Contains(' ') || arg.Contains('"') ? "'" + arg + "'" : arg);
        }

        return builder.ToString();
    }
}
