using Spectre.Console;

internal sealed class ProjectViewRenderer
{
    private const int FrameWidth = 78;
    private const int CommandRows = 8;

    public IReadOnlyList<string> Render(ProjectViewState state)
    {
        var lines = new List<string>();
        var cwd = string.IsNullOrWhiteSpace(state.RelativeCwd) ? "Assets" : state.RelativeCwd;
        var db = state.DbState == ProjectDbState.LockedImporting ? "LOCKED (Importing)" : "IDLE (Safe)";
        var header = $" UnityCLI v0.1 | MODE: PROJECT | DB: {db} | CWD: {cwd}";

        lines.Add(BorderTop());
        lines.Add(BorderBody(header));
        lines.Add(BorderSeparator());

        foreach (var treeLine in BuildTreeLines(state, cwd))
        {
            lines.Add(BorderBody(treeLine));
        }

        lines.Add(BorderSeparator());
        var recent = state.CommandTranscript
            .Skip(Math.Max(0, state.CommandTranscript.Count - CommandRows))
            .ToList();
        for (var i = 0; i < CommandRows; i++)
        {
            var line = i < recent.Count
                ? recent[i]
                : (i == recent.Count ? $"UnityCLI:{cwd} > _" : string.Empty);
            lines.Add(BorderBody($" {line}"));
        }

        lines.Add(BorderBottom());
        return lines.Select(Markup.Escape).ToList();
    }

    private static IEnumerable<string> BuildTreeLines(ProjectViewState state, string cwd)
    {
        yield return $" {GetRootLabel(cwd)}";
        foreach (var entry in state.VisibleEntries)
        {
            var prefix = entry.Depth == 0 ? string.Empty : new string(' ', 1) + string.Concat(Enumerable.Repeat("│   ", entry.Depth));
            var branch = $" {prefix}├── ";
            var label = entry.IsDirectory ? $"{entry.Name}/" : entry.Name;
            yield return $"{branch}[{entry.Index}] {label}";
        }
    }

    private static string GetRootLabel(string cwd)
    {
        var normalized = cwd.TrimEnd('/', '\\');
        var name = Path.GetFileName(normalized);
        return string.IsNullOrWhiteSpace(name) ? $"{normalized}/" : $"{name}/";
    }

    private static string BorderTop() => "┌" + new string('─', FrameWidth) + "┐";
    private static string BorderSeparator() => "├" + new string('─', FrameWidth) + "┤";
    private static string BorderBottom() => "└" + new string('─', FrameWidth) + "┘";

    private static string BorderBody(string content)
    {
        var normalized = content.Replace(Environment.NewLine, " ").Replace("\n", " ");
        var adjusted = Fit(normalized, FrameWidth);
        return $"│{adjusted}│";
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
}
