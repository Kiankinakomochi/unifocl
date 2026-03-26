using Spectre.Console;
using System.Text;

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

        var tokens = Tokenize(normalizedInput);
        var autoEnterInspectorFocus = false;
        if (TryStripInspectorFocusFlags(tokens, out var requestInspectorFocus))
        {
            autoEnterInspectorFocus = requestInspectorFocus;
            normalizedInput = string.Join(' ', tokens);
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

    private static bool TryStripInspectorFocusFlags(List<string> tokens, out bool requestFocus)
    {
        requestFocus = false;
        if (tokens.Count == 0 || !tokens[0].Equals("inspect", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var removed = false;
        for (var i = tokens.Count - 1; i >= 1; i--)
        {
            if (tokens[i].Equals("--focus", StringComparison.OrdinalIgnoreCase)
                || tokens[i].Equals("--interactive", StringComparison.OrdinalIgnoreCase))
            {
                tokens.RemoveAt(i);
                requestFocus = true;
                removed = true;
            }
        }

        return removed;
    }

    private static string? NormalizeContextualInput(string input, CliContextMode mode, Action<string> log)
    {
        var tokens = Tokenize(input);
        if (tokens.Count == 0)
        {
            return input;
        }

        tokens[0] = tokens[0].ToLowerInvariant() switch
        {
            "ins" => "inspect",
            "list" => "ls",
            "ref" => "ls",
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
            _ => tokens[0]
        };

        if (tokens[0].Equals("upm", StringComparison.OrdinalIgnoreCase) && tokens.Count >= 2)
        {
            tokens[1] = tokens[1].ToLowerInvariant() switch
            {
                "list" => "ls",
                "add" => "install",
                "i" => "install",
                "rm" => "remove",
                "uninstall" => "remove",
                "u" => "update",
                _ => tokens[1]
            };
        }

        if (tokens[0].Equals("up", StringComparison.OrdinalIgnoreCase))
        {
            tokens[0] = mode == CliContextMode.Inspector ? ":i" : "up";
        }

        if (tokens[0].Equals("cd", StringComparison.OrdinalIgnoreCase))
        {
            if (tokens.Count < 2)
            {
                log("[yellow]usage[/]: enter <idx>");
                return null;
            }

            if (mode == CliContextMode.Project)
            {
                return $"cd {tokens[1]} -nest";
            }

            if (mode == CliContextMode.Inspector)
            {
                return $"inspect {tokens[1]}";
            }
        }

        if (tokens[0].Equals("set", StringComparison.OrdinalIgnoreCase) && mode == CliContextMode.Project)
        {
            log("[yellow]project[/]: set is blocked in project mode");
            return null;
        }

        if (tokens[0].Equals("toggle", StringComparison.OrdinalIgnoreCase) && mode == CliContextMode.Project)
        {
            log("[yellow]project[/]: toggle is blocked in project mode");
            return null;
        }

        return string.Join(' ', tokens);
    }

    private static List<string> Tokenize(string input)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        foreach (var ch in input)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (!inQuotes && char.IsWhiteSpace(ch))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(ch);
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    /// <summary>Returns the structured <see cref="MutateBatchResult"/> so callers can surface it in agentic responses.</summary>
    public Task<MutateBatchResult?> HandleMutateCommandAsync(string mutatePayload, CliSessionState session, Action<string> log)
        => _mutateBatchService.HandleCommandAsync(mutatePayload, session, log);
}
