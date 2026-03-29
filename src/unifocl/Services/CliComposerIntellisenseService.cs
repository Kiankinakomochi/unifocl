using Spectre.Console;
using System.Text;

internal static class CliComposerIntellisenseService
{
    public static bool TryGetComposerIntellisenseCandidates(
        string input,
        List<CommandSpec> commands,
        List<CommandSpec> projectCommands,
        List<CommandSpec> inspectorCommands,
        CliSessionState session,
        out List<(string Label, string? CommitCommand)> candidates)
    {
        candidates = [];

        if (CliFuzzyService.TryGetFuzzyComposerCandidates(input, session, out var fuzzyCandidates, out _, out _))
        {
            candidates = fuzzyCandidates
                .Select(candidate => (candidate.Path, candidate.CommitCommand))
                .ToList();
            return true;
        }

        if (CliUpmIntellisenseService.TryGetUpmComposerCandidates(input, session, out var upmCandidates))
        {
            candidates = upmCandidates;
            return true;
        }

        if (TryGetProjectMkTypeComposerCandidates(input, session, out var mkTypeCandidates))
        {
            candidates = mkTypeCandidates;
            return true;
        }

        if (TryGetHierarchyMkTypeComposerCandidates(input, session, out var hierarchyMkCandidates))
        {
            candidates = hierarchyMkCandidates;
            return true;
        }

        if (TryGetInspectorComponentComposerCandidates(input, session, out var componentCandidates))
        {
            candidates = componentCandidates;
            return true;
        }

        if (input.StartsWith('/'))
        {
            candidates = GetSuggestionMatches(input, commands, session)
                .Select(match => (match.Signature, (string?)match.Trigger))
                .ToList();
            return true;
        }

        if (!string.IsNullOrWhiteSpace(input))
        {
            var contextualCommands = session.Inspector is not null
                ? inspectorCommands
                : projectCommands;
            candidates = GetSuggestionMatches(input, contextualCommands, session)
                .Select(match => (match.Signature, (string?)match.Trigger))
                .ToList();
            return true;
        }

        return false;
    }

    public static bool TryGetProjectMkTypeIntellisenseLines(
        string input,
        CliSessionState session,
        int selectedSuggestionIndex,
        out List<string> lines)
    {
        lines = [];
        if (!TryGetProjectMkTypeComposerCandidates(input, session, out var candidates))
        {
            return false;
        }

        lines.Add("[grey]mk[/]: type suggestions [dim](fuzzy, up/down + enter to insert)[/]");
        if (candidates.Count == 0)
        {
            lines.Add("[dim]no mk type matches[/]");
            return true;
        }

        var selected = Math.Clamp(selectedSuggestionIndex, 0, candidates.Count - 1);
        for (var i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            var selectedLine = i == selected;
            var prefix = selectedLine
                ? $"[{CliTheme.CursorForeground} on {CliTheme.CursorBackground}]>[/]"
                : "[grey] [/]";
            var escaped = Markup.Escape(candidate.Label);
            lines.Add(selectedLine
                ? $"{prefix} [{CliTheme.CursorForeground} on {CliTheme.CursorBackground}]{escaped}[/]"
                : $"{prefix} [grey]{escaped}[/]");
        }

        if (TryBuildMkUsageHint(input, candidates, selected, out var usageHint))
        {
            lines.Add(string.Empty);
            lines.Add($"[dim]{Markup.Escape(usageHint)}[/]");
        }

        return true;
    }

    public static bool TryGetInspectorComponentIntellisenseLines(
        string input,
        CliSessionState session,
        int selectedSuggestionIndex,
        out List<string> lines)
    {
        lines = [];
        if (!TryGetInspectorComponentComposerCandidates(input, session, out var candidates))
        {
            return false;
        }

        lines.Add("[grey]component[/]: add suggestions [dim](fuzzy, up/down + enter to insert)[/]");
        if (candidates.Count == 0)
        {
            lines.Add("[dim]no component matches[/]");
            return true;
        }

        const int maxSuggestions = 10;
        var visibleCount = Math.Min(maxSuggestions, candidates.Count);
        var selected = Math.Clamp(selectedSuggestionIndex, 0, visibleCount - 1);
        for (var i = 0; i < visibleCount; i++)
        {
            var candidate = candidates[i];
            var selectedLine = i == selected;
            var prefix = selectedLine
                ? $"[{CliTheme.CursorForeground} on {CliTheme.CursorBackground}]>[/]"
                : "[grey] [/]";
            var escaped = Markup.Escape(candidate.Label);
            lines.Add(selectedLine
                ? $"{prefix} [{CliTheme.CursorForeground} on {CliTheme.CursorBackground}]{escaped}[/]"
                : $"{prefix} [grey]{escaped}[/]");
        }

        if (candidates.Count > visibleCount)
        {
            lines.Add($"[dim]showing first {visibleCount}/{candidates.Count} matches[/]");
        }

        lines.Add(string.Empty);
        lines.Add("[dim]Usage: component add <Component Name>[/]");
        return true;
    }

    public static bool IsCatalogCommandInput(
        string input,
        List<CommandSpec> commands,
        List<CommandSpec> projectCommands,
        List<CommandSpec> inspectorCommands,
        CliSessionState session)
    {
        var trimmed = input.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return false;
        }

        var tokens = CliCommandParsingService.TokenizeComposerInput(trimmed);
        if (tokens.Count == 0)
        {
            return false;
        }

        var head = tokens[0];
        IEnumerable<CommandSpec> catalog = head.StartsWith('/')
            ? commands
            : (session.Inspector is not null ? inspectorCommands : projectCommands);

        return catalog.Any(command => command.Trigger.Equals(head, StringComparison.OrdinalIgnoreCase));
    }

    public static IEnumerable<string> GetSuggestionLines(
        string query,
        List<CommandSpec> commands,
        CliSessionState session,
        int selectedSuggestionIndex)
    {
        var matches = GetSuggestionMatches(query, commands, session);
        if (matches.Count == 0)
        {
            return
            [
                "[grey]intellisense[/]: command suggestions [dim](up/down + enter to insert)[/]",
                $"[dim]no matches for {Markup.Escape(query)}[/]"
            ];
        }

        var selected = Math.Clamp(selectedSuggestionIndex, 0, matches.Count - 1);
        var lines = new List<string>
        {
            "[grey]intellisense[/]: command suggestions [dim](up/down + enter to insert)[/]"
        };
        for (var i = 0; i < matches.Count; i++)
        {
            var match = matches[i];
            var selectedLine = i == selected;
            var prefix = selectedLine
                ? $"[{CliTheme.CursorForeground} on {CliTheme.CursorBackground}]>[/]"
                : "[grey] [/]";
            var escapedSignature = Markup.Escape(match.Signature);
            var escapedDescription = Markup.Escape(match.Description);
            lines.Add(selectedLine
                ? $"{prefix} [{CliTheme.CursorForeground} on {CliTheme.CursorBackground}]{escapedSignature}[/] [dim]- {escapedDescription}[/]"
                : $"{prefix} [grey]{escapedSignature}[/] [dim]- {escapedDescription}[/]");
        }

        return lines;
    }

    private static bool TryGetProjectMkTypeComposerCandidates(
        string input,
        CliSessionState session,
        out List<(string Label, string? CommitCommand)> candidates)
    {
        candidates = [];
        if (session.Inspector is not null
            || session.Mode != CliMode.Project
            || string.IsNullOrWhiteSpace(session.CurrentProjectPath))
        {
            return false;
        }

        var trimmed = input.TrimStart();
        if (trimmed.StartsWith('/'))
        {
            return false;
        }

        var tokens = CliCommandParsingService.TokenizeComposerInput(trimmed);
        if (tokens.Count == 0)
        {
            return false;
        }

        var isMk = tokens[0].Equals("mk", StringComparison.OrdinalIgnoreCase);
        var isMake = tokens[0].Equals("make", StringComparison.OrdinalIgnoreCase);
        if (!isMk && !isMake)
        {
            return false;
        }

        var mkTypeQuery = ResolveProjectMkTypeQuery(tokens, isMake);
        var knownTypes = session.ProjectView.CachedMkTypes.Count > 0
            ? (IReadOnlyList<string>)session.ProjectView.CachedMkTypes
            : ProjectMkCatalog.KnownTypes;
        var matches = new List<(string Type, double Score)>();
        foreach (var type in knownTypes)
        {
            var display = FormatMkTypeDisplay(type);
            if (string.IsNullOrWhiteSpace(mkTypeQuery))
            {
                matches.Add((type, 1d));
                continue;
            }

            if (display.Contains(mkTypeQuery, StringComparison.OrdinalIgnoreCase)
                || type.Contains(mkTypeQuery, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add((type, 0.9d));
                continue;
            }

            if (FuzzyMatcher.TryScore(mkTypeQuery, display, out var displayScore)
                || FuzzyMatcher.TryScore(mkTypeQuery, type, out displayScore))
            {
                matches.Add((type, displayScore));
            }
        }

        var ranked = matches
            .OrderByDescending(match => match.Score)
            .ThenBy(match => match.Type, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (ranked.Count == 0)
        {
            return true;
        }

        foreach (var match in ranked)
        {
            var display = FormatMkTypeDisplay(match.Type);
            if (isMake)
            {
                candidates.Add((
                    $"make --type {display}",
                    $"make --type {match.Type} "));
            }
            else
            {
                candidates.Add((
                    $"mk {display}",
                    $"mk {match.Type} "));
            }
        }

        return true;
    }

    private static bool TryGetHierarchyMkTypeComposerCandidates(
        string input,
        CliSessionState session,
        out List<(string Label, string? CommitCommand)> candidates)
    {
        candidates = [];
        if (session.Inspector is not null
            || session.ContextMode != CliContextMode.Hierarchy)
        {
            return false;
        }

        var trimmed = input.TrimStart();
        if (trimmed.StartsWith('/'))
        {
            return false;
        }

        var tokens = CliCommandParsingService.TokenizeComposerInput(trimmed);
        if (tokens.Count == 0)
        {
            return false;
        }

        var isMk = tokens[0].Equals("mk", StringComparison.OrdinalIgnoreCase);
        var isMake = tokens[0].Equals("make", StringComparison.OrdinalIgnoreCase);
        if (!isMk && !isMake)
        {
            return false;
        }

        var mkTypeQuery = ResolveProjectMkTypeQuery(tokens, isMake);
        var knownTypes = session.ProjectView.CachedHierarchyMkTypes.Count > 0
            ? (IReadOnlyList<string>)session.ProjectView.CachedHierarchyMkTypes
            : HierarchyMkFallbackTypes;
        var matches = new List<(string Type, double Score)>();
        foreach (var type in knownTypes)
        {
            var display = FormatMkTypeDisplay(type);
            if (string.IsNullOrWhiteSpace(mkTypeQuery))
            {
                matches.Add((type, 1d));
                continue;
            }

            if (display.Contains(mkTypeQuery, StringComparison.OrdinalIgnoreCase)
                || type.Contains(mkTypeQuery, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add((type, 0.9d));
                continue;
            }

            if (FuzzyMatcher.TryScore(mkTypeQuery, display, out var displayScore)
                || FuzzyMatcher.TryScore(mkTypeQuery, type, out displayScore))
            {
                matches.Add((type, displayScore));
            }
        }

        var ranked = matches
            .OrderByDescending(match => match.Score)
            .ThenBy(match => match.Type, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (ranked.Count == 0)
        {
            return true;
        }

        foreach (var match in ranked)
        {
            var display = FormatMkTypeDisplay(match.Type);
            if (isMake)
            {
                candidates.Add((
                    $"make --type {display}",
                    $"make --type {match.Type} "));
            }
            else
            {
                candidates.Add((
                    $"mk {display}",
                    $"mk {match.Type} "));
            }
        }

        return true;
    }

    private static readonly IReadOnlyList<string> HierarchyMkFallbackTypes =
    [
        "Canvas", "Panel", "Text", "Tmp", "Image", "Button", "Toggle", "Slider",
        "Scrollbar", "ScrollView", "EventSystem",
        "Cube", "Sphere", "Capsule", "Cylinder", "Plane", "Quad",
        "DirLight", "DirectionalLight", "PointLight", "SpotLight", "AreaLight", "ReflectionProbe",
        "Sprite", "SpriteMask",
        "Camera", "AudioSource", "Empty", "EmptyParent", "EmptyChild"
    ];

    private static bool TryGetInspectorComponentComposerCandidates(
        string input,
        CliSessionState session,
        out List<(string Label, string? CommitCommand)> candidates)
    {
        candidates = [];

        var context = session.Inspector;
        if (context is null || context.Depth != InspectorDepth.ComponentList)
        {
            return false;
        }

        var trimmed = input.TrimStart();
        if (trimmed.StartsWith('/'))
        {
            return false;
        }

        var tokens = CliCommandParsingService.TokenizeComposerInput(trimmed);
        if (tokens.Count == 0)
        {
            return false;
        }

        var root = tokens[0].ToLowerInvariant();
        if (root is not ("component" or "comp"))
        {
            return false;
        }

        if (tokens.Count >= 2 && !tokens[1].Equals("add", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var query = tokens.Count >= 3
            ? string.Join(' ', tokens.Skip(2))
            : string.Empty;

        var knownComponents = session.ProjectView.CachedComponentTypes.Count > 0
            ? (IReadOnlyList<string>)session.ProjectView.CachedComponentTypes
            : InspectorComponentCatalog.KnownDisplayNames;
        var matches = new List<(string DisplayName, double Score)>();
        foreach (var displayName in knownComponents)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                matches.Add((displayName, 1d));
                continue;
            }

            if (displayName.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add((displayName, 0.9d));
                continue;
            }

            if (FuzzyMatcher.TryScore(query, displayName, out var score))
            {
                matches.Add((displayName, score));
            }
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var match in matches
                     .OrderByDescending(x => x.Score)
                     .ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            if (!seen.Add(match.DisplayName))
            {
                continue;
            }

            candidates.Add((
                $"component add {match.DisplayName}",
                $"component add {match.DisplayName}"));
        }

        return true;
    }

    private static string ResolveProjectMkTypeQuery(IReadOnlyList<string> tokens, bool isMake)
    {
        if (!isMake)
        {
            return tokens.Count >= 2 ? tokens[1] : string.Empty;
        }

        for (var i = 1; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.Equals("--type", StringComparison.OrdinalIgnoreCase) || token.Equals("-t", StringComparison.OrdinalIgnoreCase))
            {
                return i + 1 < tokens.Count ? tokens[i + 1] : string.Empty;
            }

            if (token.StartsWith("--type=", StringComparison.OrdinalIgnoreCase))
            {
                return token["--type=".Length..];
            }
        }

        return string.Empty;
    }

    private static bool TryBuildMkUsageHint(
        string input,
        IReadOnlyList<(string Label, string? CommitCommand)> candidates,
        int selectedIndex,
        out string usageHint)
    {
        usageHint = string.Empty;
        if (candidates.Count == 0 || selectedIndex < 0 || selectedIndex >= candidates.Count)
        {
            return false;
        }

        var tokens = CliCommandParsingService.TokenizeComposerInput(input.TrimStart());
        if (tokens.Count == 0)
        {
            return false;
        }

        var command = tokens[0].ToLowerInvariant();
        var showUsage = command switch
        {
            "mk" => tokens.Count >= 2,
            "make" => HasConcreteMakeType(tokens),
            _ => false
        };
        if (!showUsage)
        {
            return false;
        }

        var label = candidates[selectedIndex].Label;
        if (command == "mk" && label.StartsWith("mk ", StringComparison.OrdinalIgnoreCase))
        {
            var typeDisplay = label[3..].Trim();
            usageHint = $"Usage: mk {typeDisplay} [count] [--name <name>|-n <name>] [--parent <idx|name>]";
            return true;
        }

        if (command == "make" && label.StartsWith("make --type ", StringComparison.OrdinalIgnoreCase))
        {
            var typeDisplay = label["make --type ".Length..].Trim();
            usageHint = $"Usage: make --type {typeDisplay} [--count <count>] [--name <name>|-n <name>] [--parent <idx|name>]";
            return true;
        }

        return false;
    }

    private static bool HasConcreteMakeType(IReadOnlyList<string> tokens)
    {
        for (var i = 1; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.StartsWith("--type=", StringComparison.OrdinalIgnoreCase))
            {
                return !string.IsNullOrWhiteSpace(token["--type=".Length..]);
            }

            if (token.Equals("--type", StringComparison.OrdinalIgnoreCase) || token.Equals("-t", StringComparison.OrdinalIgnoreCase))
            {
                return i + 1 < tokens.Count && !tokens[i + 1].StartsWith("-", StringComparison.Ordinal);
            }
        }

        return false;
    }

    private static string FormatMkTypeDisplay(string canonicalType)
    {
        if (string.IsNullOrWhiteSpace(canonicalType))
        {
            return canonicalType;
        }

        var builder = new StringBuilder(canonicalType.Length + 4);
        for (var i = 0; i < canonicalType.Length; i++)
        {
            var ch = canonicalType[i];
            var isBoundary = i > 0
                             && char.IsUpper(ch)
                             && (char.IsLower(canonicalType[i - 1]) || char.IsDigit(canonicalType[i - 1]));
            if (isBoundary)
            {
                builder.Append(' ');
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }

    private static List<CommandSpec> GetSuggestionMatches(string query, List<CommandSpec> commands, CliSessionState session)
    {
        var normalized = query.Trim().ToLowerInvariant();
        return commands
            .Where(command => IsSuggestionAvailableForSession(command, session))
            .Where(c => !c.Description.StartsWith("Alias for", StringComparison.OrdinalIgnoreCase))
            .Where(c => c.Signature.Contains(normalized, StringComparison.OrdinalIgnoreCase)
                        || c.Description.Contains(normalized, StringComparison.OrdinalIgnoreCase)
                        || c.Trigger.StartsWith(normalized, StringComparison.OrdinalIgnoreCase)
                        || normalized.StartsWith(c.Trigger, StringComparison.OrdinalIgnoreCase))
            .Take(14)
            .ToList();
    }

    private static bool IsSuggestionAvailableForSession(CommandSpec command, CliSessionState session)
    {
        var hasOpenProject = session.Mode == CliMode.Project
            && !string.IsNullOrWhiteSpace(session.CurrentProjectPath);
        if (hasOpenProject)
        {
            return true;
        }

        if (!command.Trigger.StartsWith('/'))
        {
            return false;
        }

        return !command.Trigger.StartsWith("/project", StringComparison.Ordinal)
            && !command.Trigger.StartsWith("/hierarchy", StringComparison.Ordinal)
            && !command.Trigger.StartsWith("/inspect", StringComparison.Ordinal)
            && !command.Trigger.StartsWith("/upm", StringComparison.Ordinal)
            && !command.Trigger.StartsWith("/build", StringComparison.Ordinal);
    }
}
