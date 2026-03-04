using Spectre.Console;
using System.Text;

internal sealed class ProjectViewRenderer
{
    private const int DefaultFrameWidth = 78;
    private const int MinFrameWidth = 40;
    private const int CommandRows = 8;
    private sealed record RenderLine(string Content, bool Highlight);

    public IReadOnlyList<string> Render(ProjectViewState state, int? highlightedEntryIndex = null, bool focusModeEnabled = false)
    {
        var frameWidth = ResolveFrameWidth();
        var lines = new List<string>();
        var cwd = string.IsNullOrWhiteSpace(state.RelativeCwd) ? "Assets" : state.RelativeCwd;
        var db = state.DbState == ProjectDbState.LockedImporting ? "LOCKED (Importing)" : "IDLE (Safe)";
        var focusLabel = focusModeEnabled
            ? " | FOCUS: ON (up/down, tab, shift+tab, esc)"
            : " | Focus Key: F7";
        var header = $" UnityCLI v{CliVersion.SemVer} | MODE: PROJECT | DB: {db} | CWD: {cwd}{focusLabel}";

        lines.Add(BorderTop(frameWidth));
        lines.Add(BorderBody(header, frameWidth));
        lines.Add(BorderSeparator(frameWidth));

        foreach (var treeLine in BuildTreeLines(state, cwd, highlightedEntryIndex))
        {
            lines.Add(BorderBody(treeLine.Content, frameWidth, treeLine.Highlight));
        }

        lines.Add(BorderSeparator(frameWidth));
        var contentWidth = Math.Max(1, frameWidth - 1);
        var wrappedTranscript = state.CommandTranscript
            .SelectMany(line => WrapTranscriptLine(line, contentWidth))
            .ToList();
        var recent = wrappedTranscript
            .Skip(Math.Max(0, wrappedTranscript.Count - CommandRows))
            .ToList();
        for (var i = 0; i < CommandRows; i++)
        {
            var line = i < recent.Count
                ? recent[i]
                : (i == recent.Count ? $"UnityCLI:{cwd} > _" : string.Empty);
            lines.Add(BorderBody($" {line}", frameWidth, allowMarkup: true));
        }

        lines.Add(BorderBottom(frameWidth));
        return lines;
    }

    private static IEnumerable<RenderLine> BuildTreeLines(ProjectViewState state, string cwd, int? highlightedEntryIndex)
    {
        yield return new RenderLine($" {GetRootLabel(cwd)}", false);
        foreach (var entry in state.VisibleEntries)
        {
            var prefix = entry.Depth == 0 ? string.Empty : new string(' ', 1) + string.Concat(Enumerable.Repeat("│   ", entry.Depth));
            var selected = highlightedEntryIndex == entry.Index;
            var marker = selected ? ">" : " ";
            var branch = $"{marker}{prefix}├── ";
            var label = entry.IsDirectory ? $"{entry.Name}/" : entry.Name;
            yield return new RenderLine($"{branch}[{entry.Index}] {label}", selected);
        }
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

    private static string BorderBody(string content, int frameWidth, bool highlight = false, bool allowMarkup = false)
    {
        var normalized = content.Replace(Environment.NewLine, " ").Replace("\n", " ");
        var adjusted = allowMarkup
            ? FitMarkup(normalized, frameWidth)
            : Markup.Escape(Fit(normalized, frameWidth));
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

    private static string FitMarkup(string markup, int width)
    {
        try
        {
            var plain = Markup.Remove(markup);
            if (plain.Length > width)
            {
                var truncated = plain[..Math.Max(0, width - 1)] + "…";
                return Markup.Escape(truncated);
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

    private static IReadOnlyList<string> WrapTranscriptLine(string line, int width)
    {
        if (string.IsNullOrEmpty(line))
        {
            return [string.Empty];
        }

        var safeWidth = Math.Max(1, width);
        var normalized = line
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var logicalLines = normalized.Split('\n');
        var wrapped = new List<string>();
        foreach (var logicalLine in logicalLines)
        {
            wrapped.AddRange(WrapMarkupLine(logicalLine, safeWidth));
        }

        return wrapped.Count == 0 ? [string.Empty] : wrapped;
    }

    private static IReadOnlyList<string> WrapMarkupLine(string markup, int width)
    {
        if (string.IsNullOrEmpty(markup))
        {
            return [string.Empty];
        }

        var lines = new List<string>();
        var current = new StringBuilder();
        var activeTags = new List<string>();
        var currentPlainLength = 0;

        void StartNewLine()
        {
            var next = new StringBuilder();
            foreach (var tag in activeTags)
            {
                next.Append('[').Append(tag).Append(']');
            }

            current = next;
            currentPlainLength = 0;
        }

        void FlushLineAndContinue()
        {
            for (var i = activeTags.Count - 1; i >= 0; i--)
            {
                current.Append("[/]");
            }

            lines.Add(current.ToString());
            StartNewLine();
        }

        void AppendLiteral(char ch)
        {
            if (currentPlainLength >= width)
            {
                FlushLineAndContinue();
            }

            if (ch == '[')
            {
                current.Append("[[");
            }
            else if (ch == ']')
            {
                current.Append("]]");
            }
            else
            {
                current.Append(ch);
            }

            currentPlainLength++;
        }

        for (var i = 0; i < markup.Length; i++)
        {
            var ch = markup[i];
            if (ch != '[')
            {
                AppendLiteral(ch);
                continue;
            }

            if (i + 1 < markup.Length && markup[i + 1] == '[')
            {
                AppendLiteral('[');
                i++;
                continue;
            }

            var close = markup.IndexOf(']', i + 1);
            if (close < 0)
            {
                AppendLiteral('[');
                continue;
            }

            var token = markup.Substring(i + 1, close - i - 1);
            if (token == "/")
            {
                if (activeTags.Count > 0)
                {
                    activeTags.RemoveAt(activeTags.Count - 1);
                }

                current.Append("[/]");
                i = close;
                continue;
            }

            if (token.Length == 0)
            {
                AppendLiteral('[');
                continue;
            }

            activeTags.Add(token);
            current.Append('[').Append(token).Append(']');
            i = close;
        }

        if (current.Length == 0)
        {
            lines.Add(string.Empty);
        }
        else
        {
            lines.Add(current.ToString());
        }

        return lines;
    }

    private static int ResolveFrameWidth()
    {
        var windowWidth = Console.WindowWidth;
        if (windowWidth <= 2)
        {
            return DefaultFrameWidth;
        }

        return Math.Max(MinFrameWidth, windowWidth - 2);
    }
}
