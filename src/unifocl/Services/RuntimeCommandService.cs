using System.Net.Http;
using System.Text;
using System.Text.Json;
using Spectre.Console;

/// <summary>
/// Handles interactive CLI dispatch for runtime commands that were previously
/// only accessible via one-shot exec or MCP: /console, /profiler, /recorder,
/// /playmode, /time, /compile.
///
/// - Console, playmode, time, compile: dispatched via POST /project/command
/// - Profiler, recorder: dispatched via POST /mcp/unifocl_project_command (custom tool)
/// </summary>
internal sealed class RuntimeCommandService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly HierarchyDaemonClient _daemonClient = new();

    // ── Console ─────────────────────────────────────────────────────────

    public async Task HandleConsoleCommandAsync(
        string input, CliSessionState session, Action<string> log)
    {
        if (!RequireProject(session, log, "console")) return;

        var tokens = Tokenize(input);
        if (tokens.Count < 2)
        {
            log("[x] usage: /console <dump|tail|clear>");
            return;
        }

        var sub = tokens[1].ToLowerInvariant();
        switch (sub)
        {
            case "dump":
            {
                string? type = null;
                var limit = 100;
                for (var i = 2; i < tokens.Count; i++)
                {
                    if (tokens[i] == "--type" && i + 1 < tokens.Count) type = tokens[++i];
                    else if (tokens[i] == "--limit" && i + 1 < tokens.Count && int.TryParse(tokens[++i], out var l)) limit = l;
                }

                var content = JsonSerializer.Serialize(new { type, limit });
                var dto = new ProjectCommandRequestDto("console-dump", null, null, content, NewId());
                await DispatchProjectCommandAsync(session, dto, "console", log);
                break;
            }
            case "tail":
            {
                var follow = tokens.Contains("--follow");
                var content = JsonSerializer.Serialize(new { follow });
                var dto = new ProjectCommandRequestDto("console-tail", null, null, content, NewId());
                await DispatchProjectCommandAsync(session, dto, "console", log);
                break;
            }
            case "clear":
            {
                var dto = MutationIntentFactory.EnsureProjectIntent(
                    new ProjectCommandRequestDto("console-clear", null, null, null, NewId()));
                await DispatchProjectCommandAsync(session, dto, "console", log);
                break;
            }
            default:
                log($"[x] unknown console subcommand: {Markup.Escape(sub)}");
                log("usage: /console <dump|tail|clear>");
                break;
        }
    }

    // ── Playmode ────────────────────────────────────────────────────────

    public async Task HandlePlaymodeCommandAsync(
        string input, CliSessionState session, Action<string> log)
    {
        if (!RequireProject(session, log, "playmode")) return;

        var tokens = Tokenize(input);
        if (tokens.Count < 2)
        {
            log("[x] usage: /playmode <start|stop|pause|resume|step>");
            return;
        }

        var sub = tokens[1].ToLowerInvariant();
        var action = sub switch
        {
            "start" => "playmode-start",
            "stop" => "playmode-stop",
            "pause" => "playmode-pause",
            "resume" => "playmode-resume",
            "step" => "playmode-step",
            _ => null
        };

        if (action is null)
        {
            log($"[x] unknown playmode subcommand: {Markup.Escape(sub)}");
            return;
        }

        var dto = MutationIntentFactory.EnsureProjectIntent(
            new ProjectCommandRequestDto(action, null, null, null, NewId()));
        await DispatchProjectCommandAsync(session, dto, "playmode", log);
    }

    // ── Time ────────────────────────────────────────────────────────────

    public async Task HandleTimeCommandAsync(
        string input, CliSessionState session, Action<string> log)
    {
        if (!RequireProject(session, log, "time")) return;

        var tokens = Tokenize(input);
        if (tokens.Count < 3 || !tokens[1].Equals("scale", StringComparison.OrdinalIgnoreCase))
        {
            log("[x] usage: /time scale <float>");
            return;
        }

        if (!float.TryParse(tokens[2], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var scale))
        {
            log($"[red]time[/]: '{Markup.Escape(tokens[2])}' is not a valid float");
            return;
        }

        var content = JsonSerializer.Serialize(new { scale });
        var dto = MutationIntentFactory.EnsureProjectIntent(
            new ProjectCommandRequestDto("time-scale", null, null, content, NewId()));
        await DispatchProjectCommandAsync(session, dto, "time", log);
    }

    // ── Compile ─────────────────────────────────────────────────────────

    public async Task HandleCompileCommandAsync(
        string input, CliSessionState session, Action<string> log)
    {
        if (!RequireProject(session, log, "compile")) return;

        var tokens = Tokenize(input);
        if (tokens.Count < 2)
        {
            log("[x] usage: /compile <request|status>");
            return;
        }

        var sub = tokens[1].ToLowerInvariant();
        var action = sub switch
        {
            "request" => "compile-request",
            "status" => "compile-status",
            _ => null
        };

        if (action is null)
        {
            log($"[x] unknown compile subcommand: {Markup.Escape(sub)}");
            return;
        }

        var dto = sub == "request"
            ? MutationIntentFactory.EnsureProjectIntent(
                new ProjectCommandRequestDto(action, null, null, null, NewId()))
            : new ProjectCommandRequestDto(action, null, null, null, NewId());
        await DispatchProjectCommandAsync(session, dto, "compile", log);
    }

    // ── Profiler ────────────────────────────────────────────────────────

    public async Task HandleProfilerCommandAsync(
        string input, CliSessionState session, Action<string> log)
    {
        if (!RequireProject(session, log, "profiler")) return;

        var tokens = Tokenize(input);
        if (tokens.Count < 2)
        {
            log("[x] usage: /profiler <inspect|start|stop|load|save|snapshot|frames|counters|threads|markers|sample|gc-alloc|compare|budget-check|export-summary|live|recorders|frame-timing|binary-log|annotate>");
            return;
        }

        var sub = tokens[1].ToLowerInvariant();
        string toolName;
        string argsJson;

        switch (sub)
        {
            case "inspect":
                toolName = "profiling.inspect";
                argsJson = "{}";
                break;
            case "start":
            {
                toolName = "profiling.start_recording";
                var deep = tokens.Contains("--deep");
                var editor = tokens.Contains("--editor");
                var keepFrames = tokens.Contains("--keep-frames");
                argsJson = JsonSerializer.Serialize(new { deep, editor, keepFrames });
                break;
            }
            case "stop":
                toolName = "profiling.stop_recording";
                argsJson = "{}";
                break;
            case "load":
            {
                var path = tokens.Count > 2 ? tokens[2] : null;
                if (path is null) { log("[x] usage: /profiler load <path>"); return; }
                var keepExisting = tokens.Contains("--keep-existing");
                toolName = "profiling.load_profile";
                argsJson = JsonSerializer.Serialize(new { path, keepExisting });
                break;
            }
            case "save":
            {
                var path = tokens.Count > 2 ? tokens[2] : null;
                if (path is null) { log("[x] usage: /profiler save <path>"); return; }
                toolName = "profiling.save_profile";
                argsJson = JsonSerializer.Serialize(new { path });
                break;
            }
            case "snapshot":
            {
                var path = tokens.Count > 2 ? tokens[2] : null;
                if (path is null) { log("[x] usage: /profiler snapshot <path>"); return; }
                toolName = "profiling.take_snapshot";
                argsJson = JsonSerializer.Serialize(new { path });
                break;
            }
            case "frames":
            {
                var (from, to) = ParseRange(tokens);
                if (from < 0 || to < 0) { log("[x] usage: /profiler frames --from <a> --to <b>"); return; }
                toolName = "profiling.frames";
                argsJson = JsonSerializer.Serialize(new { from, to });
                break;
            }
            case "counters":
            {
                var (from, to) = ParseRange(tokens);
                if (from < 0 || to < 0) { log("[x] usage: /profiler counters --from <a> --to <b> [--names <list>]"); return; }
                var names = ParseStringArg(tokens, "--names");
                toolName = "profiling.counters";
                argsJson = JsonSerializer.Serialize(new { from, to, names });
                break;
            }
            case "threads":
            {
                var frame = ParseIntArg(tokens, "--frame");
                if (frame < 0) { log("[x] usage: /profiler threads --frame <n>"); return; }
                toolName = "profiling.threads";
                argsJson = JsonSerializer.Serialize(new { frame });
                break;
            }
            case "markers":
            {
                var frame = ParseIntArg(tokens, "--frame");
                if (frame >= 0)
                {
                    toolName = "profiling.markers";
                    argsJson = JsonSerializer.Serialize(new { frame });
                }
                else
                {
                    var (from, to) = ParseRange(tokens);
                    if (from < 0 || to < 0) { log("[x] usage: /profiler markers --frame <n> OR --from <a> --to <b>"); return; }
                    toolName = "profiling.markers";
                    argsJson = JsonSerializer.Serialize(new { from, to });
                }
                break;
            }
            case "sample":
            {
                var frame = ParseIntArg(tokens, "--frame");
                var thread = ParseIntArg(tokens, "--thread");
                if (frame < 0 || thread < 0) { log("[x] usage: /profiler sample --frame <n> --thread <idx>"); return; }
                toolName = "profiling.sample";
                argsJson = JsonSerializer.Serialize(new { frame, thread });
                break;
            }
            case "gc-alloc":
            {
                var (from, to) = ParseRange(tokens);
                if (from < 0 || to < 0) { log("[x] usage: /profiler gc-alloc --from <a> --to <b>"); return; }
                toolName = "profiling.gc_alloc";
                argsJson = JsonSerializer.Serialize(new { from, to });
                break;
            }
            case "compare":
            {
                if (tokens.Count < 4) { log("[x] usage: /profiler compare <baseline> <candidate>"); return; }
                toolName = "profiling.compare";
                argsJson = JsonSerializer.Serialize(new { baseline = tokens[2], candidate = tokens[3] });
                break;
            }
            case "budget-check":
            {
                if (tokens.Count < 3) { log("[x] usage: /profiler budget-check <expressions...>"); return; }
                var expressions = tokens.Skip(2).ToList();
                toolName = "profiling.budget_check";
                argsJson = JsonSerializer.Serialize(new { expressions });
                break;
            }
            case "export-summary":
            {
                var path = tokens.Count > 2 ? tokens[2] : null;
                if (path is null) { log("[x] usage: /profiler export-summary <path>"); return; }
                toolName = "profiling.export_summary";
                argsJson = JsonSerializer.Serialize(new { path });
                break;
            }
            case "live":
            {
                if (tokens.Count < 3) { log("[x] usage: /profiler live <start|stop>"); return; }
                var liveSub = tokens[2].ToLowerInvariant();
                if (liveSub == "start")
                {
                    var counters = ParseStringArg(tokens, "--counters");
                    var duration = ParseIntArg(tokens, "--duration");
                    toolName = "profiling.live_start";
                    argsJson = JsonSerializer.Serialize(new { counters, duration = duration >= 0 ? duration : (int?)null });
                }
                else if (liveSub == "stop")
                {
                    toolName = "profiling.live_stop";
                    argsJson = "{}";
                }
                else
                {
                    log($"[x] unknown profiler live subcommand: {Markup.Escape(liveSub)}");
                    return;
                }
                break;
            }
            case "recorders":
                toolName = "profiling.recorders_list";
                argsJson = "{}";
                break;
            case "frame-timing":
                toolName = "profiling.frame_timing";
                argsJson = "{}";
                break;
            case "binary-log":
            {
                if (tokens.Count < 3) { log("[x] usage: /profiler binary-log <start|stop>"); return; }
                var blSub = tokens[2].ToLowerInvariant();
                if (blSub == "start")
                {
                    var path = tokens.Count > 3 ? tokens[3] : null;
                    if (path is null) { log("[x] usage: /profiler binary-log start <path>"); return; }
                    toolName = "profiling.binary_log_start";
                    argsJson = JsonSerializer.Serialize(new { path });
                }
                else if (blSub == "stop")
                {
                    toolName = "profiling.binary_log_stop";
                    argsJson = "{}";
                }
                else
                {
                    log($"[x] unknown binary-log subcommand: {Markup.Escape(blSub)}");
                    return;
                }
                break;
            }
            case "annotate":
            {
                if (tokens.Count < 4) { log("[x] usage: /profiler annotate <session|frame> <json>"); return; }
                var annSub = tokens[2].ToLowerInvariant();
                var json = string.Join(" ", tokens.Skip(3));
                if (annSub == "session")
                {
                    toolName = "profiling.annotate_session";
                    argsJson = json;
                }
                else if (annSub == "frame")
                {
                    toolName = "profiling.annotate_frame";
                    argsJson = json;
                }
                else
                {
                    log($"[x] unknown annotate subcommand: {Markup.Escape(annSub)}");
                    return;
                }
                break;
            }
            default:
                log($"[x] unknown profiler subcommand: {Markup.Escape(sub)}");
                return;
        }

        await DispatchCustomToolAsync(session, toolName, argsJson, "profiler", log);
    }

    // ── Recorder ────────────────────────────────────────────────────────

    public async Task HandleRecorderCommandAsync(
        string input, CliSessionState session, Action<string> log)
    {
        if (!RequireProject(session, log, "recorder")) return;

        var tokens = Tokenize(input);
        if (tokens.Count < 2)
        {
            log("[x] usage: /recorder <start|stop|status|config|switch|snapshot>");
            return;
        }

        var sub = tokens[1].ToLowerInvariant();
        string toolName;
        string argsJson;

        switch (sub)
        {
            case "start":
            {
                var profile = ParseStringArg(tokens, "--profile");
                toolName = "recorder.start";
                argsJson = profile is not null
                    ? JsonSerializer.Serialize(new { profile })
                    : "{}";
                break;
            }
            case "stop":
                toolName = "recorder.stop";
                argsJson = "{}";
                break;
            case "status":
                toolName = "recorder.status";
                argsJson = "{}";
                break;
            case "config":
            {
                if (tokens.Count < 3) { log("[x] usage: /recorder config <profile-name> [--output <path>] [--fps <n>] [--cap-frame-rate] [--width <n>] [--height <n>]"); return; }
                var profileName = tokens[2];
                var output = ParseStringArg(tokens, "--output");
                var fps = ParseIntArg(tokens, "--fps");
                var capFrameRate = tokens.Contains("--cap-frame-rate");
                var width = ParseIntArg(tokens, "--width");
                var height = ParseIntArg(tokens, "--height");
                toolName = "recorder.config";
                argsJson = JsonSerializer.Serialize(new
                {
                    profileName, output,
                    fps = fps >= 0 ? fps : (int?)null,
                    capFrameRate,
                    width = width >= 0 ? width : (int?)null,
                    height = height >= 0 ? height : (int?)null
                });
                break;
            }
            case "switch":
            {
                if (tokens.Count < 3) { log("[x] usage: /recorder switch <profile-name>"); return; }
                toolName = "recorder.switch";
                argsJson = JsonSerializer.Serialize(new { profileName = tokens[2] });
                break;
            }
            case "snapshot":
            {
                var outputPath = ParseStringArg(tokens, "--output");
                var superSize = ParseIntArg(tokens, "--super-size");
                toolName = "recorder.snapshot";
                argsJson = JsonSerializer.Serialize(new
                {
                    outputPath = outputPath ?? string.Empty,
                    superSize = superSize >= 1 ? superSize : 1
                });
                break;
            }
            default:
                log($"[x] unknown recorder subcommand: {Markup.Escape(sub)}");
                return;
        }

        await DispatchCustomToolAsync(session, toolName, argsJson, "recorder", log);
    }

    // ── Timeline ─────────────────────────────────────────────────────────

    public async Task HandleTimelineCommandAsync(
        string input, CliSessionState session, Action<string> log)
    {
        if (!RequireProject(session, log, "timeline")) return;

        var tokens = Tokenize(input);
        if (tokens.Count < 2)
        {
            log("[x] usage: /timeline <track|clip|bind> [subcommand] [options]");
            return;
        }

        var sub1 = tokens[1].ToLowerInvariant();
        var sub2 = tokens.Count > 2 ? tokens[2].ToLowerInvariant() : string.Empty;
        string toolName;
        string argsJson;

        switch (sub1)
        {
            case "track":
            {
                switch (sub2)
                {
                    case "add":
                    {
                        var assetPath = ParseStringArg(tokens, "--asset");
                        var type      = ParseStringArg(tokens, "--type");
                        var name      = ParseStringArg(tokens, "--name") ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(assetPath) || string.IsNullOrWhiteSpace(type))
                        {
                            log("[x] usage: /timeline track add --asset <path> --type <animation|audio|activation|control|group> [--name <name>]");
                            return;
                        }
                        toolName = "timeline.track.add";
                        argsJson = JsonSerializer.Serialize(new { assetPath, type, name });
                        break;
                    }
                    default:
                        log($"[x] unknown timeline track subcommand: {Markup.Escape(sub2)} — expected: add");
                        return;
                }
                break;
            }
            case "clip":
            {
                switch (sub2)
                {
                    case "add":
                    {
                        var assetPath = ParseStringArg(tokens, "--asset");
                        var trackName = ParseStringArg(tokens, "--track");
                        var clipName  = ParseStringArg(tokens, "--name");
                        var placementDirective = ParseStringArg(tokens, "--placement") ?? "end";
                        var placementRef       = ParseStringArg(tokens, "--ref") ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(assetPath) || string.IsNullOrWhiteSpace(trackName) || string.IsNullOrWhiteSpace(clipName))
                        {
                            log("[x] usage: /timeline clip add --asset <path> --track <name> --name <clip> [--placement <start|end|after|with|at>] [--ref <clip>] [--at <time>] [--duration <s>]");
                            return;
                        }
                        var placementTime = ParseDoubleArg(tokens, "--at",       0.0);
                        var duration      = ParseDoubleArg(tokens, "--duration", 1.0);
                        toolName = "timeline.clip.add";
                        argsJson = JsonSerializer.Serialize(new
                        {
                            assetPath,
                            trackName,
                            clipName,
                            duration,
                            placement = new { directive = placementDirective, reference = placementRef, time = placementTime }
                        });
                        break;
                    }
                    case "ease":
                    {
                        var assetPath = ParseStringArg(tokens, "--asset");
                        var trackName = ParseStringArg(tokens, "--track");
                        var clipName  = ParseStringArg(tokens, "--clip");
                        var mixIn     = ParseStringArg(tokens, "--mix-in")  ?? string.Empty;
                        var mixOut    = ParseStringArg(tokens, "--mix-out") ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(assetPath) || string.IsNullOrWhiteSpace(trackName) || string.IsNullOrWhiteSpace(clipName))
                        {
                            log("[x] usage: /timeline clip ease --asset <path> --track <name> --clip <name> [--mix-in <easing>] [--mix-out <easing>]");
                            return;
                        }
                        toolName = "timeline.clip.ease";
                        argsJson = JsonSerializer.Serialize(new { assetPath, trackName, clipName, mixIn, mixOut });
                        break;
                    }
                    case "preset":
                    {
                        var assetPath = ParseStringArg(tokens, "--asset");
                        var trackName = ParseStringArg(tokens, "--track");
                        var clipName  = ParseStringArg(tokens, "--clip");
                        var preset    = ParseStringArg(tokens, "--preset");
                        if (string.IsNullOrWhiteSpace(assetPath) || string.IsNullOrWhiteSpace(trackName) || string.IsNullOrWhiteSpace(clipName) || string.IsNullOrWhiteSpace(preset))
                        {
                            log("[x] usage: /timeline clip preset --asset <path> --track <name> --clip <name> --preset <scale-in|scale-out|fade-in|fade-out|bounce-in>");
                            return;
                        }
                        toolName = "timeline.clip.preset";
                        argsJson = JsonSerializer.Serialize(new { assetPath, trackName, clipName, preset });
                        break;
                    }
                    default:
                        log($"[x] unknown timeline clip subcommand: {Markup.Escape(sub2)} — expected: add, ease, preset");
                        return;
                }
                break;
            }
            case "bind":
            {
                var directorPath    = ParseStringArg(tokens, "--director");
                var trackName       = ParseStringArg(tokens, "--track");
                var targetScenePath = ParseStringArg(tokens, "--target");
                if (string.IsNullOrWhiteSpace(directorPath) || string.IsNullOrWhiteSpace(trackName) || string.IsNullOrWhiteSpace(targetScenePath))
                {
                    log("[x] usage: /timeline bind --director <path> --track <name> --target <path>");
                    return;
                }
                toolName = "timeline.bind";
                argsJson = JsonSerializer.Serialize(new { directorPath, trackName, targetScenePath });
                break;
            }
            default:
                log($"[x] unknown timeline subcommand: {Markup.Escape(sub1)} — expected: track, clip, bind");
                return;
        }

        await DispatchCustomToolAsync(session, toolName, argsJson, "timeline", log);
    }

    // ── Dispatch: project command (console, playmode, time, compile) ────

    private async Task DispatchProjectCommandAsync(
        CliSessionState session,
        ProjectCommandRequestDto dto,
        string label,
        Action<string> log)
    {
        if (DaemonControlService.GetPort(session) is not int port)
        {
            log($"[yellow]{label}[/]: daemon not running — start with /open");
            return;
        }

        log($"[grey]{label}[/]: dispatching {Markup.Escape(dto.Action)}...");
        var response = await _daemonClient.ExecuteProjectCommandAsync(port, dto,
            onStatus: status => log($"[grey]{label}[/]: {Markup.Escape(status)}"));

        if (!response.Ok)
        {
            log($"[red]{label}[/]: {Markup.Escape(dto.Action)} failed — {Markup.Escape(response.Message)}");
            return;
        }

        if (!string.IsNullOrWhiteSpace(response.Content))
        {
            RenderJsonContent(response.Content, label, log);
        }
        else
        {
            log($"[green]{label}[/]: {Markup.Escape(response.Message)}");
        }
    }

    // ── Dispatch: custom tool (profiler, recorder) ──────────────────────

    private async Task DispatchCustomToolAsync(
        CliSessionState session,
        string toolName,
        string argsJson,
        string label,
        Action<string> log)
    {
        if (DaemonControlService.GetPort(session) is not int port)
        {
            log($"[yellow]{label}[/]: daemon not running — start with /open");
            return;
        }

        log($"[grey]{label}[/]: dispatching {Markup.Escape(toolName)}...");

        try
        {
            var payload = new
            {
                operation = "execute_custom_tool",
                tool = toolName,
                args = argsJson,
                dryRun = false
            };

            var json = JsonSerializer.Serialize(payload, JsonOpts);
            using var httpContent = new StringContent(json, Encoding.UTF8, "application/json");
            var url = $"http://127.0.0.1:{port}/mcp/unifocl_project_command";

            var response = await Http.PostAsync(url, httpContent);
            var body = await response.Content.ReadAsStringAsync();

            if (string.IsNullOrWhiteSpace(body))
            {
                log($"[red]{label}[/]: empty response from daemon");
                return;
            }

            var outer = JsonSerializer.Deserialize<JsonElement>(body);

            // The MCP transport wraps the recorder/profiler JSON string inside a "result" field.
            // Unwrap it so we can inspect the actual command response.
            var result = outer;
            if (outer.TryGetProperty("result", out var resultProp)
                && resultProp.ValueKind == JsonValueKind.String)
            {
                var inner = resultProp.GetString();
                if (!string.IsNullOrWhiteSpace(inner))
                {
                    try { result = JsonSerializer.Deserialize<JsonElement>(inner); }
                    catch { /* leave result as outer if inner is not valid JSON */ }
                }
            }

            if (result.TryGetProperty("ok", out var okProp) && okProp.ValueKind == JsonValueKind.False)
            {
                var msg = result.TryGetProperty("message", out var msgProp) ? msgProp.GetString() : "failed";
                log($"[red]{label}[/]: {Markup.Escape(msg ?? "failed")}");
                return;
            }

            // In interactive TUI mode, prefer the human-friendly message when there is no embedded
            // content payload. In agentic/exec surfaces SuppressConsoleOutput is true, so the full
            // JSON is preserved for structured consumption.
            if (!CliRuntimeState.SuppressConsoleOutput)
            {
                var hasContent = result.TryGetProperty("content", out var contentEl)
                    && contentEl.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(contentEl.GetString());

                if (!hasContent && result.TryGetProperty("message", out var okMsgProp))
                {
                    var msg = okMsgProp.GetString();
                    if (!string.IsNullOrWhiteSpace(msg))
                    {
                        log($"[green]{label}[/]: {Markup.Escape(msg)}");
                        return;
                    }
                }
            }

            RenderJsonElement(result, label, log);
        }
        catch (Exception ex)
        {
            log($"[red]{label}[/]: {Markup.Escape(ex.Message)}");
        }
    }

    // ── Rendering ───────────────────────────────────────────────────────

    private static void RenderJsonContent(string content, string label, Action<string> log)
    {
        try
        {
            var doc = JsonSerializer.Deserialize<JsonElement>(content);
            RenderJsonElement(doc, label, log);
        }
        catch
        {
            log($"[green]{label}[/]: {Markup.Escape(content)}");
        }
    }

    private static void RenderJsonElement(JsonElement element, string label, Action<string> log)
    {
        var pretty = JsonSerializer.Serialize(element,
            new JsonSerializerOptions { WriteIndented = true });
        foreach (var line in pretty.Split('\n'))
        {
            log($"[{CliTheme.TextMuted}]{Markup.Escape(line)}[/]");
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static bool RequireProject(CliSessionState session, Action<string> log, string label)
    {
        if (session.Mode == CliMode.Project && !string.IsNullOrWhiteSpace(session.CurrentProjectPath))
            return true;
        log($"[yellow]{label}[/]: open a project first with /open");
        return false;
    }

    private static string NewId() => Guid.NewGuid().ToString("N");

    private static List<string> Tokenize(string input)
    {
        var tokens = new List<string>();
        var span = input.AsSpan().Trim();
        while (span.Length > 0)
        {
            if (span[0] == '"' || span[0] == '\'')
            {
                var quote = span[0];
                var end = span[1..].IndexOf(quote);
                if (end >= 0)
                {
                    tokens.Add(span[1..(end + 1)].ToString());
                    span = span[(end + 2)..].TrimStart();
                    continue;
                }
            }

            var spaceIdx = span.IndexOf(' ');
            if (spaceIdx < 0)
            {
                tokens.Add(span.ToString());
                break;
            }

            tokens.Add(span[..spaceIdx].ToString());
            span = span[(spaceIdx + 1)..].TrimStart();
        }

        return tokens;
    }

    private static (int from, int to) ParseRange(List<string> tokens)
    {
        var from = ParseIntArg(tokens, "--from");
        var to = ParseIntArg(tokens, "--to");
        return (from, to);
    }

    private static int ParseIntArg(List<string> tokens, string flag)
    {
        for (var i = 0; i < tokens.Count - 1; i++)
        {
            if (tokens[i].Equals(flag, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(tokens[i + 1], out var val))
                return val;
        }
        return -1;
    }

    private static string? ParseStringArg(List<string> tokens, string flag)
    {
        for (var i = 0; i < tokens.Count - 1; i++)
        {
            if (tokens[i].Equals(flag, StringComparison.OrdinalIgnoreCase))
                return tokens[i + 1];
        }
        return null;
    }

    private static double ParseDoubleArg(List<string> tokens, string flag, double defaultValue)
    {
        var raw = ParseStringArg(tokens, flag);
        return raw is not null
            && double.TryParse(raw, System.Globalization.NumberStyles.Any,
                               System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? v
            : defaultValue;
    }
}
