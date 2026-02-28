using Spectre.Console;

internal sealed class InspectorTuiRenderer
{
    private const int InnerWidth = 78;
    private const int MinBodyRows = 4;
    private const int MinStreamRows = 4;
    private const int ReservedPromptRows = 4;
    private const int FrameOverheadRows = 5;
    private sealed record RenderRow(string Content, bool Highlight);

    public void Render(
        InspectorContext context,
        int? highlightedComponentIndex = null,
        string? highlightedFieldName = null,
        bool focusModeEnabled = false)
    {
        AnsiConsole.Clear();
        var availableRows = Math.Max(MinBodyRows + MinStreamRows, Console.WindowHeight - ReservedPromptRows);
        var dynamicRows = Math.Max(MinBodyRows + MinStreamRows, availableRows - FrameOverheadRows);

        var bodyRows = BuildBodyRows(context, highlightedComponentIndex, highlightedFieldName).ToList();
        var streamRows = BuildStreamRows(context).ToList();
        var hasStreamPane = streamRows.Count > 0;
        var (bodyViewportRows, streamViewportRows) = hasStreamPane
            ? AllocateViewportRows(dynamicRows, bodyRows.Count, streamRows.Count)
            : (Math.Max(1, dynamicRows), 0);
        var bodyOffset = context.BodyScrollOffset;
        var streamOffset = context.StreamScrollOffset;
        var visibleBody = SliceRows(bodyRows, bodyViewportRows, ref bodyOffset, followTail: false);
        var visibleStream = hasStreamPane
            ? SliceRows(streamRows, streamViewportRows, ref streamOffset, context.FollowStreamScroll)
            : Array.Empty<string>();
        context.BodyScrollOffset = bodyOffset;
        context.StreamScrollOffset = streamOffset;

        var frame = new List<string>(FrameOverheadRows + visibleBody.Count + (hasStreamPane ? visibleStream.Count + 1 : 0))
        {
            Border('┌', '┐'),
            Line(BuildHeader(context, focusModeEnabled)),
            Border('├', '┤')
        };

        frame.AddRange(visibleBody.Select(row => Line(row.Content, row.Highlight)));
        if (hasStreamPane)
        {
            frame.Add(Border('├', '┤'));
            frame.AddRange(visibleStream.Select(streamLine => Line(streamLine)));
        }
        frame.Add(Border('└', '┘'));

        foreach (var line in frame)
        {
            CliTheme.MarkupLine(line);
        }
    }

    private static IEnumerable<RenderRow> BuildBodyRows(
        InspectorContext context,
        int? highlightedComponentIndex,
        string? highlightedFieldName)
    {
        return context.Depth == InspectorDepth.ComponentList
            ? BuildComponentRows(context, highlightedComponentIndex)
            : BuildFieldRows(context, highlightedFieldName);
    }

    private static IEnumerable<RenderRow> BuildComponentRows(InspectorContext context, int? highlightedComponentIndex)
    {
        var lines = new List<RenderRow> { new("COMPONENTS:", false) };
        foreach (var component in context.Components)
        {
            var selected = highlightedComponentIndex == component.Index;
            var marker = selected ? ">" : " ";
            lines.Add(new RenderRow($"{marker}[{component.Index}] {component.Name}", selected));
        }

        if (lines.Count == 1)
        {
            lines.Add(new RenderRow(" [empty]", false));
        }

        return lines;
    }

    private static IEnumerable<RenderRow> BuildFieldRows(InspectorContext context, string? highlightedFieldName)
    {
        var lines = new List<RenderRow>
        {
            new($"{PadRight("FIELD", 20)}{PadRight("VALUE", 26)}TYPE", false),
            new(new string('─', InnerWidth - 1), false)
        };

        foreach (var field in context.Fields)
        {
            var selected = string.Equals(highlightedFieldName, field.Name, StringComparison.OrdinalIgnoreCase);
            var marker = selected ? ">" : " ";
            var typeCell = $"[{field.Type}]";
            lines.Add(new RenderRow($"{marker}{PadRight(field.Name, 19)}{PadRight(field.Value, 26)}{typeCell}", selected));
        }

        if (context.Fields.Count == 0)
        {
            lines.Add(new RenderRow(" [empty]", false));
        }

        return lines;
    }

    private static IEnumerable<string> BuildStreamRows(InspectorContext context)
    {
        var rows = context.CommandStream
            .Where(line => !line.StartsWith("UnityCLI:", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (rows.Count == 0)
        {
            return [];
        }

        return rows;
    }

    private static string BuildHeader(InspectorContext context, bool focusModeEnabled)
    {
        var target = context.Depth == InspectorDepth.ComponentFields
            ? context.PromptPath
            : context.TargetPath;
        var focusLabel = focusModeEnabled
            ? " | FOCUS: ON (up/down, tab, shift+tab, esc)"
            : " | Focus Key: F8";
        return $"UnityCLI v0.1 | MODE: INSPECTOR | Target: {target}{focusLabel}";
    }

    private static string Border(char left, char right)
    {
        return $"{left}{new string('─', InnerWidth)}{right}";
    }

    private static string Line(string content, bool highlight = false)
    {
        var trimmed = content.Length > InnerWidth ? content[..InnerWidth] : content;
        var escaped = Markup.Escape(trimmed.PadRight(InnerWidth));
        var line = $"│{escaped}│";
        return highlight ? CliTheme.CursorWrapEscaped(line) : line;
    }

    private static (int BodyRows, int StreamRows) AllocateViewportRows(int dynamicRows, int bodyCount, int streamCount)
    {
        var preferredBody = Math.Max(MinBodyRows, bodyCount);
        var preferredStream = Math.Max(MinStreamRows, streamCount);
        var preferredTotal = preferredBody + preferredStream;
        if (preferredTotal <= dynamicRows)
        {
            return (preferredBody, preferredStream);
        }

        var streamRows = Math.Min(preferredStream, Math.Max(MinStreamRows, dynamicRows / 3));
        var bodyRows = dynamicRows - streamRows;
        if (bodyRows < MinBodyRows)
        {
            bodyRows = MinBodyRows;
            streamRows = Math.Max(MinStreamRows, dynamicRows - bodyRows);
        }

        return (Math.Max(1, bodyRows), Math.Max(1, streamRows));
    }

    private static IReadOnlyList<RenderRow> SliceRows(
        IReadOnlyList<RenderRow> source,
        int viewportRows,
        ref int offset,
        bool followTail)
    {
        viewportRows = Math.Max(1, viewportRows);
        var rows = source.Count == 0 ? new List<RenderRow> { new(string.Empty, false) } : source.ToList();
        var maxOffset = Math.Max(0, rows.Count - viewportRows);
        if (followTail)
        {
            offset = maxOffset;
        }

        offset = Math.Clamp(offset, 0, maxOffset);
        var visible = rows.Skip(offset).Take(viewportRows).ToList();
        while (visible.Count < viewportRows)
        {
            visible.Add(new RenderRow(string.Empty, false));
        }

        var hiddenAbove = offset;
        var hiddenBelow = Math.Max(0, rows.Count - (offset + visible.Count));
        if (hiddenAbove > 0 && visible.Count > 0)
        {
            visible[0] = new RenderRow($"... {hiddenAbove} line(s) above ...", false);
        }

        if (hiddenBelow > 0 && visible.Count > 1)
        {
            visible[^1] = new RenderRow($"... {hiddenBelow} line(s) below ...", false);
        }

        return visible;
    }

    private static IReadOnlyList<string> SliceRows(
        IReadOnlyList<string> source,
        int viewportRows,
        ref int offset,
        bool followTail)
    {
        viewportRows = Math.Max(1, viewportRows);
        var rows = source.Count == 0 ? new List<string> { string.Empty } : source.ToList();
        var maxOffset = Math.Max(0, rows.Count - viewportRows);
        if (followTail)
        {
            offset = maxOffset;
        }

        offset = Math.Clamp(offset, 0, maxOffset);
        var visible = rows.Skip(offset).Take(viewportRows).ToList();
        while (visible.Count < viewportRows)
        {
            visible.Add(string.Empty);
        }

        var hiddenAbove = offset;
        var hiddenBelow = Math.Max(0, rows.Count - (offset + visible.Count));
        if (hiddenAbove > 0 && visible.Count > 0)
        {
            visible[0] = $"... {hiddenAbove} line(s) above ...";
        }

        if (hiddenBelow > 0 && visible.Count > 1)
        {
            visible[^1] = $"... {hiddenBelow} line(s) below ...";
        }

        return visible;
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
