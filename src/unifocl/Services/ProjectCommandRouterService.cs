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

        var projectPath = session.CurrentProjectPath;
        if (await _projectViewService.TryHandleProjectViewCommandAsync(input, session, daemonControlService, daemonRuntime))
        {
            return true;
        }

        var tokens = Tokenize(input);
        if (tokens.Count == 0)
        {
            return true;
        }

        if (await _inspectorModeService.TryHandleInspectorCommandAsync(
                input,
                tokens,
                session,
                log))
        {
            return true;
        }

        if (IsFileBypassCommand(tokens))
        {
            return HandleFileBypassCommand(tokens, projectPath, log);
        }

        if (IsDaemonCommand(tokens))
        {
            var touched = await daemonControlService.TouchAttachedDaemonAsync(session);
            if (!touched)
            {
                await AnsiConsole.Status()
                    .Spinner(Spinner.Known.Dots)
                    .StartAsync("Headless daemon sleeping. Cold starting...", async _ =>
                    {
                        await daemonControlService.EnsureProjectDaemonAsync(projectPath, daemonRuntime, session, log);
                    });
            }

            if (!await daemonControlService.TouchAttachedDaemonAsync(session))
            {
                log("[red]daemon[/]: unavailable after cold start attempt");
                return true;
            }

            log("[grey]daemon[/]: routed command to headless daemon (stub bridge)");
            log($"[deepskyblue1]stub[/]: daemon command [white]{Markup.Escape(input)}[/]");
            return true;
        }

        return false;
    }

    private static bool IsFileBypassCommand(IReadOnlyList<string> tokens)
    {
        if (tokens.Count >= 2 && tokens[0].Equals("mkdir", StringComparison.OrdinalIgnoreCase))
        {
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

        if (tokens[0].Equals("mv", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (tokens[0].Equals("set", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (tokens.Count >= 2
            && tokens[0].Equals("mk", StringComparison.OrdinalIgnoreCase)
            && tokens[1].Equals("cube", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static bool HandleFileBypassCommand(IReadOnlyList<string> tokens, string projectPath, Action<string> log)
    {
        if (tokens[0].Equals("mkdir", StringComparison.OrdinalIgnoreCase))
        {
            var relative = tokens[1];
            var target = ResolveProjectPath(projectPath, relative);
            Directory.CreateDirectory(target);
            log($"[green]fs[/]: created directory [white]{Markup.Escape(target)}[/] (daemon bypass)");
            return true;
        }

        return false;
    }

    private static string ResolveProjectPath(string projectPath, string relativeOrAbsolute)
    {
        if (Path.IsPathRooted(relativeOrAbsolute))
        {
            return relativeOrAbsolute;
        }

        return Path.GetFullPath(Path.Combine(projectPath, relativeOrAbsolute));
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
