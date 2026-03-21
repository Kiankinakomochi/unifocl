using Spectre.Console;
using System.Text;

internal static class CliComposerService
{
    private const int IdleLoopSleepMilliseconds = 16;
    private const int StreamLogSignatureTailLines = 24;
    private const int MaxComposerStreamLogLines = 24;
    private const int MaxPinnedIntellisenseSuggestions = 5;
    private const int LogScrollStepLines = 3;
    private static bool _hasAnchor;
    private static int _anchorLeft;
    private static int _anchorTop;
    private static bool _bootLogoCollapsed;

    private static string PromptDividerLine
    {
        get
        {
            var width = 64;
            try
            {
                if (!Console.IsOutputRedirected)
                {
                    width = Math.Max(24, Console.WindowWidth - 2);
                }
            }
            catch
            {
                width = 64;
            }

            return $"[grey]{new string('─', width)}[/]";
        }
    }

    public static string? ReadInput(
        List<CommandSpec> commands,
        List<CommandSpec> projectCommands,
        List<CommandSpec> inspectorCommands,
        List<string> streamLog,
        CliSessionState session)
    {
        if (Console.IsInputRedirected)
        {
            CliTheme.MarkupLine(PromptDividerLine);
            CliTheme.Markup($"{BuildPromptLabelMarkup(session)} [grey]>[/] ");
            var line = Console.ReadLine();
            CliTheme.MarkupLine(PromptDividerLine);
            return line;
        }

        return ReadInteractiveInput(commands, projectCommands, inspectorCommands, streamLog, session);
    }

    public static void ResetBootLogoCollapsed()
    {
        _bootLogoCollapsed = false;
    }

    private static string? ReadInteractiveInput(
        List<CommandSpec> commands,
        List<CommandSpec> projectCommands,
        List<CommandSpec> inspectorCommands,
        List<string> streamLog,
        CliSessionState session)
    {
        var input = new StringBuilder();
        var selectedIntellisenseCandidateIndex = 0;
        var intellisenseDismissed = false;
        var streamLogScrollOffset = 0;
        var isDirty = true;
        var lastFrameSignature = string.Empty;
        var lastStreamLogSignature = GetStreamLogSignature(streamLog);
        _ = TryGetWindowSize(out var lastWindowWidth, out var lastWindowHeight);
        SaveComposerAnchor();

        while (true)
        {
            var composerResetRequested = false;
            _ = CliComposerIntellisenseService.TryGetComposerIntellisenseCandidates(input.ToString(), commands, projectCommands, inspectorCommands, session, out var allCandidates);
            var candidates = intellisenseDismissed ? [] : allCandidates;
            if (candidates.Count == 0)
            {
                selectedIntellisenseCandidateIndex = 0;
            }
            else
            {
                selectedIntellisenseCandidateIndex = Math.Clamp(selectedIntellisenseCandidateIndex, 0, candidates.Count - 1);
            }

            if (Console.KeyAvailable)
            {
                var key = Console.ReadKey(intercept: true);
                if (IsLogScrollUpKey(key))
                {
                    if (streamLog.Count > 0)
                    {
                        var maxOffset = Math.Max(0, streamLog.Count - 1);
                        var nextOffset = Math.Min(maxOffset, streamLogScrollOffset + LogScrollStepLines);
                        if (nextOffset != streamLogScrollOffset)
                        {
                            streamLogScrollOffset = nextOffset;
                            isDirty = true;
                        }
                    }

                    continue;
                }

                if (IsLogScrollDownKey(key))
                {
                    if (streamLogScrollOffset > 0)
                    {
                        var nextOffset = Math.Max(0, streamLogScrollOffset - LogScrollStepLines);
                        if (nextOffset != streamLogScrollOffset)
                        {
                            streamLogScrollOffset = nextOffset;
                            isDirty = true;
                        }
                    }

                    continue;
                }

                if (IsLogScrollLatestKey(key))
                {
                    if (streamLogScrollOffset != 0)
                    {
                        streamLogScrollOffset = 0;
                        isDirty = true;
                    }

                    continue;
                }

                switch (key.Key)
                {
                    case ConsoleKey.Enter:
                        if (CliComposerIntellisenseService.IsCatalogCommandInput(input.ToString(), commands, projectCommands, inspectorCommands, session))
                        {
                            Console.WriteLine();
                            return input.ToString();
                        }

                        if (candidates.Count > 0
                            && selectedIntellisenseCandidateIndex >= 0
                            && selectedIntellisenseCandidateIndex < candidates.Count
                            && !string.IsNullOrWhiteSpace(candidates[selectedIntellisenseCandidateIndex].CommitCommand))
                        {
                            var currentInput = input.ToString();
                            var acceptedCommand = candidates[selectedIntellisenseCandidateIndex].CommitCommand!;
                            input.Clear();
                            input.Append(CliCommandParsingService.MergeAcceptedSuggestion(currentInput, acceptedCommand));
                            // Accepting a suggestion exits IntelliSense so next Enter executes.
                            intellisenseDismissed = true;
                            isDirty = true;
                            break;
                        }

                        Console.WriteLine();
                        return input.ToString();
                    case ConsoleKey.Backspace:
                        if (input.Length > 0)
                        {
                            input.Remove(input.Length - 1, 1);
                            isDirty = true;
                        }

                        intellisenseDismissed = false;
                        break;
                    case ConsoleKey.Escape:
                        if (!intellisenseDismissed && allCandidates.Count > 0)
                        {
                            intellisenseDismissed = true;
                            isDirty = true;
                        }
                        else if (input.Length > 0 || intellisenseDismissed)
                        {
                            input.Clear();
                            intellisenseDismissed = false;
                            selectedIntellisenseCandidateIndex = 0;
                            isDirty = true;
                        }

                        selectedIntellisenseCandidateIndex = 0;
                        break;
                    case ConsoleKey.UpArrow:
                        if (intellisenseDismissed && allCandidates.Count > 0)
                        {
                            intellisenseDismissed = false;
                            candidates = allCandidates;
                            isDirty = true;
                        }

                        if (candidates.Count > 0)
                        {
                            selectedIntellisenseCandidateIndex = selectedIntellisenseCandidateIndex <= 0
                                ? candidates.Count - 1
                                : selectedIntellisenseCandidateIndex - 1;
                            isDirty = true;
                        }
                        break;
                    case ConsoleKey.DownArrow:
                        if (intellisenseDismissed && allCandidates.Count > 0)
                        {
                            intellisenseDismissed = false;
                            candidates = allCandidates;
                            isDirty = true;
                        }

                        if (candidates.Count > 0)
                        {
                            selectedIntellisenseCandidateIndex = selectedIntellisenseCandidateIndex >= candidates.Count - 1
                                ? 0
                                : selectedIntellisenseCandidateIndex + 1;
                            isDirty = true;
                        }
                        break;
                    case ConsoleKey.F7:
                        if (input.Length == 0
                            && session.ContextMode == CliContextMode.Inspector
                            && session.Inspector is not null)
                        {
                            Console.WriteLine();
                            return ":focus-inspector";
                        }

                        if (input.Length == 0
                            && session.ContextMode == CliContextMode.Project
                            && !string.IsNullOrWhiteSpace(session.CurrentProjectPath))
                        {
                            Console.WriteLine();
                            return ":focus-project";
                        }

                        if (input.Length == 0
                            && (session.Mode != CliMode.Project || string.IsNullOrWhiteSpace(session.CurrentProjectPath))
                            && session.RecentProjectEntries.Count > 0)
                        {
                            Console.WriteLine();
                            return ":focus-recent";
                        }
                        break;
                    default:
                        if (!char.IsControl(key.KeyChar))
                        {
                            CollapseBootLogoAfterFirstInput(streamLog, ref composerResetRequested);
                            input.Append(key.KeyChar);
                            intellisenseDismissed = false;
                            isDirty = true;
                        }
                        break;
                }
            }

            if (composerResetRequested)
            {
                SaveComposerAnchor();
                lastFrameSignature = string.Empty;
                _ = TryGetWindowSize(out lastWindowWidth, out lastWindowHeight);
                isDirty = true;
            }

            var currentStreamLogSignature = GetStreamLogSignature(streamLog);
            if (!string.Equals(lastStreamLogSignature, currentStreamLogSignature, StringComparison.Ordinal))
            {
                lastStreamLogSignature = currentStreamLogSignature;
                isDirty = true;
            }

            var maxScrollOffset = Math.Max(0, streamLog.Count - 1);
            if (streamLogScrollOffset > maxScrollOffset)
            {
                streamLogScrollOffset = maxScrollOffset;
                isDirty = true;
            }

            if (TryGetWindowSize(out var currentWindowWidth, out var currentWindowHeight)
                && (currentWindowWidth != lastWindowWidth || currentWindowHeight != lastWindowHeight))
            {
                lastWindowWidth = currentWindowWidth;
                lastWindowHeight = currentWindowHeight;
                isDirty = true;
            }

            if (isDirty)
            {
                var lines = BuildComposerFrameLines(
                    input.ToString(),
                    commands,
                    projectCommands,
                    inspectorCommands,
                    streamLog,
                    session,
                    selectedIntellisenseCandidateIndex,
                    intellisenseDismissed,
                    streamLogScrollOffset);
                var frameSignature = string.Join('\n', lines);
                if (!string.Equals(lastFrameSignature, frameSignature, StringComparison.Ordinal))
                {
                    ClearComposerFrame();
                    foreach (var line in lines)
                    {
                        CliTheme.MarkupLine(line);
                    }

                    lastFrameSignature = frameSignature;
                }

                isDirty = false;
            }

            Thread.Sleep(IdleLoopSleepMilliseconds);
        }
    }

    private static void CollapseBootLogoAfterFirstInput(List<string> streamLog, ref bool composerResetRequested)
    {
        if (_bootLogoCollapsed || Console.IsOutputRedirected)
        {
            return;
        }

        var hadLogoGlyph = streamLog.RemoveAll(line => line.Contains('█')) > 0;
        if (!hadLogoGlyph)
        {
            _bootLogoCollapsed = true;
            return;
        }

        for (var i = streamLog.Count - 1; i > 0; i--)
        {
            if (string.IsNullOrWhiteSpace(streamLog[i]) && string.IsNullOrWhiteSpace(streamLog[i - 1]))
            {
                streamLog.RemoveAt(i);
            }
        }

        try
        {
            Console.Clear();
        }
        catch
        {
            // Keep interaction resilient when clear is unsupported.
        }

        CliLogService.RenderInitialLog(streamLog);
        _bootLogoCollapsed = true;
        composerResetRequested = true;
    }

    private static List<string> BuildComposerFrameLines(
        string input,
        List<CommandSpec> commands,
        List<CommandSpec> projectCommands,
        List<CommandSpec> inspectorCommands,
        List<string> streamLog,
        CliSessionState session,
        int selectedFuzzyCandidateIndex,
        bool suppressIntellisense,
        int streamLogScrollOffset)
    {
        var pinnedBottomLines = new List<string>
        {
            PromptDividerLine,
            $"{BuildPromptLabelMarkup(session)} [grey]>[/] [bold white]{Markup.Escape(input)}[/]"
        };
        pinnedBottomLines.AddRange(
            BuildPinnedIntellisenseLines(
                input,
                commands,
                projectCommands,
                inspectorCommands,
                session,
                selectedFuzzyCandidateIndex,
                suppressIntellisense));
        pinnedBottomLines.Add(PromptDividerLine);

        var maxComposerRows = ResolveMaxComposerRows();
        var pinnedBottomRows = CountVisualRows(pinnedBottomLines);
        var logRowsBudget = maxComposerRows == int.MaxValue
            ? MaxComposerStreamLogLines
            : Math.Max(0, maxComposerRows - pinnedBottomRows);

        var lines = new List<string>();
        lines.AddRange(BuildComposerStreamLogLines(streamLog, logRowsBudget, streamLogScrollOffset));
        lines.AddRange(pinnedBottomLines);

        ConstrainComposerLinesToViewport(lines);
        return lines;
    }

    private static List<string> BuildComposerStreamLogLines(
        List<string> streamLog,
        int rowBudget,
        int scrollOffsetFromTail)
    {
        if (streamLog.Count == 0 || rowBudget <= 0)
        {
            return [];
        }

        var clampedOffset = Math.Clamp(scrollOffsetFromTail, 0, Math.Max(0, streamLog.Count - 1));
        var endExclusive = Math.Max(0, streamLog.Count - clampedOffset);
        if (endExclusive <= 0)
        {
            return [];
        }

        var usedRows = 0;
        var start = endExclusive;
        for (var i = endExclusive - 1; i >= 0; i--)
        {
            var rows = EstimateVisualRows(streamLog[i]);
            if (usedRows + rows > rowBudget)
            {
                break;
            }

            usedRows += rows;
            start = i;
        }

        var lines = new List<string>(Math.Max(0, endExclusive - start));
        for (var i = start; i < endExclusive; i++)
        {
            lines.Add(streamLog[i]);
        }

        return lines;
    }

    private static List<string> BuildPinnedIntellisenseLines(
        string input,
        List<CommandSpec> commands,
        List<CommandSpec> projectCommands,
        List<CommandSpec> inspectorCommands,
        CliSessionState session,
        int selectedSuggestionIndex,
        bool suppressIntellisense)
    {
        if (suppressIntellisense)
        {
            return ["[dim]intellisense dismissed (Esc). Type or use ↑/↓ to reopen suggestions.[/]"];
        }

        if (!CliComposerIntellisenseService.TryGetComposerIntellisenseCandidates(
                input,
                commands,
                projectCommands,
                inspectorCommands,
                session,
                out var candidates))
        {
            if (session.Inspector is not null)
            {
                return [];
            }

            if (session.ContextMode == CliContextMode.Project && !string.IsNullOrWhiteSpace(session.CurrentProjectPath))
            {
                return ["[dim]Project mode: / commands, up/down suggestions, Enter to accept[/]"];
            }

            return ["[dim]Type / to open command palette. Use your mouse wheel to scroll log history.[/]"];
        }

        if (candidates.Count == 0)
        {
            return
            [
                "[grey]intellisense[/]: command suggestions [dim](up/down + enter to insert)[/]",
                $"[dim]no matches for {Markup.Escape(input)}[/]"
            ];
        }

        var selected = Math.Clamp(selectedSuggestionIndex, 0, candidates.Count - 1);
        var visibleCount = Math.Min(MaxPinnedIntellisenseSuggestions, candidates.Count);
        var startIndex = 0;
        if (candidates.Count > visibleCount && selected >= visibleCount)
        {
            startIndex = Math.Clamp(selected - visibleCount + 1, 0, candidates.Count - visibleCount);
        }

        var endIndexExclusive = startIndex + visibleCount;
        var lines = new List<string>
        {
            "[grey]intellisense[/]: command suggestions [dim](up/down + enter to insert)[/]"
        };

        for (var i = startIndex; i < endIndexExclusive; i++)
        {
            var selectedLine = i == selected;
            var prefix = selectedLine
                ? $"[{CliTheme.CursorForeground} on {CliTheme.CursorBackground}]>[/]"
                : "[grey] [/]";
            var escapedLabel = Markup.Escape(candidates[i].Label);
            lines.Add(selectedLine
                ? $"{prefix} [{CliTheme.CursorForeground} on {CliTheme.CursorBackground}]{escapedLabel}[/]"
                : $"{prefix} [grey]{escapedLabel}[/]");
        }

        if (candidates.Count > visibleCount)
        {
            lines.Add($"[dim]showing {startIndex + 1}-{endIndexExclusive}/{candidates.Count}[/]");
        }

        lines.Add("[dim]logs: PgUp/PgDn/End (Win) or Ctrl+B/Ctrl+F/Ctrl+E (mac) [/]");

        return lines;
    }

    private static string BuildPromptLabelMarkup(CliSessionState session)
    {
        var context = session.Inspector;
        if (context is not null)
        {
            var escapedPromptPath = Markup.Escape(context.PromptPath);
            return context.Depth == InspectorDepth.ComponentList
                ? $"[bold deepskyblue1]UnityCLI[/][grey]:[/][{CliTheme.Info}]{escapedPromptPath}[/] [grey][[inspect]][/]"
                : $"[bold deepskyblue1]UnityCLI[/][grey]:[/][{CliTheme.Info}]{escapedPromptPath}[/]";
        }

        if (session.ContextMode == CliContextMode.Project && !string.IsNullOrWhiteSpace(session.CurrentProjectPath))
        {
            var safeLabel = session.SafeModeEnabled ? " [yellow][[safe]][/]" : string.Empty;
            return $"[bold deepskyblue1]unifocl[/][grey]:[/][{CliTheme.Info}]{Markup.Escape(FormatProjectPathForPrompt(session.CurrentProjectPath))}[/]{safeLabel}";
        }

        return "[bold deepskyblue1]unifocl[/]";
    }

    private static void SaveComposerAnchor()
    {
        try
        {
            if (Console.IsOutputRedirected)
            {
                return;
            }

            var (left, top) = Console.GetCursorPosition();
            _anchorLeft = left;
            _anchorTop = top;
            _hasAnchor = true;
        }
        catch
        {
            // Fallback for terminals that do not support cursor position APIs.
            _hasAnchor = false;
            Console.Write("\u001b7");
        }
    }

    private static void ClearComposerFrame()
    {
        try
        {
            if (Console.IsOutputRedirected)
            {
                return;
            }

            if (_hasAnchor)
            {
                Console.SetCursorPosition(_anchorLeft, _anchorTop);
                Console.Write("\u001b[0J");
                return;
            }
        }
        catch
        {
            // Fallback below.
        }

        // DEC restore cursor + clear to end of screen.
        Console.Write("\u001b8\u001b[0J");
    }

    private static void ConstrainComposerLinesToViewport(List<string> lines)
    {
        if (Console.IsOutputRedirected || !_hasAnchor)
        {
            return;
        }

        int maxRows;
        try
        {
            // Keep one free row to avoid terminal auto-scroll on the bottom line.
            maxRows = Math.Max(1, Console.WindowHeight - _anchorTop - 1);
        }
        catch
        {
            return;
        }

        if (maxRows <= 0 || lines.Count == 0)
        {
            return;
        }

        var totalRows = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            totalRows += EstimateVisualRows(lines[i]);
        }

        if (totalRows <= maxRows)
        {
            return;
        }

        // Trim from the top so Unity logs are pushed upward first and input remains visible.
        var trimCount = 0;
        while (trimCount < lines.Count && totalRows > maxRows)
        {
            totalRows -= EstimateVisualRows(lines[trimCount]);
            trimCount++;
        }

        if (trimCount <= 0)
        {
            return;
        }

        lines.RemoveRange(0, Math.Min(trimCount, lines.Count));
    }

    private static int EstimateVisualRows(string markupLine)
    {
        int width;
        try
        {
            width = Math.Max(1, Console.WindowWidth);
        }
        catch
        {
            width = 80;
        }

        var plain = AgenticFormatter.StripMarkup(markupLine);
        if (plain.Length == 0)
        {
            return 1;
        }

        return Math.Max(1, (plain.Length + width - 1) / width);
    }

    private static int CountVisualRows(IReadOnlyList<string> lines)
    {
        var rows = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            rows += EstimateVisualRows(lines[i]);
        }

        return rows;
    }

    private static string GetStreamLogSignature(List<string> streamLog)
    {
        if (streamLog.Count == 0)
        {
            return "0|";
        }

        var start = Math.Max(0, streamLog.Count - StreamLogSignatureTailLines);
        var tail = new List<string>(streamLog.Count - start);
        for (var i = start; i < streamLog.Count; i++)
        {
            tail.Add(streamLog[i]);
        }

        return $"{streamLog.Count}|{string.Join('\n', tail)}";
    }

    private static int ResolveMaxComposerRows()
    {
        if (Console.IsOutputRedirected || !_hasAnchor)
        {
            return int.MaxValue;
        }

        try
        {
            return Math.Max(1, Console.WindowHeight - _anchorTop - 1);
        }
        catch
        {
            return int.MaxValue;
        }
    }

    private static bool TryGetWindowSize(out int width, out int height)
    {
        try
        {
            width = Console.WindowWidth;
            height = Console.WindowHeight;
            return true;
        }
        catch
        {
            width = 0;
            height = 0;
            return false;
        }
    }

    private static string FormatProjectPathForPrompt(string projectPath)
    {
        var normalized = projectPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return projectPath;
        }

        var leaf = Path.GetFileName(normalized);
        var parent = Path.GetFileName(Path.GetDirectoryName(normalized) ?? string.Empty);
        if (string.IsNullOrWhiteSpace(leaf))
        {
            return normalized;
        }

        if (string.IsNullOrWhiteSpace(parent))
        {
            return leaf;
        }

        return $"{parent}/{leaf}";
    }

    private static bool IsLogScrollUpKey(ConsoleKeyInfo key)
    {
        if (key.Key == ConsoleKey.PageUp)
        {
            return true;
        }

        return key.Modifiers.HasFlag(ConsoleModifiers.Control) && key.Key == ConsoleKey.B;
    }

    private static bool IsLogScrollDownKey(ConsoleKeyInfo key)
    {
        if (key.Key == ConsoleKey.PageDown)
        {
            return true;
        }

        return key.Modifiers.HasFlag(ConsoleModifiers.Control) && key.Key == ConsoleKey.F;
    }

    private static bool IsLogScrollLatestKey(ConsoleKeyInfo key)
    {
        if (key.Key == ConsoleKey.End)
        {
            return true;
        }

        return key.Modifiers.HasFlag(ConsoleModifiers.Control) && key.Key == ConsoleKey.E;
    }
}
