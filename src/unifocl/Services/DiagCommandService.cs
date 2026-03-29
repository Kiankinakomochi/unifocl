using System.Text.Json;
using Spectre.Console;

internal sealed class DiagCommandService
{
    private static readonly JsonSerializerOptions ReadJsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly HierarchyDaemonClient _daemonClient = new();

    private static readonly string[] AllOps =
    [
        "script-defines", "compile-errors", "assembly-graph", "scene-deps", "prefab-deps",
        "asset-size", "import-hotspots"
    ];

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
            log("[x] usage: /diag <script-defines|compile-errors|assembly-graph|scene-deps|prefab-deps|asset-size|import-hotspots|all>");
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
                case "asset-size":
                case "import-hotspots":
                    await RunDaemonDiagAsync(op, session, log);
                    break;
                default:
                    log($"[x] unknown diag op: {Markup.Escape(op)}");
                    log("supported: script-defines | compile-errors | assembly-graph | scene-deps | prefab-deps | asset-size | import-hotspots | all");
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
            case "scene-deps":     RenderSceneDeps(response.Content, log); break;
            case "prefab-deps":    RenderPrefabDeps(response.Content, log); break;
            case "asset-size":     RenderAssetSize(response.Content, log); break;
            case "import-hotspots": RenderImportHotspots(response.Content, log); break;
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

        foreach (var msg in result.Messages)
        {
            var (color, icon) = msg.Type switch
            {
                "Warning" => (CliTheme.Warning, "⚠"),
                _         => (CliTheme.Error, "✗")
            };
            log($"  [{color}]{icon}[/] {Markup.Escape(msg.Message)}");
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

    private static void RenderAssetSize(string json, Action<string> log)
    {
        try
        {
            var payload = JsonSerializer.Deserialize<AssetSizePayload>(json, ReadJsonOptions);
            if (payload is null) { log("[red]diag[/]: asset-size — failed to parse response"); return; }

            log($"[bold {CliTheme.TextPrimary}]asset-size[/]  [{CliTheme.TextMuted}]{payload.TotalAssets} asset(s), total {FormatBytes(payload.TotalSizeBytes)}[/]");

            const int MaxShow = 30;
            var shown = Math.Min(payload.Assets?.Length ?? 0, MaxShow);
            for (var i = 0; i < shown; i++)
            {
                var a = payload.Assets![i];
                log($"  [{CliTheme.TextPrimary}]{FormatBytes(a.SizeBytes),8}[/]  [{CliTheme.TextMuted}]deps={a.DepCount}[/]  {Markup.Escape(a.Path)}");
            }

            if ((payload.Assets?.Length ?? 0) > MaxShow)
                log($"  [{CliTheme.TextMuted}](+ {payload.Assets!.Length - MaxShow} more)[/]");
        }
        catch
        {
            log("[red]diag[/]: asset-size — failed to parse response");
        }
    }

    private static void RenderImportHotspots(string json, Action<string> log)
    {
        try
        {
            var payload = JsonSerializer.Deserialize<ImportHotspotPayload>(json, ReadJsonOptions);
            if (payload is null) { log("[red]diag[/]: import-hotspots — failed to parse response"); return; }

            log($"[bold {CliTheme.TextPrimary}]import-hotspots[/]  [{CliTheme.TextMuted}]{payload.BatchesRecorded} batch(es) recorded, {payload.UniqueAssetsTracked} unique asset(s) tracked[/]");

            if (payload.Hotspots is null || payload.Hotspots.Length == 0)
            {
                log($"  [{CliTheme.TextMuted}](no import data yet — assets must be imported while daemon is running)[/]");
                return;
            }

            foreach (var h in payload.Hotspots)
                log($"  [{CliTheme.TextPrimary}]imports={h.ImportCount,4}[/]  {Markup.Escape(h.AssetPath)}");
        }
        catch
        {
            log("[red]diag[/]: import-hotspots — failed to parse response");
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1_073_741_824) return $"{bytes / 1_073_741_824.0:0.##} GB";
        if (bytes >= 1_048_576)     return $"{bytes / 1_048_576.0:0.##} MB";
        if (bytes >= 1_024)         return $"{bytes / 1_024.0:0.##} KB";
        return $"{bytes} B";
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

    // ── JSON payload contracts ────────────────────────────────────────────────

    private sealed class AssetSizePayload
    {
        public int TotalAssets { get; set; }
        public long TotalSizeBytes { get; set; }
        public AssetEntry[]? Assets { get; set; }

        public sealed class AssetEntry
        {
            public string Path { get; set; } = "";
            public long SizeBytes { get; set; }
            public int DepCount { get; set; }
        }
    }

    private sealed class ImportHotspotPayload
    {
        public int BatchesRecorded { get; set; }
        public int UniqueAssetsTracked { get; set; }
        public HotspotEntry[]? Hotspots { get; set; }

        public sealed class HotspotEntry
        {
            public string AssetPath { get; set; } = "";
            public int ImportCount { get; set; }
        }
    }
}
