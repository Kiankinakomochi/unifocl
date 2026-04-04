using System.Text.Json;
using Spectre.Console;

internal sealed class TagLayerCommandService
{
    private static readonly JsonSerializerOptions ReadJsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly HierarchyDaemonClient _daemonClient = new();

    // ── TAG ──────────────────────────────────────────────────────────────

    public async Task HandleTagCommandAsync(
        string input,
        CliSessionState session,
        DaemonControlService daemonControlService,
        DaemonRuntime daemonRuntime,
        Action<string> log)
    {
        if (session.Mode != CliMode.Project || string.IsNullOrWhiteSpace(session.CurrentProjectPath))
        {
            log("[yellow]tag[/]: open a project first with /open");
            return;
        }

        var tokens = Tokenize(input);
        if (tokens.Count < 2)
        {
            log("[x] usage: /tag <list|add <name>|remove <name>>");
            return;
        }

        switch (tokens[1].ToLowerInvariant())
        {
            case "list":
            case "ls":
                await RunTagListAsync(session, log);
                break;
            case "add":
            case "a":
                if (tokens.Count < 3) { log("[x] usage: /tag add <name>"); return; }
                await RunTagAddAsync(tokens[2], session, log);
                break;
            case "remove":
            case "rm":
                if (tokens.Count < 3) { log("[x] usage: /tag remove <name>"); return; }
                await RunTagRemoveAsync(tokens[2], session, log);
                break;
            default:
                log($"[x] unknown tag subcommand: {Markup.Escape(tokens[1])}");
                log("supported: list (ls) | add (a) <name> | remove (rm) <name>");
                break;
        }
    }

    // ── LAYER ─────────────────────────────────────────────────────────────

    public async Task HandleLayerCommandAsync(
        string input,
        CliSessionState session,
        DaemonControlService daemonControlService,
        DaemonRuntime daemonRuntime,
        Action<string> log)
    {
        if (session.Mode != CliMode.Project || string.IsNullOrWhiteSpace(session.CurrentProjectPath))
        {
            log("[yellow]layer[/]: open a project first with /open");
            return;
        }

        var tokens = Tokenize(input);
        if (tokens.Count < 2)
        {
            log("[x] usage: /layer <list|add <name> [--index <idx>]|rename <old> <new>|remove <name|index>>");
            return;
        }

        switch (tokens[1].ToLowerInvariant())
        {
            case "list":
            case "ls":
                await RunLayerListAsync(session, log);
                break;

            case "add":
            case "a":
            {
                if (tokens.Count < 3) { log("[x] usage: /layer add <name> [--index <idx>]"); return; }
                int? index = null;
                for (var i = 3; i < tokens.Count - 1; i++)
                {
                    if (tokens[i].Equals("--index", StringComparison.OrdinalIgnoreCase)
                        && int.TryParse(tokens[i + 1], out var idx))
                    {
                        index = idx;
                        break;
                    }
                }

                await RunLayerAddAsync(tokens[2], index, session, log);
                break;
            }

            case "rename":
            case "rn":
                if (tokens.Count < 4) { log("[x] usage: /layer rename <old-name|index> <new-name>"); return; }
                await RunLayerRenameAsync(tokens[2], tokens[3], session, log);
                break;

            case "remove":
            case "rm":
                if (tokens.Count < 3) { log("[x] usage: /layer remove <name|index>"); return; }
                await RunLayerRemoveAsync(tokens[2], session, log);
                break;

            default:
                log($"[x] unknown layer subcommand: {Markup.Escape(tokens[1])}");
                log("supported: list (ls) | add (a) <name> [--index <idx>] | rename (rn) <old> <new> | remove (rm) <name|index>");
                break;
        }
    }

    // ── tag daemon calls ──────────────────────────────────────────────────

    private async Task RunTagListAsync(CliSessionState session, Action<string> log)
    {
        if (DaemonControlService.GetPort(session) is not int port)
        {
            log("[yellow]tag[/]: daemon not running — start with /open");
            return;
        }

        var request = new ProjectCommandRequestDto("tag-list", null, null, null, Guid.NewGuid().ToString("N"));
        var response = await _daemonClient.ExecuteProjectCommandAsync(port, request, onStatus: null);

        if (!response.Ok)
        {
            log($"[red]tag[/]: list failed — {Markup.Escape(response.Message)}");
            return;
        }

        if (string.IsNullOrWhiteSpace(response.Content))
        {
            log("[grey]tag[/]: (no tags)");
            return;
        }

        TagListResult? result;
        try { result = JsonSerializer.Deserialize<TagListResult>(response.Content, ReadJsonOptions); }
        catch { log("[red]tag[/]: failed to parse response"); return; }
        if (result?.Tags is null) return;

        log($"[bold {CliTheme.TextPrimary}]tags[/]  [{CliTheme.TextMuted}]{result.Total} total · {result.BuiltIn} built-in · {result.Custom} custom[/]");
        foreach (var tag in result.Tags)
        {
            var kind = tag.BuiltIn
                ? $"[{CliTheme.TextMuted}]built-in[/]"
                : $"[{CliTheme.Info}]custom  [/]";
            log($"  {kind}  [{CliTheme.TextPrimary}]{Markup.Escape(tag.Name)}[/]");
        }
    }

    private async Task RunTagAddAsync(string name, CliSessionState session, Action<string> log)
    {
        if (DaemonControlService.GetPort(session) is not int port)
        {
            log("[yellow]tag[/]: daemon not running — start with /open");
            return;
        }

        var content = JsonSerializer.Serialize(new { name });
        var request = new ProjectCommandRequestDto("tag-add", null, null, content, Guid.NewGuid().ToString("N"));
        var response = await _daemonClient.ExecuteProjectCommandAsync(port, request, onStatus: null);

        if (!response.Ok)
        {
            log($"[red]tag[/]: add failed — {Markup.Escape(response.Message)}");
            return;
        }

        log($"[green]tag[/]: added [white]{Markup.Escape(name)}[/]");
    }

    private async Task RunTagRemoveAsync(string name, CliSessionState session, Action<string> log)
    {
        if (DaemonControlService.GetPort(session) is not int port)
        {
            log("[yellow]tag[/]: daemon not running — start with /open");
            return;
        }

        var content = JsonSerializer.Serialize(new { name });
        var request = new ProjectCommandRequestDto("tag-remove", null, null, content, Guid.NewGuid().ToString("N"));
        var response = await _daemonClient.ExecuteProjectCommandAsync(port, request, onStatus: null);

        if (!response.Ok)
        {
            log($"[red]tag[/]: remove failed — {Markup.Escape(response.Message)}");
            return;
        }

        log($"[green]tag[/]: removed [white]{Markup.Escape(name)}[/]");
    }

    // ── layer daemon calls ────────────────────────────────────────────────

    private async Task RunLayerListAsync(CliSessionState session, Action<string> log)
    {
        if (DaemonControlService.GetPort(session) is not int port)
        {
            log("[yellow]layer[/]: daemon not running — start with /open");
            return;
        }

        var request = new ProjectCommandRequestDto("layer-list", null, null, null, Guid.NewGuid().ToString("N"));
        var response = await _daemonClient.ExecuteProjectCommandAsync(port, request, onStatus: null);

        if (!response.Ok)
        {
            log($"[red]layer[/]: list failed — {Markup.Escape(response.Message)}");
            return;
        }

        if (string.IsNullOrWhiteSpace(response.Content))
        {
            log("[grey]layer[/]: (no layers)");
            return;
        }

        LayerListResult? result;
        try { result = JsonSerializer.Deserialize<LayerListResult>(response.Content, ReadJsonOptions); }
        catch { log("[red]layer[/]: failed to parse response"); return; }
        if (result?.Layers is null) return;

        log($"[bold {CliTheme.TextPrimary}]layers[/]  [{CliTheme.TextMuted}]{result.Total} defined[/]");
        foreach (var layer in result.Layers)
        {
            var kind = layer.Index < 8
                ? $"[{CliTheme.TextMuted}]built-in[/]"
                : $"[{CliTheme.Info}]user    [/]";
            log($"  [{CliTheme.TextMuted}]{layer.Index,2}[/]  {kind}  [{CliTheme.TextPrimary}]{Markup.Escape(layer.Name)}[/]");
        }
    }

    private async Task RunLayerAddAsync(string name, int? index, CliSessionState session, Action<string> log)
    {
        if (DaemonControlService.GetPort(session) is not int port)
        {
            log("[yellow]layer[/]: daemon not running — start with /open");
            return;
        }

        var payload = index.HasValue
            ? JsonSerializer.Serialize(new { name, index = index.Value })
            : JsonSerializer.Serialize(new { name });
        var request = new ProjectCommandRequestDto("layer-add", null, null, payload, Guid.NewGuid().ToString("N"));
        var response = await _daemonClient.ExecuteProjectCommandAsync(port, request, onStatus: null);

        if (!response.Ok)
        {
            log($"[red]layer[/]: add failed — {Markup.Escape(response.Message)}");
            return;
        }

        LayerMutateResult? result = null;
        if (!string.IsNullOrWhiteSpace(response.Content))
        {
            try { result = JsonSerializer.Deserialize<LayerMutateResult>(response.Content, ReadJsonOptions); }
            catch { }
        }

        var idxSuffix = result?.Index is int i ? $" at index {i}" : string.Empty;
        log($"[green]layer[/]: added [white]{Markup.Escape(name)}[/]{idxSuffix}");
    }

    private async Task RunLayerRenameAsync(string oldNameOrIndex, string newName, CliSessionState session, Action<string> log)
    {
        if (DaemonControlService.GetPort(session) is not int port)
        {
            log("[yellow]layer[/]: daemon not running — start with /open");
            return;
        }

        var payload = JsonSerializer.Serialize(new { nameOrIndex = oldNameOrIndex, newName });
        var request = new ProjectCommandRequestDto("layer-rename", null, null, payload, Guid.NewGuid().ToString("N"));
        var response = await _daemonClient.ExecuteProjectCommandAsync(port, request, onStatus: null);

        if (!response.Ok)
        {
            log($"[red]layer[/]: rename failed — {Markup.Escape(response.Message)}");
            return;
        }

        log($"[green]layer[/]: renamed [white]{Markup.Escape(oldNameOrIndex)}[/] → [white]{Markup.Escape(newName)}[/]");
    }

    private async Task RunLayerRemoveAsync(string nameOrIndex, CliSessionState session, Action<string> log)
    {
        if (DaemonControlService.GetPort(session) is not int port)
        {
            log("[yellow]layer[/]: daemon not running — start with /open");
            return;
        }

        var payload = JsonSerializer.Serialize(new { nameOrIndex });
        var request = new ProjectCommandRequestDto("layer-remove", null, null, payload, Guid.NewGuid().ToString("N"));
        var response = await _daemonClient.ExecuteProjectCommandAsync(port, request, onStatus: null);

        if (!response.Ok)
        {
            log($"[red]layer[/]: remove failed — {Markup.Escape(response.Message)}");
            return;
        }

        log($"[green]layer[/]: cleared slot [white]{Markup.Escape(nameOrIndex)}[/]");
    }

    // ── response DTOs ─────────────────────────────────────────────────────

    private sealed class TagListResult
    {
        public List<TagEntry> Tags { get; set; } = [];
        public int Total { get; set; }
        public int BuiltIn { get; set; }
        public int Custom { get; set; }
    }

    private sealed class TagEntry
    {
        public string Name { get; set; } = string.Empty;
        public bool BuiltIn { get; set; }
    }

    private sealed class LayerListResult
    {
        public List<LayerEntry> Layers { get; set; } = [];
        public int Total { get; set; }
    }

    private sealed class LayerEntry
    {
        public int Index { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    private sealed class LayerMutateResult
    {
        public int Index { get; set; }
    }

    private static List<string> Tokenize(string input)
        => CliCommandParsingService.TokenizeComposerInput(input);
}
