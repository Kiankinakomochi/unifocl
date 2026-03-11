using Spectre.Console;
using System.Text;

internal static class CliComposerService
{
    private static bool _hasAnchor;
    private static int _anchorLeft;
    private static int _anchorTop;
    private static bool _bootLogoCollapsed;

    public static string? ReadInput(
        List<CommandSpec> commands,
        List<CommandSpec> projectCommands,
        List<CommandSpec> inspectorCommands,
        List<string> streamLog,
        CliSessionState session)
    {
        if (Console.IsInputRedirected)
        {
            CliTheme.Markup($"{BuildPromptLabelMarkup(session)} [grey]>[/] ");
            return Console.ReadLine();
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
        SaveComposerAnchor();
        RenderComposerFrame(
            input.ToString(),
            commands,
            projectCommands,
            inspectorCommands,
            session,
            selectedIntellisenseCandidateIndex,
            intellisenseDismissed);

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

            var key = Console.ReadKey(intercept: true);

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
                        break;
                    }

                    Console.WriteLine();
                    return input.ToString();
                case ConsoleKey.Backspace:
                    if (input.Length > 0)
                    {
                        input.Remove(input.Length - 1, 1);
                    }

                    intellisenseDismissed = false;
                    break;
                case ConsoleKey.Escape:
                    if (!intellisenseDismissed && allCandidates.Count > 0)
                    {
                        intellisenseDismissed = true;
                    }
                    else
                    {
                        input.Clear();
                        intellisenseDismissed = false;
                    }

                    selectedIntellisenseCandidateIndex = 0;
                    break;
                case ConsoleKey.UpArrow:
                    if (intellisenseDismissed && allCandidates.Count > 0)
                    {
                        intellisenseDismissed = false;
                        candidates = allCandidates;
                    }

                    if (candidates.Count > 0)
                    {
                        selectedIntellisenseCandidateIndex = selectedIntellisenseCandidateIndex <= 0
                            ? candidates.Count - 1
                            : selectedIntellisenseCandidateIndex - 1;
                    }
                    break;
                case ConsoleKey.DownArrow:
                    if (intellisenseDismissed && allCandidates.Count > 0)
                    {
                        intellisenseDismissed = false;
                        candidates = allCandidates;
                    }

                    if (candidates.Count > 0)
                    {
                        selectedIntellisenseCandidateIndex = selectedIntellisenseCandidateIndex >= candidates.Count - 1
                            ? 0
                            : selectedIntellisenseCandidateIndex + 1;
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
                    }
                    break;
            }

            if (composerResetRequested)
            {
                SaveComposerAnchor();
                RenderComposerFrame(
                    input.ToString(),
                    commands,
                    projectCommands,
                    inspectorCommands,
                    session,
                    selectedIntellisenseCandidateIndex,
                    intellisenseDismissed);
                continue;
            }

            ClearComposerFrame();
            RenderComposerFrame(
                input.ToString(),
                commands,
                projectCommands,
                inspectorCommands,
                session,
                selectedIntellisenseCandidateIndex,
                intellisenseDismissed);
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

    private static void RenderComposerFrame(
        string input,
        List<CommandSpec> commands,
        List<CommandSpec> projectCommands,
        List<CommandSpec> inspectorCommands,
        CliSessionState session,
        int selectedFuzzyCandidateIndex,
        bool suppressIntellisense)
    {
        var lines = new List<string>();
        lines.AddRange(BuildComposerUnityLogPane(session));
        lines.AddRange(
        [
            $"{BuildPromptLabelMarkup(session)} [grey]>[/] [bold white]{Markup.Escape(input)}[/]"
        ]);

        if (suppressIntellisense)
        {
            lines.Add("[dim]intellisense dismissed (Esc). Type or use ↑/↓ to reopen suggestions.[/]");
        }
        else if (input.StartsWith('/'))
        {
            lines.Add(string.Empty);
            lines.AddRange(CliComposerIntellisenseService.GetSuggestionLines(input, commands, session, selectedFuzzyCandidateIndex));
        }
        else if (CliComposerIntellisenseService.TryGetProjectMkTypeIntellisenseLines(input, session, selectedFuzzyCandidateIndex, out var mkTypeLines))
        {
            lines.Add(string.Empty);
            lines.AddRange(mkTypeLines);
        }
        else if (CliComposerIntellisenseService.TryGetInspectorComponentIntellisenseLines(input, session, selectedFuzzyCandidateIndex, out var componentLines))
        {
            lines.Add(string.Empty);
            lines.AddRange(componentLines);
        }
        else if (CliFuzzyService.TryGetFuzzyQueryIntellisenseLines(input, session, selectedFuzzyCandidateIndex, out var fuzzyLines))
        {
            lines.Add(string.Empty);
            lines.AddRange(fuzzyLines);
        }
        else if (!string.IsNullOrWhiteSpace(input))
        {
            var contextualCommands = session.Inspector is not null
                ? inspectorCommands
                : projectCommands;
            lines.Add(string.Empty);
            lines.AddRange(CliComposerIntellisenseService.GetSuggestionLines(input, contextualCommands, session, selectedFuzzyCandidateIndex));
        }
        else if (session.Inspector is not null)
        {
            // Keep inspector idle composer minimal; avoid verbose default helper line.
            lines.Add(string.Empty);
        }
        else if (session.ContextMode == CliContextMode.Project && !string.IsNullOrWhiteSpace(session.CurrentProjectPath))
        {
            lines.Add("[dim]Project mode: list, enter <idx>, up, f [--type <type>|t:<type>] <query>, mk <type> [count] [--name], load <idx|name>, rename <idx> <new>, remove <idx>, move <...>, F7 focus nav[/]");
        }
        else
        {
            lines.Add("[dim]Type / to open command palette. Use your mouse wheel to scroll log history.[/]");
        }

        ConstrainComposerLinesToViewport(lines);

        foreach (var line in lines)
        {
            CliTheme.MarkupLine(line);
        }
    }

    private static IEnumerable<string> BuildComposerUnityLogPane(CliSessionState session)
    {
        if (session.UnityLogPane.Count == 0)
        {
            return [];
        }

        const int maxLogRows = 6;
        const int fallbackPaneWidth = 78;
        var paneWidth = Math.Max(30, (Console.IsOutputRedirected ? fallbackPaneWidth : Console.WindowWidth) - 2);
        var border = new string('─', paneWidth);
        var lines = new List<string>
        {
            $"┌{border}┐",
            $"│{FitForPane(" UNITY LOG ", paneWidth)}│"
        };

        var visible = session.UnityLogPane.Skip(Math.Max(0, session.UnityLogPane.Count - maxLogRows)).ToList();

        for (var i = 0; i < maxLogRows; i++)
        {
            var content = i < visible.Count ? visible[i] : string.Empty;
            lines.Add($"│{FitForPane(content, paneWidth)}│");
        }

        lines.Add($"└{border}┘");
        return lines;
    }

    private static string FitForPane(string text, int width)
    {
        if (string.IsNullOrEmpty(text))
        {
            return new string(' ', width);
        }

        var normalized = text.Replace(Environment.NewLine, " ").Replace("\n", " ").Replace("\r", " ");
        if (normalized.Length > width)
        {
            normalized = normalized[..Math.Max(0, width - 1)] + "…";
        }

        if (normalized.Length < width)
        {
            normalized = normalized.PadRight(width);
        }

        return Markup.Escape(normalized);
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
            return $"[bold deepskyblue1]unifocl[/][grey]:[/][{CliTheme.Info}]{Markup.Escape(session.CurrentProjectPath)}[/]{safeLabel}";
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

        var keptLines = new List<string>(Math.Min(lines.Count, maxRows));
        var usedRows = 0;
        var hiddenLines = 0;

        for (var i = 0; i < lines.Count; i++)
        {
            var candidate = lines[i];
            var rows = EstimateVisualRows(candidate);
            if (usedRows + rows > maxRows)
            {
                hiddenLines = lines.Count - i;
                break;
            }

            keptLines.Add(candidate);
            usedRows += rows;
        }

        if (hiddenLines <= 0)
        {
            return;
        }

        var hint = "[dim]… more hidden (resize terminal)[/]";
        var hintRows = EstimateVisualRows(hint);
        while (keptLines.Count > 0 && usedRows + hintRows > maxRows)
        {
            var removed = keptLines[^1];
            keptLines.RemoveAt(keptLines.Count - 1);
            usedRows -= EstimateVisualRows(removed);
            hiddenLines++;
        }

        if (usedRows + hintRows <= maxRows)
        {
            keptLines.Add(hint);
        }

        lines.Clear();
        lines.AddRange(keptLines);
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
}
