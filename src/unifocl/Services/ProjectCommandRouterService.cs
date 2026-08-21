using Spectre.Console;

internal sealed class ProjectCommandRouterService
{
    private readonly InspectorModeService _inspectorModeService = new();
    private readonly ProjectViewService _projectViewService = new();
    private readonly MutateBatchService _mutateBatchService = new();

    public async Task<bool> TryHandleProjectCommandAsync(
        string input,
        CliSessionState session,
        DaemonControlService daemonControlService,
        DaemonRuntime daemonRuntime,
        Action<string> log)
    {
        var enteredFromHierarchyContext = session.ContextMode == CliContextMode.Hierarchy;

        if (session.Mode != CliMode.Project || string.IsNullOrWhiteSpace(session.CurrentProjectPath))
        {
            log("[grey]system[/]: boot mode is slash-first; type / to see available commands");
            return true;
        }

        if (session.ContextMode == CliContextMode.None)
        {
            session.ContextMode = CliContextMode.Project;
        }

        if (input.Equals(":focus-project", StringComparison.OrdinalIgnoreCase))
        {
            if (session.ContextMode != CliContextMode.Project)
            {
                log("[yellow]project[/]: focus navigation is available in project context only");
                return true;
            }

            await _projectViewService.RunKeyboardFocusModeAsync(session, daemonControlService, daemonRuntime, log);
            return true;
        }

        if (input.Equals(":focus-inspector", StringComparison.OrdinalIgnoreCase))
        {
            if (session.ContextMode != CliContextMode.Inspector || session.Inspector is null)
            {
                log("[yellow]inspector[/]: focus navigation is available in inspector context only");
                return true;
            }

            await _inspectorModeService.RunKeyboardFocusModeAsync(session, log);
            _inspectorModeService.RenderCurrentFrame(session);
            return true;
        }

        // /mutate is context-free: infers hierarchy vs inspector per-op from the op type.
        // Intercept before mode checks to avoid the hierarchy-TUI guard.
        if (IsMutateCommand(input))
        {
            await _mutateBatchService.HandleCommandAsync(input, session, log);
            return true;
        }


        var normalizedInput = NormalizeContextualInput(input, session.ContextMode, log);
        if (normalizedInput is null)
        {
            return true;
        }

        var spans = CliCommandParsingService.TokenizeWithSpans(normalizedInput);
        var tokens = spans.Select(s => s.Value).ToList();
        var autoEnterInspectorFocus = false;
        if (TryStripInspectorFocusFlags(normalizedInput, spans, tokens, out var strippedInput))
        {
            autoEnterInspectorFocus = true;
            normalizedInput = strippedInput;
        }

        if (tokens.Count == 0)
        {
            if (session.ContextMode == CliContextMode.Project)
            {
                await _projectViewService.TryHandleProjectViewCommandAsync(
                    string.Empty,
                    session,
                    daemonControlService,
                    daemonRuntime,
                    log);
            }

            return true;
        }

        var isInspectCommand = tokens[0].Equals("inspect", StringComparison.OrdinalIgnoreCase);
        if (session.ContextMode == CliContextMode.Hierarchy && !isInspectCommand)
        {
            log("[yellow]mode[/]: hierarchy contextual commands run inside /hierarchy mode");
            return true;
        }

        if (session.ContextMode == CliContextMode.Project
            && await _projectViewService.TryHandleProjectViewCommandAsync(normalizedInput, session, daemonControlService, daemonRuntime, log))
        {
            return true;
        }

        if ((session.ContextMode == CliContextMode.Inspector || tokens[0].Equals("inspect", StringComparison.OrdinalIgnoreCase))
            && await _inspectorModeService.TryHandleInspectorCommandAsync(
                normalizedInput,
                tokens,
                session,
                log))
        {
            session.ContextMode = session.Inspector is null ? CliContextMode.Project : CliContextMode.Inspector;
            if (session.ContextMode == CliContextMode.Inspector
                && tokens[0].Equals("inspect", StringComparison.OrdinalIgnoreCase)
                && autoEnterInspectorFocus)
            {
                await _inspectorModeService.RunKeyboardFocusModeAsync(session, log, enteredFromHierarchyContext);
                _inspectorModeService.RenderCurrentFrame(session);
            }

            return true;
        }

        if (session.ContextMode == CliContextMode.Inspector)
        {
            log("[yellow]inspector[/]: unsupported command in inspector mode");
            return true;
        }

        if (IsDaemonCommand(tokens))
        {
            log($"[yellow]project[/]: unsupported project command: [white]{Markup.Escape(normalizedInput)}[/]");
            log("[grey]project[/]: use load/mk/make/rename/rm/f inside project mode, or /hierarchy and /inspect for scene/object operations");
            return true;
        }

        return false;
    }

    private static bool IsMutateCommand(string input)
    {
        var trimmed = input.TrimStart().TrimStart('/');
        return trimmed.StartsWith("mutate ", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("mutate", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDaemonCommand(IReadOnlyList<string> tokens)
    {
        if (tokens.Count == 0)
        {
            return false;
        }

        if (tokens[0].Equals("move", StringComparison.OrdinalIgnoreCase)
            || tokens[0].Equals("mv", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (tokens[0].Equals("set", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (tokens.Count >= 2
            && (tokens[0].Equals("make", StringComparison.OrdinalIgnoreCase) || tokens[0].Equals("mk", StringComparison.OrdinalIgnoreCase))
            && tokens[1].Equals("cube", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Removes "--focus"/"--interactive" from an "inspect" command. Works on raw
    /// token spans so quoting elsewhere in the input survives the rewrite; the
    /// span list and value list are kept in sync for downstream token routing.
    /// </summary>
    internal static bool TryStripInspectorFocusFlags(
        string input,
        List<RawToken> spans,
        List<string> tokens,
        out string strippedInput)
    {
        strippedInput = input;
        if (tokens.Count == 0 || !tokens[0].Equals("inspect", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var removed = false;
        for (var i = spans.Count - 1; i >= 1; i--)
        {
            var raw = input.Substring(spans[i].Start, spans[i].Length);
            if (raw.Equals("--focus", StringComparison.OrdinalIgnoreCase)
                || raw.Equals("--interactive", StringComparison.OrdinalIgnoreCase))
            {
                spans.RemoveAt(i);
                tokens.RemoveAt(i);
                removed = true;
            }
        }

        if (removed)
        {
            strippedInput = string.Join(' ', spans.Select(s => input.Substring(s.Start, s.Length)));
        }

        return removed;
    }

    /// <summary>
    /// Rewrites command-word aliases (head token, and the subcommand for "upm")
    /// while keeping the rest of the input byte-for-byte intact. The rewrite works
    /// on raw token spans: quote characters in arguments (e.g. paths with spaces)
    /// must survive so the downstream quote-aware tokenizers still group them.
    /// </summary>
    internal static string? NormalizeContextualInput(string input, CliContextMode mode, Action<string> log)
    {
        var spans = CliCommandParsingService.TokenizeWithSpans(input);
        if (spans.Count == 0)
        {
            return input;
        }

        var rawParts = spans.Select(s => input.Substring(s.Start, s.Length)).ToList();
        var head = spans[0].Value.ToLowerInvariant() switch
        {
            "ins" => "inspect",
            "list" => "ls",
            "ref" => "ls",
            "addressables" => "addressable",
            "enter" => "cd",
            ".." => "up",
            "make" => mode == CliContextMode.Inspector ? "make" : "mk",
            "remove" => "rm",
            "rn" => "rename",
            "s" => "set",
            "e" => "edit",
            "t" => "toggle",
            "find" => "f",
            "move" => "mv",
            _ => spans[0].Value
        };

        if (head.Equals("upm", StringComparison.OrdinalIgnoreCase) && spans.Count >= 2)
        {
            rawParts[1] = spans[1].Value.ToLowerInvariant() switch
            {
                "list" => "ls",
                "add" => "install",
                "i" => "install",
                "rm" => "remove",
                "uninstall" => "remove",
                "u" => "update",
                _ => rawParts[1]
            };
        }

        if (head.Equals("up", StringComparison.OrdinalIgnoreCase))
        {
            head = mode == CliContextMode.Inspector ? ":i" : "up";
        }

        if (head.Equals("cd", StringComparison.OrdinalIgnoreCase))
        {
            if (spans.Count < 2)
            {
                log("[yellow]usage[/]: enter <idx>");
                return null;
            }

            if (mode == CliContextMode.Project)
            {
                return $"cd {spans[1].Value} -nest";
            }

            if (mode == CliContextMode.Inspector)
            {
                return $"inspect {spans[1].Value}";
            }
        }

        if (head.Equals("set", StringComparison.OrdinalIgnoreCase) && mode == CliContextMode.Project)
        {
            log("[yellow]project[/]: set is blocked in project mode");
            return null;
        }

        if (head.Equals("toggle", StringComparison.OrdinalIgnoreCase) && mode == CliContextMode.Project)
        {
            log("[yellow]project[/]: toggle is blocked in project mode");
            return null;
        }

        rawParts[0] = head;
        return string.Join(' ', rawParts);
    }

    /// <summary>Returns the structured <see cref="MutateBatchResult"/> so callers can surface it in agentic responses.</summary>
    public Task<MutateBatchResult?> HandleMutateCommandAsync(string mutatePayload, CliSessionState session, Action<string> log)
        => _mutateBatchService.HandleCommandAsync(mutatePayload, session, log);
}
