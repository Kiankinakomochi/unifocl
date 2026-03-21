using Spectre.Console;

internal sealed class ProjectViewRenderer
{
    private const int DefaultFrameWidth = TuiConsoleViewport.DefaultColumns - 2;
    private const int MinFrameWidth = 40;
    private const int FrameChromeRows = 4;
    private sealed record RenderLine(string Content, bool Highlight);

    public IReadOnlyList<string> Render(ProjectViewState state, int? highlightedEntryIndex = null, bool focusModeEnabled = false)
    {
        var frameWidth = ResolveFrameWidth();
        var lines = new List<string>();
        var cwd = string.IsNullOrWhiteSpace(state.RelativeCwd) ? "Assets" : state.RelativeCwd;
        var db = state.DbState == ProjectDbState.LockedImporting ? "LOCKED (Importing)" : "IDLE (Safe)";
        var focusLabel = focusModeEnabled
            ? " | FOCUS: ON (up/down, idx jump, tab, shift+tab, esc)"
            : " | Focus Key: F7";
        var header = $" UnityCLI v{CliVersion.SemVer} | MODE: PROJECT | DB: {db} | CWD: {cwd}{focusLabel}";

        lines.Add(BorderTop(frameWidth));
        lines.Add(BorderBody(header, frameWidth));
        lines.Add(BorderSeparator(frameWidth));

        foreach (var treeLine in BuildTreeLines(state, cwd, highlightedEntryIndex))
        {
            lines.Add(BorderBody(treeLine.Content, frameWidth, treeLine.Highlight));
        }

        lines.Add(BorderBottom(frameWidth));
        return lines;
    }

    private static IEnumerable<RenderLine> BuildTreeLines(ProjectViewState state, string cwd, int? highlightedEntryIndex)
    {
        var lines = new List<RenderLine>();

        var totalEntries = state.VisibleEntries.Count;
        var intendedRows = FrameChromeRows + 1 + totalEntries;
        var excessRows = TuiConsoleViewport.GetExcessRows(intendedRows);
        var availableTreeRows = Math.Max(0, ResolveWindowHeight() - FrameChromeRows);
        var treeRowBudget = Math.Max(0, (1 + totalEntries) - excessRows);
        treeRowBudget = Math.Min(availableTreeRows, treeRowBudget);
        var entryRowBudget = Math.Max(0, treeRowBudget - 1);

        if (treeRowBudget > 0)
        {
            lines.Add(new RenderLine($" {GetRootLabel(cwd)}", false));
        }

        if (totalEntries > entryRowBudget && entryRowBudget > 0)
        {
            entryRowBudget = Math.Max(1, entryRowBudget - 1);
        }

        var selectedPosition = highlightedEntryIndex is int highlighted
            ? state.VisibleEntries.FindIndex(entry => entry.Index == highlighted)
            : -1;
        if (selectedPosition < 0)
        {
            selectedPosition = 0;
        }

        var maxWindowStart = Math.Max(0, totalEntries - entryRowBudget);
        var centeredWindowStart = selectedPosition - (entryRowBudget / 2);
        var windowStart = Math.Clamp(centeredWindowStart, 0, maxWindowStart);
        var visibleEntries = state.VisibleEntries.Skip(windowStart).Take(entryRowBudget).ToList();
        foreach (var entry in visibleEntries)
        {
            var prefix = entry.Depth == 0 ? string.Empty : new string(' ', 1) + string.Concat(Enumerable.Repeat("│   ", entry.Depth));
            var selected = highlightedEntryIndex == entry.Index;
            var marker = selected ? ">" : " ";
            var branch = $"{marker}{prefix}├── ";
            var label = entry.IsDirectory ? $"{entry.Name}/" : entry.Name;
            lines.Add(new RenderLine($"{branch}[{entry.Index}] {label}", selected));
        }

        var omittedEntries = Math.Max(0, totalEntries - visibleEntries.Count);
        if (omittedEntries > 0 && treeRowBudget >= 2)
        {
            var rangeEnd = Math.Min(totalEntries, windowStart + visibleEntries.Count);
            lines.Add(new RenderLine($" … showing {windowStart + 1}-{rangeEnd}/{totalEntries} project entries (+{omittedEntries} more)", false));
        }

        return lines;
    }

    private static string GetRootLabel(string cwd)
    {
        var normalized = cwd.TrimEnd('/', '\\');
        var name = Path.GetFileName(normalized);
        return string.IsNullOrWhiteSpace(name) ? $"{normalized}/" : $"{name}/";
    }

    private static string BorderTop(int frameWidth) => "┌" + new string('─', frameWidth) + "┐";
    private static string BorderSeparator(int frameWidth) => "├" + new string('─', frameWidth) + "┤";
    private static string BorderBottom(int frameWidth) => "└" + new string('─', frameWidth) + "┘";

    private static string BorderBody(string content, int frameWidth, bool highlight = false)
    {
        var normalized = content.Replace(Environment.NewLine, " ").Replace("\n", " ");
        var adjusted = Markup.Escape(Fit(normalized, frameWidth));
        var line = $"│{adjusted}│";
        return highlight ? CliTheme.CursorWrapEscaped(line) : line;
    }

    private static string Fit(string text, int width)
    {
        if (text.Length > width)
        {
            return text[..Math.Max(0, width - 1)] + "…";
        }

        if (text.Length < width)
        {
            return text + new string(' ', width - text.Length);
        }

        return text;
    }

    private static int ResolveFrameWidth()
    {
        var (windowWidth, _) = TuiConsoleViewport.GetWindowSizeOrDefault();
        if (windowWidth <= 2)
        {
            return DefaultFrameWidth;
        }

        return Math.Max(MinFrameWidth, windowWidth - 2);
    }

    private static int ResolveWindowHeight()
    {
        var (_, windowHeight) = TuiConsoleViewport.GetWindowSizeOrDefault();
        return windowHeight > 0 ? windowHeight : TuiConsoleViewport.DefaultRows;
    }
}
