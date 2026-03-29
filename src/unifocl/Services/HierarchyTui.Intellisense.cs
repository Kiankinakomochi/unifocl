using System.Text;
using Spectre.Console;

internal sealed partial class HierarchyTui
{
    private static string ReadPromptInputWithIntellisenseFromFirstKey(
        string cwdPath,
        ConsoleKeyInfo firstKey,
        HierarchySnapshotDto snapshot,
        int cwdId)
    {
        var input = new StringBuilder();
        if (firstKey.Key == ConsoleKey.Enter)
        {
            Console.WriteLine();
            return string.Empty;
        }

        if (firstKey.Key == ConsoleKey.Escape)
        {
            Console.WriteLine();
            return string.Empty;
        }

        if (!TryAppendInputCharacter(firstKey, input))
        {
            Console.WriteLine();
            return string.Empty;
        }

        var selectedIndex = 0;
        var dismissed = false;
        var renderedLines = RenderPromptIntellisense(cwdPath, input.ToString(), selectedIndex, dismissed, snapshot, cwdId);
        var (knownViewportWidth, knownViewportHeight) = TuiConsoleViewport.GetWindowSizeOrDefault();

        while (true)
        {
            var allCandidates = GetHierarchyIntellisenseCandidates(input.ToString(), snapshot, cwdId);
            var candidates = dismissed ? [] : allCandidates;
            if (candidates.Count == 0)
            {
                selectedIndex = 0;
            }
            else
            {
                selectedIndex = Math.Clamp(selectedIndex, 0, candidates.Count - 1);
            }

            if (!TuiConsoleViewport.WaitForKeyOrResize(ref knownViewportWidth, ref knownViewportHeight, out var key))
            {
                ClearPromptFrame(renderedLines);
                renderedLines = RenderPromptIntellisense(cwdPath, input.ToString(), selectedIndex, dismissed, snapshot, cwdId);
                continue;
            }
            if (key.Key == ConsoleKey.Enter)
            {
                if (IsHierarchyCatalogCommandInput(input.ToString()))
                {
                    ClearPromptFrame(renderedLines);
                    Console.WriteLine($"UnityCLI:{cwdPath} > {input}");
                    return input.ToString().Trim();
                }

                if (candidates.Count > 0
                    && selectedIndex >= 0
                    && selectedIndex < candidates.Count
                    && !string.IsNullOrWhiteSpace(candidates[selectedIndex].CommitCommand))
                {
                    var currentInput = input.ToString();
                    input.Clear();
                    input.Append(MergeAcceptedSuggestion(currentInput, candidates[selectedIndex].CommitCommand));
                    // Accepting a suggestion exits IntelliSense so next Enter executes.
                    dismissed = true;
                }
                else
                {
                    ClearPromptFrame(renderedLines);
                    Console.WriteLine($"UnityCLI:{cwdPath} > {input}");
                    return input.ToString().Trim();
                }
            }
            else if (key.Key == ConsoleKey.Backspace)
            {
                if (input.Length > 0)
                {
                    input.Length--;
                }

                dismissed = false;
            }
            else if (key.Key == ConsoleKey.Escape)
            {
                if (!dismissed && allCandidates.Count > 0)
                {
                    dismissed = true;
                }
                else
                {
                    input.Clear();
                    dismissed = false;
                }

                selectedIndex = 0;
            }
            else if (key.Key == ConsoleKey.UpArrow)
            {
                if (dismissed && allCandidates.Count > 0)
                {
                    dismissed = false;
                    candidates = allCandidates;
                }

                if (candidates.Count > 0)
                {
                    selectedIndex = selectedIndex <= 0 ? candidates.Count - 1 : selectedIndex - 1;
                }
            }
            else if (key.Key == ConsoleKey.DownArrow)
            {
                if (dismissed && allCandidates.Count > 0)
                {
                    dismissed = false;
                    candidates = allCandidates;
                }

                if (candidates.Count > 0)
                {
                    selectedIndex = selectedIndex >= candidates.Count - 1 ? 0 : selectedIndex + 1;
                }
            }
            else if (TryAppendInputCharacter(key, input))
            {
                dismissed = false;
            }

            ClearPromptFrame(renderedLines);
            renderedLines = RenderPromptIntellisense(cwdPath, input.ToString(), selectedIndex, dismissed, snapshot, cwdId);
        }
    }

    private static bool TryAppendInputCharacter(ConsoleKeyInfo key, StringBuilder input)
    {
        if (char.IsControl(key.KeyChar))
        {
            return false;
        }

        input.Append(key.KeyChar);
        return true;
    }

    private static int RenderPromptIntellisense(
        string cwdPath,
        string input,
        int selectedSuggestionIndex,
        bool suppressIntellisense,
        HierarchySnapshotDto snapshot,
        int cwdId)
    {
        var lines = new List<string>
        {
            $"UnityCLI:{Markup.Escape(cwdPath)} > [bold white]{Markup.Escape(input)}[/]"
        };

        if (suppressIntellisense)
        {
            lines.Add("[dim]intellisense dismissed (Esc). Type or use ↑/↓ to reopen suggestions.[/]");
        }
        else
        {
            var candidates = GetHierarchyIntellisenseCandidates(input, snapshot, cwdId);
            var isFuzzyPreview = TryParseFuzzyQuery(input, out _);
            lines.Add(isFuzzyPreview
                ? "[grey]fuzzy[/]: hierarchy path suggestions [dim](up/down to browse, Enter to execute current input)[/]"
                : "[grey]intellisense[/]: hierarchy commands [dim](up/down + enter to insert)[/]");
            if (candidates.Count == 0)
            {
                lines.Add(isFuzzyPreview
                    ? "[dim]no fuzzy matches[/]"
                    : (string.IsNullOrWhiteSpace(input)
                        ? "[dim]no commands available[/]"
                        : $"[dim]no matches for {Markup.Escape(input)}[/]"));
            }
            else
            {
                var selected = Math.Clamp(selectedSuggestionIndex, 0, candidates.Count - 1);
                const int maxVisible = 12;
                var visibleCount = Math.Min(maxVisible, candidates.Count);
                var windowStart = Math.Clamp(selected - (visibleCount / 2), 0, Math.Max(0, candidates.Count - visibleCount));
                var windowEnd = windowStart + visibleCount;
                for (var i = windowStart; i < windowEnd; i++)
                {
                    var candidate = candidates[i];
                    var isSelected = i == selected;
                    var prefix = isSelected
                        ? $"[{CliTheme.CursorForeground} on {CliTheme.CursorBackground}]>[/]"
                        : "[grey] [/]";
                    var label = isFuzzyPreview
                        ? FormatHierarchyFuzzyCandidateLabel(candidate.Label, isSelected)
                        : Markup.Escape(candidate.Label);
                    lines.Add(isSelected
                        ? (isFuzzyPreview
                            ? $"{prefix} {label}"
                            : $"{prefix} [{CliTheme.CursorForeground} on {CliTheme.CursorBackground}]{label}[/]")
                        : (isFuzzyPreview
                            ? $"{prefix} {label}"
                            : $"{prefix} [grey]{label}[/]"));
                }

                if (candidates.Count > visibleCount)
                {
                    lines.Add($"[dim]showing {windowStart + 1}-{windowEnd}/{candidates.Count}[/]");
                }
            }
        }

        foreach (var line in lines)
        {
            CliTheme.MarkupLine(line);
        }

        return lines.Count;
    }

    private static bool IsHierarchyCatalogCommandInput(string input)
    {
        var trimmed = input.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return false;
        }

        var head = trimmed.Split(' ', '\t', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(head))
        {
            return false;
        }

        return HierarchyCommands.Any(command => command.Trigger.Equals(head, StringComparison.OrdinalIgnoreCase))
            || MkTypeCommands.Any(command => command.Trigger.Equals(head, StringComparison.OrdinalIgnoreCase));
    }

    private static string MergeAcceptedSuggestion(string currentInput, string acceptedCommand)
    {
        if (string.IsNullOrWhiteSpace(acceptedCommand))
        {
            return currentInput;
        }

        var trimmedCurrent = currentInput.TrimStart();
        if (string.IsNullOrWhiteSpace(trimmedCurrent))
        {
            return acceptedCommand;
        }

        var tokenCount = CountTokens(acceptedCommand);
        if (tokenCount <= 0)
        {
            return acceptedCommand;
        }

        var remainder = SliceAfterTokenCount(trimmedCurrent, tokenCount);
        if (string.IsNullOrEmpty(remainder))
        {
            return acceptedCommand;
        }

        if (acceptedCommand.EndsWith(' '))
        {
            return acceptedCommand + remainder.TrimStart();
        }

        if (char.IsWhiteSpace(remainder[0]))
        {
            return acceptedCommand + remainder;
        }

        return $"{acceptedCommand} {remainder}";
    }

    private static int CountTokens(string input)
    {
        var count = 0;
        var inQuotes = false;
        var inToken = false;
        foreach (var ch in input)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (!inQuotes && char.IsWhiteSpace(ch))
            {
                if (inToken)
                {
                    count++;
                    inToken = false;
                }

                continue;
            }

            inToken = true;
        }

        if (inToken)
        {
            count++;
        }

        return count;
    }

    private static string SliceAfterTokenCount(string input, int tokenCount)
    {
        if (tokenCount <= 0)
        {
            return input;
        }

        var i = 0;
        var consumed = 0;
        var inQuotes = false;
        var inToken = false;
        while (i < input.Length)
        {
            var ch = input[i];
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                if (!inToken)
                {
                    inToken = true;
                }

                i++;
                continue;
            }

            if (!inQuotes && char.IsWhiteSpace(ch))
            {
                if (inToken)
                {
                    consumed++;
                    inToken = false;
                    if (consumed == tokenCount)
                    {
                        return input[i..];
                    }
                }

                i++;
                continue;
            }

            inToken = true;
            i++;
        }

        if (inToken)
        {
            consumed++;
        }

        return consumed >= tokenCount ? string.Empty : input;
    }

    private static void ClearPromptFrame(int renderedLines)
    {
        for (var i = 0; i < renderedLines; i++)
        {
            Console.Write("\u001b[1A");
            Console.Write("\r\u001b[2K");
        }
    }

    private static List<CommandSpec> GetHierarchySuggestionMatches(string query)
    {
        var normalized = query.Trim().ToLowerInvariant();
        IEnumerable<CommandSpec> source = HierarchyCommands
            .Concat(MkTypeCommands)
            .Where(command => !command.Description.StartsWith("Alias for", StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(normalized))
        {
            source = source.Where(command =>
                command.Signature.Contains(normalized, StringComparison.OrdinalIgnoreCase)
                || command.Description.Contains(normalized, StringComparison.OrdinalIgnoreCase)
                || command.Trigger.StartsWith(normalized, StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith(command.Trigger, StringComparison.OrdinalIgnoreCase));
        }

        return source.ToList();
    }

    private static List<(string Label, string CommitCommand)> GetHierarchyIntellisenseCandidates(string query, HierarchySnapshotDto snapshot, int cwdId)
    {
        if (TryParseFuzzyQuery(query, out var fuzzyQuery))
        {
            return GetHierarchyFuzzyPreviewCandidates(snapshot, cwdId, fuzzyQuery);
        }

        return GetHierarchySuggestionMatches(query)
            .Select(match => (match.Signature, string.IsNullOrWhiteSpace(match.Trigger) ? match.Signature : match.Trigger))
            .ToList();
    }

    private static bool TryParseFuzzyQuery(string input, out string query)
    {
        query = string.Empty;
        var trimmed = input.TrimStart();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return false;
        }

        if (trimmed.Equals("f", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("ff", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (trimmed.StartsWith("f ", StringComparison.OrdinalIgnoreCase))
        {
            query = trimmed[2..].Trim();
            return true;
        }

        if (trimmed.StartsWith("ff ", StringComparison.OrdinalIgnoreCase))
        {
            query = trimmed[3..].Trim();
            return true;
        }

        return false;
    }

    private static List<(string Label, string CommitCommand)> GetHierarchyFuzzyPreviewCandidates(
        HierarchySnapshotDto snapshot,
        int cwdId,
        string query)
    {
        var cwdNode = FindNode(snapshot.Root, cwdId) ?? snapshot.Root;
        var seedPath = BuildPath(snapshot.Root, cwdNode.Id);
        var paths = new List<string>();
        CollectHierarchyPaths(cwdNode, seedPath, paths);

        IEnumerable<(string Path, double Score)> ranked;
        if (string.IsNullOrWhiteSpace(query))
        {
            ranked = paths.Select(path => (path, 1d));
        }
        else
        {
            var scored = new List<(string Path, double Score)>();
            foreach (var path in paths)
            {
                if (path.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    scored.Add((path, 0.9d));
                    continue;
                }

                if (FuzzyMatcher.TryScore(query, path, out var score))
                {
                    scored.Add((path, score));
                }
            }

            ranked = scored;
        }

        return ranked
            .OrderByDescending(entry => entry.Score)
            .ThenBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
            .Take(30)
            .Select(entry => (entry.Path, $"f {entry.Path}"))
            .ToList();
    }

    private static void CollectHierarchyPaths(HierarchyNodeDto node, string currentPath, List<string> output)
    {
        foreach (var child in node.Children)
        {
            var childPath = $"{currentPath}/{child.Name}";
            output.Add(childPath);
            CollectHierarchyPaths(child, childPath, output);
        }
    }

    private static string FormatHierarchyFuzzyCandidateLabel(string path, bool selectedLine)
    {
        var normalizedPath = path.Replace('\\', '/');
        var separatorIndex = normalizedPath.LastIndexOf('/');
        if (separatorIndex < 0 || separatorIndex >= normalizedPath.Length - 1)
        {
            var escaped = Markup.Escape(path);
            return selectedLine
                ? $"[bold white]{escaped}[/]"
                : $"[white]{escaped}[/]";
        }

        var context = Markup.Escape(normalizedPath[..(separatorIndex + 1)]);
        var leaf = Markup.Escape(normalizedPath[(separatorIndex + 1)..]);
        return selectedLine
            ? $"[grey58]{context}[/][bold white]{leaf}[/]"
            : $"[grey58]{context}[/][bold deepskyblue1]{leaf}[/]";
    }
}
