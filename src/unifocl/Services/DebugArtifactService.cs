using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;

/// <summary>
/// Composite orchestrator that collects tiered debug artifacts by dispatching
/// multiple sub-operations to the daemon via HTTP.
///
/// Dispatch paths:
///   - Standard ops (console, validate, build, diag, hierarchy, settings, compile):
///     POST /project/command with ProjectCommandRequestDto
///   - Custom tool ops (profiling.*, recorder.*):
///     POST /mcp/unifocl_project_command with execute_custom_tool payload
/// </summary>
internal sealed class DebugArtifactService
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions WriteOpts = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>
    /// Collect a debug artifact at the requested tier level via daemon HTTP endpoints.
    /// </summary>
    /// <param name="tier">0-3 collection depth.</param>
    /// <param name="daemonPort">Daemon HTTP port.</param>
    /// <param name="ticketMeta">Optional ticket metadata stub.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<DebugArtifact> CollectAsync(
        int tier,
        int daemonPort,
        DebugArtifactTicketMeta? ticketMeta,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var errors = new List<DebugArtifactCollectionError>();

        if (daemonPort <= 0)
        {
            errors.Add(new DebugArtifactCollectionError("daemon", "no daemon port available"));
            sw.Stop();
            return new DebugArtifact("1.0", DateTime.UtcNow.ToString("O"), tier,
                Math.Round(sw.Elapsed.TotalMilliseconds, 1), ticketMeta,
                new DebugArtifactEnvironment(), null, null, null, null, null, errors);
        }

        // ── T0: environment (always) ────────────────────────────────────
        var settings = await SafeProjectCommandAsync(daemonPort, "settings-inspect", null, errors, "settings inspect", ct);
        var compileStatus = await SafeProjectCommandAsync(daemonPort, "compile-status", null, errors, "compile.status", ct);
        var environment = new DebugArtifactEnvironment(settings, compileStatus);

        // ── T1: logs + validation ───────────────────────────────────────
        DebugArtifactLogs? logs = null;
        DebugArtifactValidation? validation = null;
        if (tier >= 1)
        {
            var consoleErrors = await SafeProjectCommandAsync(
                daemonPort, "console-dump",
                JsonSerializer.Serialize(new { type = "error", limit = 500 }),
                errors, "console dump (errors)", ct);
            var consoleWarnings = await SafeProjectCommandAsync(
                daemonPort, "console-dump",
                JsonSerializer.Serialize(new { type = "warning", limit = 200 }),
                errors, "console dump (warnings)", ct);
            var compileErrors = await SafeProjectCommandAsync(
                daemonPort, "diag-compile-errors", null, errors, "diag.compile-errors", ct);
            logs = new DebugArtifactLogs(consoleErrors, consoleWarnings, compileErrors);

            var sceneList = await SafeProjectCommandAsync(daemonPort, "validate-scene-list", null, errors, "validate.scene-list", ct);
            var missingScripts = await SafeProjectCommandAsync(daemonPort, "validate-missing-scripts", null, errors, "validate.missing-scripts", ct);
            var packages = await SafeProjectCommandAsync(daemonPort, "validate-packages", null, errors, "validate.packages", ct);
            var buildSettings = await SafeProjectCommandAsync(daemonPort, "validate-build-settings", null, errors, "validate.build-settings", ct);
            var asmdef = await SafeProjectCommandAsync(daemonPort, "validate-asmdef", null, errors, "validate.asmdef", ct);
            var assetRefs = await SafeProjectCommandAsync(daemonPort, "validate-asset-refs", null, errors, "validate.asset-refs", ct);
            validation = new DebugArtifactValidation(sceneList, missingScripts, packages, buildSettings, asmdef, assetRefs);
        }

        // ── T2: state dumps + perf summary ──────────────────────────────
        DebugArtifactStateDumps? stateDumps = null;
        DebugArtifactPerformance? performance = null;
        if (tier >= 2)
        {
            var hierarchy = await SafeProjectCommandAsync(daemonPort, "hierarchy-snapshot", null, errors, "hierarchy.snapshot", ct);
            var buildReport = await SafeProjectCommandAsync(daemonPort, "build-report", null, errors, "build.report", ct);
            var buildMeta = await SafeProjectCommandAsync(daemonPort, "build-artifact-metadata", null, errors, "build.artifact-metadata", ct);
            stateDumps = new DebugArtifactStateDumps(hierarchy, buildReport, buildMeta);

            var profilerInspect = await SafeCustomToolAsync(daemonPort, "profiling.inspect", "{}", errors, "profiling.inspect", ct);
            var frameTiming = await SafeCustomToolAsync(daemonPort, "profiling.frame_timing", "{}", errors, "profiling.frame_timing", ct);
            performance = new DebugArtifactPerformance(profilerInspect, frameTiming);
        }

        // ── T3: detailed perf + media ───────────────────────────────────
        DebugArtifactMedia? media = null;
        if (tier >= 3)
        {
            var (from, to) = ExtractFrameRange(performance?.ProfilerInspect);

            JsonElement? frames = null;
            JsonElement? gcAlloc = null;
            JsonElement? markers = null;
            string? exportPath = null;

            if (from >= 0 && to > from)
            {
                var rangeArgs = JsonSerializer.Serialize(new { from, to });
                frames = await SafeCustomToolAsync(daemonPort, "profiling.frames", rangeArgs, errors, "profiling.frames", ct);
                gcAlloc = await SafeCustomToolAsync(daemonPort, "profiling.gc_alloc", rangeArgs, errors, "profiling.gc_alloc", ct);
                markers = await SafeCustomToolAsync(daemonPort, "profiling.markers", rangeArgs, errors, "profiling.markers", ct);
            }
            else
            {
                errors.Add(new DebugArtifactCollectionError(
                    "profiling.frames/gc_alloc/markers",
                    "no valid frame range from profiling.inspect"));
            }

            var summaryResult = await SafeCustomToolAsync(
                daemonPort, "profiling.export_summary",
                JsonSerializer.Serialize(new { path = ".unifocl-runtime/artifacts/profiler-summary.json" }),
                errors, "profiling.export_summary", ct);
            if (summaryResult.HasValue)
                exportPath = TryExtractString(summaryResult.Value, "path");

            performance = (performance ?? new DebugArtifactPerformance()) with
            {
                Frames = frames,
                GcAlloc = gcAlloc,
                Markers = markers,
                ExportSummaryPath = exportPath
            };

            var recorderStatus = await SafeCustomToolAsync(daemonPort, "recorder.status", "{}", errors, "recorder.status", ct);

            string? snapshotPath = null;
            var snapshotResult = await SafeCustomToolAsync(
                daemonPort, "profiling.take_snapshot",
                JsonSerializer.Serialize(new { path = ".unifocl-runtime/artifacts/memory-snapshot.snap" }),
                errors, "profiling.take_snapshot", ct);
            if (snapshotResult.HasValue)
                snapshotPath = TryExtractString(snapshotResult.Value, "path");

            media = new DebugArtifactMedia(recorderStatus, snapshotPath);
        }

        sw.Stop();
        return new DebugArtifact(
            ArtifactVersion: "1.0",
            CollectedAtUtc: DateTime.UtcNow.ToString("O"),
            Tier: tier,
            CollectionDurationMs: Math.Round(sw.Elapsed.TotalMilliseconds, 1),
            TicketMeta: ticketMeta,
            Environment: environment,
            Logs: logs,
            Validation: validation,
            StateDumps: stateDumps,
            Performance: performance,
            Media: media,
            Errors: errors);
    }

    /// <summary>
    /// Prepare the project for debug artifact collection at the given tier.
    /// Starts profiler, recorder, and clears the console as appropriate.
    ///
    /// Tier 0-1: clears console (clean log baseline).
    /// Tier 2:   + starts profiler recording.
    /// Tier 3:   + starts profiler with deep profiling + starts recorder.
    ///
    /// After prep, the caller should enter playmode, reproduce the issue,
    /// exit playmode, then call CollectAsync.
    /// </summary>
    public async Task<DebugArtifactPrepResult> PrepAsync(
        int tier,
        int daemonPort,
        CancellationToken ct)
    {
        var errors = new List<DebugArtifactCollectionError>();

        if (daemonPort <= 0)
        {
            errors.Add(new DebugArtifactCollectionError("daemon", "no daemon port available"));
            return new DebugArtifactPrepResult(false, tier, false, false, false, errors,
                "fix daemon connection before retrying");
        }

        // All tiers: clear console for a clean log baseline
        var clearResult = await SafeProjectCommandAsync(
            daemonPort, "console-clear", null, errors, "console clear", ct);
        var consoleCleared = clearResult.HasValue;

        // T2+: start profiler
        var profilerStarted = false;
        if (tier >= 2)
        {
            var deep = tier >= 3;
            var profilerArgs = JsonSerializer.Serialize(new { deep, editor = false, keepFrames = false });
            var profilerResult = await SafeCustomToolAsync(
                daemonPort, "profiling.start_recording", profilerArgs, errors, "profiling.start_recording", ct);
            profilerStarted = profilerResult.HasValue
                && !(profilerResult.Value.TryGetProperty("ok", out var okP) && okP.ValueKind == JsonValueKind.False);
        }

        // T3: start recorder
        var recorderStarted = false;
        if (tier >= 3)
        {
            var recorderResult = await SafeCustomToolAsync(
                daemonPort, "recorder.start", "{}", errors, "recorder.start", ct);
            recorderStarted = recorderResult.HasValue
                && !(recorderResult.Value.TryGetProperty("ok", out var okP) && okP.ValueKind == JsonValueKind.False);
        }

        var allOk = errors.Count == 0;
        var nextStep = tier >= 2
            ? "enter playmode, reproduce the issue, then run: /playmode stop && /profiler stop"
              + (tier >= 3 ? " && /recorder stop" : "")
              + " && /debug-artifact collect --tier " + tier
            : "enter playmode, reproduce the issue, then run: /playmode stop && /debug-artifact collect --tier " + tier;

        return new DebugArtifactPrepResult(allOk, tier, profilerStarted, recorderStarted, consoleCleared, errors, nextStep);
    }

    /// <summary>
    /// Persist artifact JSON to .unifocl-runtime/artifacts/ and return the output path.
    /// </summary>
    public string PersistArtifact(DebugArtifact artifact, string projectPath)
    {
        var dir = Path.Combine(projectPath, ".unifocl-runtime", "artifacts");
        Directory.CreateDirectory(dir);
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var filePath = Path.Combine(dir, $"{timestamp}-debug-artifact.json");
        var json = JsonSerializer.Serialize(artifact, WriteOpts);
        File.WriteAllText(filePath, json);
        return filePath;
    }

    /// <summary>
    /// Resolve daemon port from runtime registry for a project path.
    /// </summary>
    public static int ResolveDaemonPort(string projectPath)
    {
        var runtimeRoot = Path.Combine(projectPath, ".unifocl-runtime");
        var runtime = new DaemonRuntime(runtimeRoot);
        var daemon = runtime.GetAll().FirstOrDefault();
        if (daemon is not null) return daemon.Port;

        var globalRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".unifocl-runtime");
        var globalRuntime = new DaemonRuntime(globalRoot);
        daemon = globalRuntime.GetAll().FirstOrDefault();
        return daemon?.Port ?? 0;
    }

    // ── Standard project command dispatch via POST /project/command ──

    private static async Task<JsonElement?> SafeProjectCommandAsync(
        int port,
        string action,
        string? content,
        List<DebugArtifactCollectionError> errors,
        string label,
        CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var dto = new ProjectCommandRequestDto(action, null, null, content, Guid.NewGuid().ToString("N"));
            var json = JsonSerializer.Serialize(dto, JsonOpts);
            using var httpContent = new StringContent(json, Encoding.UTF8, "application/json");
            var url = $"http://127.0.0.1:{port}/project/command";

            var response = await Http.PostAsync(url, httpContent, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (string.IsNullOrWhiteSpace(body))
            {
                errors.Add(new DebugArtifactCollectionError(label, "empty response from daemon"));
                return null;
            }

            var result = JsonSerializer.Deserialize<ProjectCommandResponseDto>(body, JsonOpts);
            if (result?.Ok != true)
            {
                errors.Add(new DebugArtifactCollectionError(label, result?.Message ?? "failed"));
                return null;
            }

            if (!string.IsNullOrEmpty(result.Content))
                return JsonSerializer.Deserialize<JsonElement>(result.Content);

            return JsonSerializer.Deserialize<JsonElement>(
                JsonSerializer.Serialize(new { ok = true, message = result.Message }, JsonOpts));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            errors.Add(new DebugArtifactCollectionError(label, ex.Message));
            return null;
        }
    }

    // ── Custom tool dispatch via POST /mcp/unifocl_project_command ──

    private static async Task<JsonElement?> SafeCustomToolAsync(
        int port,
        string toolName,
        string argsJson,
        List<DebugArtifactCollectionError> errors,
        string label,
        CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
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

            var response = await Http.PostAsync(url, httpContent, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            var result = JsonSerializer.Deserialize<JsonElement>(body);

            if (result.TryGetProperty("ok", out var okProp) && okProp.ValueKind == JsonValueKind.False)
            {
                var msg = result.TryGetProperty("message", out var msgProp) ? msgProp.GetString() : "failed";
                errors.Add(new DebugArtifactCollectionError(label, msg ?? "failed"));
                return null;
            }

            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            errors.Add(new DebugArtifactCollectionError(label, ex.Message));
            return null;
        }
    }

    // ── Helpers ──

    private static (int from, int to) ExtractFrameRange(JsonElement? profilerInspect)
    {
        if (profilerInspect is null)
            return (-1, -1);

        try
        {
            var root = profilerInspect.Value;

            if (root.TryGetProperty("firstFrameIndex", out var first)
                && root.TryGetProperty("lastFrameIndex", out var last))
                return (first.GetInt32(), last.GetInt32());

            if (root.TryGetProperty("frameRange", out var range)
                && range.TryGetProperty("first", out var f)
                && range.TryGetProperty("last", out var l))
                return (f.GetInt32(), l.GetInt32());
        }
        catch { /* fall through */ }

        return (-1, -1);
    }

    private static string? TryExtractString(JsonElement element, string key)
    {
        try
        {
            if (element.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.String)
                return prop.GetString();
        }
        catch { /* ignore */ }

        return null;
    }
}
