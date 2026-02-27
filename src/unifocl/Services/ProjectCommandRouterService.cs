using Spectre.Console;
using System.Text;

internal sealed class ProjectCommandRouterService
{
    private readonly InspectorModeService _inspectorModeService = new();

    public async Task<bool> TryHandleProjectCommandAsync(
        string input,
        CliSessionState session,
        DaemonControlService daemonControlService,
        DaemonRuntime daemonRuntime,
        Action<string> log)
    {
        if (string.IsNullOrWhiteSpace(session.CurrentProjectPath))
        {
            log("[grey]system[/]: boot mode is slash-first; type / to see available commands");
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
            return HandleFileBypassCommand(tokens, session.CurrentProjectPath, log);
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
                        await daemonControlService.EnsureProjectDaemonAsync(session.CurrentProjectPath, daemonRuntime, session, log);
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
        if (tokens.Count >= 3 && tokens[0].Equals("mk", StringComparison.OrdinalIgnoreCase) && tokens[1].Equals("script", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

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

        var scriptName = tokens[2];
        var sanitized = SanitizeTypeName(scriptName);
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            log("[red]fs[/]: invalid script name");
            return true;
        }

        var scriptsDir = Path.Combine(projectPath, "Assets", "Scripts");
        Directory.CreateDirectory(scriptsDir);
        var targetPath = Path.Combine(scriptsDir, sanitized + ".cs");

        if (File.Exists(targetPath))
        {
            log($"[yellow]fs[/]: script already exists [white]{Markup.Escape(targetPath)}[/]");
            return true;
        }

        var content = BuildScriptTemplate(sanitized);
        File.WriteAllText(targetPath, content);
        log($"[green]fs[/]: created script [white]{Markup.Escape(targetPath)}[/] (daemon bypass)");
        log("[grey]fs[/]: Unity will import and generate .meta when editor/daemon runs");
        return true;
    }

    private static string ResolveProjectPath(string projectPath, string relativeOrAbsolute)
    {
        if (Path.IsPathRooted(relativeOrAbsolute))
        {
            return relativeOrAbsolute;
        }

        return Path.GetFullPath(Path.Combine(projectPath, relativeOrAbsolute));
    }

    private static string SanitizeTypeName(string raw)
    {
        var builder = new StringBuilder();
        foreach (var ch in raw)
        {
            if (char.IsLetterOrDigit(ch) || ch == '_')
            {
                builder.Append(ch);
            }
        }

        var value = builder.ToString();
        if (value.Length == 0)
        {
            return string.Empty;
        }

        if (!char.IsLetter(value[0]) && value[0] != '_')
        {
            value = "_" + value;
        }

        return value;
    }

    private static string BuildScriptTemplate(string typeName)
    {
        return
$"using UnityEngine;{Environment.NewLine}{Environment.NewLine}public class {typeName} : MonoBehaviour{Environment.NewLine}{{{Environment.NewLine}    private void Start(){{ }}{Environment.NewLine}{Environment.NewLine}    private void Update(){{ }}{Environment.NewLine}}}{Environment.NewLine}";
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
