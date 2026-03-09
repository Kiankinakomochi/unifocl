using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

internal static class AgenticFormatter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly Regex MarkupTagRegex = new(@"\[(\/)?[^\]]+\]", RegexOptions.Compiled);

    public static string SerializeEnvelope(AgenticResponseEnvelope envelope, AgenticOutputFormat format)
    {
        return format == AgenticOutputFormat.Yaml
            ? SerializeYaml(envelope)
            : JsonSerializer.Serialize(envelope, JsonOptions);
    }

    public static string SerializeYaml(object value)
    {
        var element = JsonSerializer.SerializeToElement(value, JsonOptions);
        var sb = new StringBuilder();
        WriteYamlElement(element, sb, 0, null);
        return sb.ToString().TrimEnd();
    }

    public static string StripMarkup(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        return MarkupTagRegex.Replace(input, string.Empty).Trim();
    }

    private static void WriteYamlElement(JsonElement element, StringBuilder sb, int indent, string? propertyName)
    {
        var prefix = new string(' ', indent);
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (propertyName is not null)
                {
                    sb.Append(prefix).Append(propertyName).Append(':').AppendLine();
                }

                foreach (var property in element.EnumerateObject())
                {
                    WriteYamlElement(property.Value, sb, indent + (propertyName is null ? 0 : 2), property.Name);
                }
                break;
            case JsonValueKind.Array:
                if (propertyName is not null)
                {
                    sb.Append(prefix).Append(propertyName).Append(':').AppendLine();
                }

                foreach (var item in element.EnumerateArray())
                {
                    var itemPrefix = new string(' ', indent + (propertyName is null ? 0 : 2));
                    if (item.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    {
                        sb.Append(itemPrefix).Append('-').AppendLine();
                        WriteYamlElement(item, sb, indent + (propertyName is null ? 2 : 4), null);
                    }
                    else
                    {
                        sb.Append(itemPrefix).Append("- ").Append(FormatScalar(item)).AppendLine();
                    }
                }
                break;
            default:
                if (propertyName is null)
                {
                    sb.Append(prefix).Append(FormatScalar(element)).AppendLine();
                }
                else
                {
                    sb.Append(prefix).Append(propertyName).Append(": ").Append(FormatScalar(element)).AppendLine();
                }
                break;
        }
    }

    private static string FormatScalar(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Null => "null",
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.String => QuoteYamlString(element.GetString() ?? string.Empty),
            _ => QuoteYamlString(element.GetRawText())
        };
    }

    private static string QuoteYamlString(string value)
    {
        var escaped = value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        return $"\"{escaped}\"";
    }
}
