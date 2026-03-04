using Spectre.Console;
using System.Text;

internal sealed class ProjectCommandRouterService
{
    private readonly InspectorModeService _inspectorModeService = new();
    private readonly ProjectViewService _projectViewService = new();

    public async Task<bool> TryHandleProjectCommandAsync(
        string input,
        CliSessionState session,
        DaemonControlService daemonControlService,
        DaemonRuntime daemonRuntime,
        Action<string> log)
    {
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

            await _projectViewService.RunKeyboardFocusModeAsync(session, daemonControlService, daemonRuntime);
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
            return true;
        }

        var normalizedInput = NormalizeContextualInput(input, session.ContextMode, log);
        if (normalizedInput is null)
        {
            return true;
        }

        if (session.ContextMode == CliContextMode.Hierarchy)
        {
            log("[yellow]mode[/]: hierarchy contextual commands run inside /hierarchy mode");
            return true;
        }

        if (session.ContextMode == CliContextMode.Project
            && await _projectViewService.TryHandleProjectViewCommandAsync(normalizedInput, session, daemonControlService, daemonRuntime))
        {
            return true;
        }

        var tokens = Tokenize(normalizedInput);
        if (tokens.Count == 0)
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
            log("[grey]project[/]: use load/mk script/rename/rm/f inside project mode, or /hierarchy and /inspect for scene/object operations");
            return true;
        }

        return false;
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

    private static string? NormalizeContextualInput(string input, CliContextMode mode, Action<string> log)
    {
        var tokens = Tokenize(input);
        if (tokens.Count == 0)
        {
            return input;
        }

        tokens[0] = tokens[0].ToLowerInvariant() switch
        {
            "list" => "ls",
            "ref" => "ls",
            "enter" => "cd",
            ".." => "up",
            "make" => "mk",
            "remove" => "rm",
            "rn" => "rename",
            "s" => "set",
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
}
