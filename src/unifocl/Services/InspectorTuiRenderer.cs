using Spectre.Console;

internal sealed class InspectorTuiRenderer
{
    private const int DefaultInnerWidth = 78;
    private const int MinInnerWidth = 40;
    private const int MinBodyRows = 4;
    private const int MinStreamRows = 4;
    private const int ReservedPromptRows = 4;
    private const int FrameOverheadRows = 5;
    private sealed record RenderRow(string Content, bool Highlight, bool AllowMarkup = false);

    public void Render(
        InspectorContext context,
        int? highlightedComponentIndex = null,
        string? highlightedFieldName = null,
        bool focusModeEnabled = false)
    {
        AnsiConsole.Clear();
        var innerWidth = ResolveInnerWidth();
        var availableRows = Math.Max(MinBodyRows + MinStreamRows, Console.WindowHeight - ReservedPromptRows);
        var dynamicRows = Math.Max(MinBodyRows + MinStreamRows, availableRows - FrameOverheadRows);

        var bodyRows = BuildBodyRows(context, highlightedComponentIndex, highlightedFieldName, innerWidth).ToList();
        var streamRows = BuildStreamRows(context, Math.Max(1, innerWidth - 1)).ToList();
        var hasStreamPane = streamRows.Count > 0;
        var (bodyViewportRows, streamViewportRows) = hasStreamPane
            ? AllocateViewportRows(dynamicRows, bodyRows.Count, streamRows.Count)
            : (Math.Max(1, dynamicRows), 0);
        var bodyOffset = context.BodyScrollOffset;
        EnsureFocusedRowVisibility(context, highlightedComponentIndex, highlightedFieldName, bodyRows.Count, bodyViewportRows, ref bodyOffset);
        var streamOffset = context.StreamScrollOffset;
        var visibleBody = SliceRows(bodyRows, bodyViewportRows, ref bodyOffset, followTail: false);
        var visibleStream = hasStreamPane
            ? SliceRows(streamRows, streamViewportRows, ref streamOffset, context.FollowStreamScroll)
            : Array.Empty<string>();
        context.BodyScrollOffset = bodyOffset;
        context.StreamScrollOffset = streamOffset;

        var frame = new List<string>(FrameOverheadRows + visibleBody.Count + (hasStreamPane ? visibleStream.Count + 1 : 0))
        {
            Border('┌', '┐', innerWidth),
            Line(BuildHeader(context, focusModeEnabled), innerWidth),
            Border('├', '┤', innerWidth)
        };

        frame.AddRange(visibleBody.Select(row => Line(row.Content, innerWidth, row.Highlight, row.AllowMarkup)));
        if (hasStreamPane)
        {
            frame.Add(Border('├', '┤', innerWidth));
            frame.AddRange(visibleStream.Select(streamLine => Line(streamLine, innerWidth, allowMarkup: true)));
        }
        frame.Add(Border('└', '┘', innerWidth));

        foreach (var line in frame)
        {
            CliTheme.MarkupLine(line);
        }
    }

    private static IEnumerable<RenderRow> BuildBodyRows(
        InspectorContext context,
        int? highlightedComponentIndex,
        string? highlightedFieldName,
        int innerWidth)
    {
        var rows = context.Depth == InspectorDepth.ComponentList
            ? BuildComponentRows(context, highlightedComponentIndex)
            : BuildFieldRows(context, highlightedFieldName, innerWidth);
        if (!context.InteractiveOverlayActive || string.IsNullOrWhiteSpace(context.InteractiveOverlayTitle))
        {
            return rows;
        }

        var overlayRows = new List<RenderRow>
        {
            new($"OVERLAY: {context.InteractiveOverlayTitle}", false),
            new(new string('─', Math.Max(1, innerWidth - 1)), false)
        };
        var overlayValue = context.InteractiveOverlayValue ?? string.Empty;
        overlayRows.AddRange(TuiTextWrap.WrapPlainText(overlayValue, Math.Max(10, innerWidth - 2)).Select(line => new RenderRow(line, false)));
        overlayRows.Add(new RenderRow(string.Empty, false));
        overlayRows.AddRange(rows);
        return overlayRows;
    }

    private static IEnumerable<RenderRow> BuildComponentRows(InspectorContext context, int? highlightedComponentIndex)
    {
        var lines = new List<RenderRow> { new("COMPONENTS:", false) };
        foreach (var component in context.Components)
        {
            var selected = highlightedComponentIndex == component.Index;
            var marker = selected ? "[bold deepskyblue1]▸[/]" : "[grey]·[/]";
            var name = Markup.Escape(component.Name);
            var indexCell = Markup.Escape($"[{component.Index}]");
            lines.Add(new RenderRow($"{marker}{indexCell} {name}", false, true));
        }

        if (lines.Count == 1)
        {
            lines.Add(new RenderRow(" [empty]", false));
        }

        return lines;
    }

    private static IEnumerable<RenderRow> BuildFieldRows(InspectorContext context, string? highlightedFieldName, int innerWidth)
    {
        var lines = new List<RenderRow>
        {
            new($"{PadRight("FIELD", 20)}{PadRight("VALUE", 26)}TYPE", false),
            new(new string('─', Math.Max(1, innerWidth - 1)), false)
        };

        if (context.InteractiveEditActive && !string.IsNullOrWhiteSpace(context.InteractiveEditFieldName))
        {
            var part = context.InteractiveEditPartCount > 0
                ? $" [{context.InteractiveEditPartIndex + 1}/{context.InteractiveEditPartCount}]"
                : string.Empty;
            var mode = string.IsNullOrWhiteSpace(context.InteractiveEditMode) ? "edit" : context.InteractiveEditMode;
            lines.Add(new RenderRow($"EDITING {context.InteractiveEditFieldName} ({mode}){part}", false));
        }

        foreach (var field in context.Fields)
        {
            var selected = string.Equals(highlightedFieldName, field.Name, StringComparison.OrdinalIgnoreCase);
            var marker = selected ? "[bold deepskyblue1]▸[/]" : "[grey]·[/]";
            var typeCell = Markup.Escape($"[{field.Type}]");
            var valueCell = BuildValueCell(context, field);
            var fieldCell = PadRight(Markup.Escape(field.Name), 19);
            var renderedValue = PadRight(Markup.Escape(valueCell), 26);
            lines.Add(new RenderRow($"{marker}{fieldCell}{renderedValue}{typeCell}", false, true));
        }

        if (context.Fields.Count == 0)
        {
            lines.Add(new RenderRow(" [empty]", false));
        }

        return lines;
    }

    private static string BuildValueCell(InspectorContext context, InspectorFieldEntry field)
    {
        if (!context.InteractiveEditActive
            || string.IsNullOrWhiteSpace(context.InteractiveEditFieldName)
            || !field.Name.Equals(context.InteractiveEditFieldName, StringComparison.OrdinalIgnoreCase))
        {
            return field.Value;
        }

        if (context.InteractiveEditMode?.Equals("vector", StringComparison.OrdinalIgnoreCase) == true
            && TryParseVectorTokens(field.Value, out var parts)
            && parts.Count > 0)
        {
            var selectedIndex = Math.Clamp(context.InteractiveEditPartIndex, 0, parts.Count - 1);
            parts[selectedIndex] = $"<{parts[selectedIndex]}>";
            return $"({string.Join(", ", parts)})";
        }

        if (context.InteractiveEditMode?.Equals("enum", StringComparison.OrdinalIgnoreCase) == true)
        {
            return $"<{field.Value}>";
        }

        if (context.InteractiveEditMode?.Equals("bool", StringComparison.OrdinalIgnoreCase) == true)
        {
            return $"<{field.Value}>";
        }

        if (context.InteractiveEditMode?.Equals("number", StringComparison.OrdinalIgnoreCase) == true)
        {
            return $"<{field.Value}>";
        }

        return field.Value;
    }

    private static bool TryParseVectorTokens(string value, out List<string> parts)
    {
        parts = [];
        var normalized = value.Trim();
        if (!normalized.StartsWith("(", StringComparison.Ordinal)
            || !normalized.EndsWith(")", StringComparison.Ordinal))
        {
            return false;
        }

        normalized = normalized[1..^1];
        parts = normalized
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(token => token.Trim())
            .Where(token => token.Length > 0)
            .ToList();
        return parts.Count is >= 2 and <= 4;
    }

    private static IEnumerable<string> BuildStreamRows(InspectorContext context, int contentWidth)
    {
        _ = contentWidth;

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
            ? " | FOCUS: ON (up/down, tab, enter edit, shift+tab, esc, f7/f8)"
            : " | Focus Key: F8";
        return $"UnityCLI v{CliVersion.SemVer} | MODE: INSPECTOR | Target: {target}{focusLabel}";
    }

    private static string Border(char left, char right, int innerWidth)
    {
        return $"{left}{new string('─', innerWidth)}{right}";
    }

    private static string Line(string content, int innerWidth, bool highlight = false, bool allowMarkup = false)
    {
        var payload = allowMarkup ? FitMarkup(content, innerWidth) : Markup.Escape(Fit(content, innerWidth));
        var line = $"│{payload}│";
        return highlight ? CliTheme.CursorWrapEscaped(line) : line;
    }

    private static string Fit(string text, int width)
    {
        var trimmed = text.Length > width ? text[..width] : text;
        return trimmed.PadRight(width);
    }

    private static string FitMarkup(string markup, int width)
    {
        try
        {
            var plain = Markup.Remove(markup);
            if (plain.Length > width)
            {
                return Markup.Escape(plain[..Math.Max(0, width - 1)] + "…");
            }

            if (plain.Length < width)
            {
                return markup + new string(' ', width - plain.Length);
            }

            return markup;
        }
        catch
        {
            return Markup.Escape(Fit(markup, width));
        }
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

    private static void EnsureFocusedRowVisibility(
        InspectorContext context,
        int? highlightedComponentIndex,
        string? highlightedFieldName,
        int totalRows,
        int viewportRows,
        ref int offset)
    {
        if (totalRows <= 0 || viewportRows <= 0)
        {
            offset = 0;
            return;
        }

        var overlayRows = 0;
        if (context.InteractiveOverlayActive && !string.IsNullOrWhiteSpace(context.InteractiveOverlayTitle))
        {
            overlayRows = 3;
            if (!string.IsNullOrWhiteSpace(context.InteractiveOverlayValue))
            {
                overlayRows += TuiTextWrap.WrapPlainText(context.InteractiveOverlayValue, Math.Max(10, ResolveInnerWidth() - 2)).Count;
            }
        }

        int? targetRow = null;
        if (context.Depth == InspectorDepth.ComponentList && highlightedComponentIndex is int componentIndex)
        {
            var position = context.Components.FindIndex(component => component.Index == componentIndex);
            if (position >= 0)
            {
                targetRow = overlayRows + 1 + position;
            }
        }
        else if (context.Depth == InspectorDepth.ComponentFields && !string.IsNullOrWhiteSpace(highlightedFieldName))
        {
            var position = context.Fields.FindIndex(field => field.Name.Equals(highlightedFieldName, StringComparison.OrdinalIgnoreCase));
            if (position >= 0)
            {
                targetRow = overlayRows + 2 + position;
            }
        }

        var maxOffset = Math.Max(0, totalRows - viewportRows);
        offset = Math.Clamp(offset, 0, maxOffset);
        if (targetRow is null)
        {
            return;
        }

        if (targetRow < offset)
        {
            offset = Math.Max(0, targetRow.Value - 1);
            return;
        }

        var bottom = offset + viewportRows - 1;
        if (targetRow > bottom)
        {
            offset = Math.Max(0, targetRow.Value - viewportRows + 2);
        }

        offset = Math.Clamp(offset, 0, maxOffset);
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

    private static int ResolveInnerWidth()
    {
        var windowWidth = Console.WindowWidth;
        if (windowWidth <= 2)
        {
            return DefaultInnerWidth;
        }

        return Math.Max(MinInnerWidth, windowWidth - 2);
    }
}
