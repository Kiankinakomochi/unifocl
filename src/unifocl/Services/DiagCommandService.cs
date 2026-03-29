using System.Text.Json;
using Spectre.Console;

internal sealed class DiagCommandService
{
    private static readonly JsonSerializerOptions ReadJsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly HierarchyDaemonClient _daemonClient = new();

    private static readonly string[] AllOps = ["script-defines", "compile-errors", "assembly-graph", "scene-deps", "prefab-deps"];

    public async Task HandleDiagCommandAsync(
        string input,
        CliSessionState session,
        DaemonControlService daemonControlService,
        DaemonRuntime daemonRuntime,
        Action<string> log)
    {
        if (session.Mode != CliMode.Project || string.IsNullOrWhiteSpace(session.CurrentProjectPath))
        {
            log("[yellow]diag[/]: open a project first with /open");
            return;
        }

        var tokens = Tokenize(input);
        if (tokens.Count < 2)
        {
            log("[x] usage: /diag <script-defines|compile-errors|assembly-graph|scene-deps|prefab-deps|all>");
            return;
        }

        var subcommand = tokens[1].ToLowerInvariant();
        var ops = subcommand == "all" ? AllOps : new[] { subcommand };

        var isAll = subcommand == "all";
        var first = true;
        foreach (var op in ops)
        {
            if (isAll && !first)
            {
                var sep = "─── " + new string('─', 40);
                log($"[{CliTheme.TextMuted}]{sep}[/]");
            }

            first = false;

            switch (op)
            {
                case "script-defines":
                case "compile-errors":
                case "assembly-graph":
                case "scene-deps":
                case "prefab-deps":
                    await RunDaemonDiagAsync(op, session, log);
                    break;
                default:
                    log($"[x] unknown diag op: {Markup.Escape(op)}");
                    log("supported: script-defines | compile-errors | assembly-graph | scene-deps | prefab-deps | all");
                    return;
            }
        }
    }

    private async Task RunDaemonDiagAsync(string op, CliSessionState session, Action<string> log)
    {
        if (DaemonControlService.GetPort(session) is not int port)
        {
            log("[yellow]diag[/]: daemon not running — start with /open");
            return;
        }

        var action = $"diag-{op}";
        var request = new ProjectCommandRequestDto(action, null, null, null, Guid.NewGuid().ToString("N"));

        log($"[grey]diag[/]: running {Markup.Escape(op)}...");
        var response = await _daemonClient.ExecuteProjectCommandAsync(port, request,
            onStatus: status => log($"[grey]diag[/]: {Markup.Escape(status)}"));

        if (!response.Ok)
        {
            log($"[red]diag[/]: {Markup.Escape(op)} failed — {Markup.Escape(response.Message)}");
            return;
        }

        if (string.IsNullOrWhiteSpace(response.Content))
        {
            log($"[grey]diag[/]: {Markup.Escape(op)} — {Markup.Escape(response.Message)}");
            return;
        }

        switch (op)
        {
            case "script-defines": RenderScriptDefines(response.Content, log); break;
            case "compile-errors": RenderCompileErrors(response.Content, log); break;
            case "assembly-graph": RenderAssemblyGraph(response.Content, log); break;
            case "scene-deps":    RenderSceneDeps(response.Content, log); break;
            case "prefab-deps":   RenderPrefabDeps(response.Content, log); break;
        }
    }

    private static void RenderScriptDefines(string content, Action<string> log)
    {
        DiagScriptDefinesResult? result;
        try { result = JsonSerializer.Deserialize<DiagScriptDefinesResult>(content, ReadJsonOptions); }
        catch { log("[red]diag[/]: script-defines — failed to parse response"); return; }
        if (result is null) return;

        log($"[bold {CliTheme.TextPrimary}]script-defines[/]  [{CliTheme.TextMuted}]{result.TargetCount} build target(s)[/]");
        foreach (var t in result.Targets)
        {
            log($"  [{CliTheme.TextPrimary}]{Markup.Escape(t.BuildTarget)}[/]");
            if (string.IsNullOrWhiteSpace(t.Defines))
            {
                log($"    [{CliTheme.TextMuted}](none)[/]");
            }
            else
            {
                var defines = t.Defines.Split(';', StringSplitOptions.RemoveEmptyEntries);
                log($"    [{CliTheme.TextMuted}]{Markup.Escape(string.Join("  ", defines))}[/]");
            }
        }
    }

    private static void RenderCompileErrors(string content, Action<string> log)
    {
        DiagCompileErrorsResult? result;
        try { result = JsonSerializer.Deserialize<DiagCompileErrorsResult>(content, ReadJsonOptions); }
        catch { log("[red]diag[/]: compile-errors — failed to parse response"); return; }
        if (result is null) return;

        var statusColor = result.ErrorCount == 0 ? CliTheme.Success : CliTheme.Error;
        var statusIcon = result.ErrorCount == 0 ? "✓" : "✗";
        log($"[bold {statusColor}]{statusIcon}[/] [{CliTheme.TextPrimary}]compile-errors[/]  " +
            $"[{CliTheme.TextMuted}]{result.AssemblyCount} assembl(ies)[/]  " +
            $"[{CliTheme.Error}]{result.ErrorCount} error(s)[/]  " +
            $"[{CliTheme.Warning}]{result.WarningCount} warning(s)[/]");

        var ordered = result.Messages
            .OrderBy(m => m.Type switch { "Error" => 0, "Warning" => 1, _ => 2 })
            .ToList();

        foreach (var msg in ordered)
        {
            var (color, icon) = msg.Type switch
            {
                "Error"       => (CliTheme.Error, "✗"),
                "Warning"     => (CliTheme.Warning, "⚠"),
                _             => (CliTheme.Info, "i")
            };
            var location = string.IsNullOrEmpty(msg.File) ? string.Empty
                : $" [{CliTheme.TextMuted}]@ {Markup.Escape(msg.File)}:{msg.Line}[/]";
            log($"  [{color}]{icon}[/] [{CliTheme.TextMuted}]{Markup.Escape(msg.Assembly)}[/] {Markup.Escape(msg.Message)}{location}");
        }
    }

    private static void RenderAssemblyGraph(string content, Action<string> log)
    {
        DiagAssemblyGraphResult? result;
        try { result = JsonSerializer.Deserialize<DiagAssemblyGraphResult>(content, ReadJsonOptions); }
        catch { log("[red]diag[/]: assembly-graph — failed to parse response"); return; }
        if (result is null) return;

        log($"[bold {CliTheme.TextPrimary}]assembly-graph[/]  [{CliTheme.TextMuted}]{result.AssemblyCount} assembl(ies)[/]");
        foreach (var a in result.Assemblies)
        {
            if (string.IsNullOrWhiteSpace(a.Refs))
            {
                log($"  [{CliTheme.TextPrimary}]{Markup.Escape(a.Name)}[/]  [{CliTheme.TextMuted}](no asmdef refs)[/]");
            }
            else
            {
                log($"  [{CliTheme.TextPrimary}]{Markup.Escape(a.Name)}[/]  → [{CliTheme.TextMuted}]{Markup.Escape(a.Refs.Replace(";", ", "))}[/]");
            }
        }
    }

    private static void RenderSceneDeps(string content, Action<string> log)
    {
        DiagSceneDepsResult? result;
        try { result = JsonSerializer.Deserialize<DiagSceneDepsResult>(content, ReadJsonOptions); }
        catch { log("[red]diag[/]: scene-deps — failed to parse response"); return; }
        if (result is null) return;

        log($"[bold {CliTheme.TextPrimary}]scene-deps[/]  [{CliTheme.TextMuted}]{result.SceneCount} scene(s)[/]");
        RenderDepEntries(result.Scenes, log);
    }

    private static void RenderPrefabDeps(string content, Action<string> log)
    {
        DiagPrefabDepsResult? result;
        try { result = JsonSerializer.Deserialize<DiagPrefabDepsResult>(content, ReadJsonOptions); }
        catch { log("[red]diag[/]: prefab-deps — failed to parse response"); return; }
        if (result is null) return;

        var shown = result.Prefabs.Count;
        var total = result.PrefabCount;
        var header = shown < total
            ? $"[bold {CliTheme.TextPrimary}]prefab-deps[/]  [{CliTheme.TextMuted}]{shown} shown (of {total} total)[/]"
            : $"[bold {CliTheme.TextPrimary}]prefab-deps[/]  [{CliTheme.TextMuted}]{total} prefab(s)[/]";
        log(header);
        RenderDepEntries(result.Prefabs, log);
    }

    private static void RenderDepEntries(List<DiagDepEntry> entries, Action<string> log)
    {
        foreach (var entry in entries)
        {
            log($"  [{CliTheme.TextPrimary}]{Markup.Escape(entry.Path)}[/]  [{CliTheme.TextMuted}]{entry.DepCount} dep(s)[/]");
            if (!string.IsNullOrWhiteSpace(entry.TopDeps))
            {
                var deps = entry.TopDeps.Split(';', StringSplitOptions.RemoveEmptyEntries);
                foreach (var dep in deps)
                    log($"    [{CliTheme.TextMuted}]{Markup.Escape(dep)}[/]");
                if (entry.DepCount > deps.Length)
                    log($"    [{CliTheme.TextMuted}](+ {entry.DepCount - deps.Length} more)[/]");
            }
        }
    }

    private static List<string> Tokenize(string input)
    {
        var tokens = new List<string>();
        var span = input.AsSpan().Trim();
        while (!span.IsEmpty)
        {
            if (span[0] == '"' || span[0] == '\'')
            {
                var quote = span[0];
                span = span[1..];
                var end = span.IndexOf(quote);
                if (end < 0) end = span.Length;
                tokens.Add(span[..end].ToString());
                span = end < span.Length ? span[(end + 1)..].TrimStart() : ReadOnlySpan<char>.Empty;
            }
            else
            {
                var end = span.IndexOf(' ');
                if (end < 0) end = span.Length;
                tokens.Add(span[..end].ToString());
                span = end < span.Length ? span[(end + 1)..].TrimStart() : ReadOnlySpan<char>.Empty;
            }
        }

        return tokens;
    }
}
