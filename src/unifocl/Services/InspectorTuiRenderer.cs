using Spectre.Console;

internal sealed class InspectorTuiRenderer
{
    private const int InnerWidth = 78;
    private const int BodyHeight = 9;
    private const int StreamHeight = 10;

    public void Render(InspectorContext context)
    {
        AnsiConsole.Clear();

        var frame = new List<string>
        {
            Border('┌', '┐'),
            Line(BuildHeader(context)),
            Border('├', '┤')
        };

        frame.AddRange(context.Depth == InspectorDepth.ComponentList
            ? BuildComponentBody(context)
            : BuildFieldBody(context));

        frame.Add(Border('├', '┤'));
        frame.AddRange(BuildStreamBody(context));
        frame.Add(Border('└', '┘'));

        foreach (var line in frame)
        {
            AnsiConsole.MarkupLine(Markup.Escape(line));
        }
    }

    private static IEnumerable<string> BuildComponentBody(InspectorContext context)
    {
        var lines = new List<string> { Line("COMPONENTS:") };
        foreach (var component in context.Components)
        {
            lines.Add(Line($" [{component.Index}] {component.Name}"));
        }

        while (lines.Count < BodyHeight)
        {
            lines.Add(Line(string.Empty));
        }

        return lines.Take(BodyHeight);
    }

    private static IEnumerable<string> BuildFieldBody(InspectorContext context)
    {
        var lines = new List<string>
        {
            Line($"{PadRight("FIELD", 20)}{PadRight("VALUE", 26)}TYPE"),
            Line(new string('─', InnerWidth - 1))
        };

        foreach (var field in context.Fields)
        {
            var typeCell = $"[{field.Type}]";
            lines.Add(Line($"{PadRight(field.Name, 20)}{PadRight(field.Value, 26)}{typeCell}"));
        }

        while (lines.Count < BodyHeight)
        {
            lines.Add(Line(string.Empty));
        }

        return lines.Take(BodyHeight);
    }

    private static IEnumerable<string> BuildStreamBody(InspectorContext context)
    {
        var stream = context.CommandStream
            .TakeLast(StreamHeight)
            .ToList();

        while (stream.Count < StreamHeight)
        {
            stream.Add(string.Empty);
        }

        return stream.Select(Line);
    }

    private static string BuildHeader(InspectorContext context)
    {
        var target = context.Depth == InspectorDepth.ComponentFields
            ? context.PromptPath
            : context.TargetPath;
        return $"UnityCLI v0.1 | MODE: INSPECTOR | Target: {target}";
    }

    private static string Border(char left, char right)
    {
        return $"{left}{new string('─', InnerWidth)}{right}";
    }

    private static string Line(string content)
    {
        var trimmed = content.Length > InnerWidth ? content[..InnerWidth] : content;
        return $"│{trimmed.PadRight(InnerWidth)}│";
    }

    private static string PadRight(string value, int width)
    {
        if (value.Length >= width)
        {
            return value[..Math.Min(value.Length, width - 1)] + " ";
        }

        return value.PadRight(width);
    }
}
