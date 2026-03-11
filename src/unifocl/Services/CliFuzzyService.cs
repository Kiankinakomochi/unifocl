using Spectre.Console;

internal static class CliFuzzyService
{
    public static bool TryGetFuzzyQueryIntellisenseLines(
        string input,
        CliSessionState session,
        int selectedFuzzyCandidateIndex,
        out List<string> lines)
    {
        lines = [];
        if (!TryGetFuzzyComposerCandidates(input, session, out var candidates, out var modeLabel, out var emptyLabel))
        {
            return false;
        }

        lines.Add($"[grey]fuzzy[/]: {Markup.Escape(modeLabel)} [dim](up/down + enter to insert)[/]");
        if (candidates.Count == 0)
        {
            lines.Add($"[dim]{Markup.Escape(emptyLabel)}[/]");
            return true;
        }

        var selected = Math.Clamp(selectedFuzzyCandidateIndex, 0, candidates.Count - 1);
        var inspectorMode = session.Inspector is not null;
        for (var i = 0; i < candidates.Count && i < 10; i++)
        {
            var candidate = candidates[i];
            var selectedLine = i == selected;
            var prefix = selectedLine
                ? $"[{CliTheme.CursorForeground} on {CliTheme.CursorBackground}]>[/]"
                : "[grey] [/]";
            var formattedPath = inspectorMode
                ? FormatInspectorFuzzyCandidateLabel(candidate.Path, selectedLine)
                : FormatProjectFuzzyCandidateLabel(candidate.Path, selectedLine);
            lines.Add(selectedLine
                ? $"{prefix} [{CliTheme.CursorForeground} on {CliTheme.CursorBackground}]{i}[/] {formattedPath}"
                : $"{prefix} [deepskyblue1]{i}[/] {formattedPath}");
        }

        return true;
    }

    public static bool TryGetFuzzyComposerCandidates(
        string input,
        CliSessionState session,
        out List<(string Path, string? CommitCommand)> candidates,
        out string modeLabel,
        out string emptyLabel)
    {
        candidates = [];
        modeLabel = string.Empty;
        emptyLabel = string.Empty;
        if (!TryParseFuzzyQueryInput(input, out var query))
        {
            return false;
        }

        if (session.Inspector is not null)
        {
            modeLabel = "inspector query";
            candidates = GetInspectorFuzzyCandidates(session.Inspector, query);
            emptyLabel = "no inspector matches";
            return true;
        }

        if (session.Mode != CliMode.Project || string.IsNullOrWhiteSpace(session.CurrentProjectPath))
        {
            modeLabel = "available in project/inspector contexts";
            emptyLabel = "switch context to use fuzzy selection";
            return true;
        }

        modeLabel = "project query";
        candidates = GetProjectFuzzyCandidates(session.ProjectView, query);
        emptyLabel = session.ProjectView.AssetPathByInstanceId.Count == 0
            ? "asset index is cold; press Enter once to sync"
            : "no project matches";
        return true;
    }

    private static bool TryParseFuzzyQueryInput(string input, out string query)
    {
        query = string.Empty;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var trimmed = input.TrimStart();
        var firstSpace = trimmed.IndexOf(' ');
        var command = firstSpace < 0 ? trimmed : trimmed[..firstSpace];
        if (!command.Equals("f", StringComparison.OrdinalIgnoreCase)
            && !command.Equals("ff", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        query = firstSpace < 0 ? string.Empty : trimmed[(firstSpace + 1)..].Trim();
        return true;
    }

    private static string FormatInspectorFuzzyCandidateLabel(string path, bool selectedLine)
    {
        var lastDot = path.LastIndexOf('.');
        var lastSlash = path.LastIndexOf('/');
        var separatorIndex = Math.Max(lastDot, lastSlash);
        if (separatorIndex < 0 || separatorIndex >= path.Length - 1)
        {
            var escaped = Markup.Escape(path);
            return selectedLine
                ? $"[{CliTheme.CursorForeground} on {CliTheme.CursorBackground}]{escaped}[/]"
                : $"[white]{escaped}[/]";
        }

        var context = Markup.Escape(path[..(separatorIndex + 1)]);
        var leaf = Markup.Escape(path[(separatorIndex + 1)..]);
        return selectedLine
            ? $"[grey58]{context}[/][bold {CliTheme.CursorForeground} on {CliTheme.CursorBackground}]{leaf}[/]"
            : $"[grey58]{context}[/][bold white]{leaf}[/]";
    }

    private static string FormatProjectFuzzyCandidateLabel(string path, bool selectedLine)
    {
        var normalizedPath = path.Replace('\\', '/');
        var separatorIndex = normalizedPath.LastIndexOf('/');
        if (separatorIndex < 0 || separatorIndex >= normalizedPath.Length - 1)
        {
            var escaped = Markup.Escape(path);
            return selectedLine
                ? $"[bold {CliTheme.CursorForeground} on {CliTheme.CursorBackground}]{escaped}[/]"
                : $"[bold white]{escaped}[/]";
        }

        var context = Markup.Escape(normalizedPath[..(separatorIndex + 1)]);
        var leaf = Markup.Escape(normalizedPath[(separatorIndex + 1)..]);
        return selectedLine
            ? $"[grey58]{context}[/][bold {CliTheme.CursorForeground} on {CliTheme.CursorBackground}]{leaf}[/]"
            : $"[grey58]{context}[/][bold white]{leaf}[/]";
    }

    private static List<(string Path, string? CommitCommand)> GetProjectFuzzyCandidates(ProjectViewState state, string query)
    {
        var (typeFilter, term) = ProjectMkCatalog.ParseFuzzyQuery(query);
        var entries = state.AssetPathByInstanceId.Count > 0
            ? state.AssetPathByInstanceId.Values
            : state.VisibleEntries.Select(entry => entry.RelativePath);
        var matches = new List<ProjectFuzzyMatch>();
        foreach (var path in entries)
        {
            if (!ProjectMkCatalog.PassesFuzzyTypeFilter(path, typeFilter))
            {
                continue;
            }

            var score = 1d;
            var matched = string.IsNullOrWhiteSpace(term) || FuzzyMatcher.TryScore(term, path, out score);
            if (!matched)
            {
                continue;
            }

            matches.Add(new ProjectFuzzyMatch(0, 0, path, score));
        }

        return matches
            .OrderByDescending(match => match.Score)
            .ThenBy(match => match.Path, StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .Select(match => (match.Path, (string?)$"load \"{match.Path}\""))
            .ToList();
    }

    private static List<(string Path, string? CommitCommand)> GetInspectorFuzzyCandidates(InspectorContext context, string query)
    {
        var matches = new List<InspectorSearchResultDto>();
        if (context.Depth == InspectorDepth.ComponentList)
        {
            foreach (var component in context.Components)
            {
                if (FuzzyMatcher.TryScore(query, component.Name, out var score))
                {
                    matches.Add(new InspectorSearchResultDto("component", component.Index, component.Name, component.Name, score));
                }
            }
        }
        else
        {
            foreach (var field in context.Fields)
            {
                var path = $"{context.SelectedComponentName}.{field.Name}";
                if (FuzzyMatcher.TryScore(query, path, out var score) || FuzzyMatcher.TryScore(query, field.Name, out score))
                {
                    matches.Add(new InspectorSearchResultDto("field", context.SelectedComponentIndex, field.Name, path, score));
                }
            }
        }

        return matches
            .OrderByDescending(match => match.Score)
            .ThenBy(match => match.Path, StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .Select(match =>
            {
                var commit = match.Scope.Equals("component", StringComparison.OrdinalIgnoreCase) && match.ComponentIndex is int componentIndex
                    ? $"inspect {componentIndex}"
                    : null;
                return (match.Path, commit);
            })
            .ToList();
    }
}
