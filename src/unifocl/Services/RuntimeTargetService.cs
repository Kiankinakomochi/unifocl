using System.Net.Http;
using System.Text;
using System.Text.Json;

/// <summary>
/// CLI-side service for runtime target operations.
/// Sends HTTP requests directly to the daemon's /runtime/* endpoints,
/// which delegate to <c>DaemonRuntimeBridge</c> on the Unity editor side.
/// </summary>
internal sealed class RuntimeTargetService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ExecSessionService _sessions;

    public RuntimeTargetService(ExecSessionService sessions)
    {
        _sessions = sessions;
    }

    /// <summary>
    /// Handle a runtime.* ExecV2 operation by dispatching to the daemon's HTTP endpoints.
    /// </summary>
    public async Task<ExecV2Response> HandleAsync(
        ExecV2Request request,
        CancellationToken cancellationToken = default)
    {
        var session = _sessions.Get(request.SessionId);
        if (session is null || string.IsNullOrWhiteSpace(session.ProjectPath))
        {
            return Rejected(request.RequestId,
                "runtime operations require an active session with a projectPath. " +
                "Open a session first with operation 'session.open'.");
        }

        var port = DebugArtifactService.ResolveDaemonPort(session.ProjectPath);
        if (port <= 0)
        {
            return new ExecV2Response(
                ExecV2Status.Failed,
                request.RequestId,
                Message: "no daemon found for the active project. Ensure the editor is open and the daemon is running.");
        }

        return request.Operation.ToLowerInvariant() switch
        {
            "runtime.target.list" => await HandleTargetListAsync(request, port, cancellationToken),
            "runtime.attach" => await HandleAttachAsync(request, port, cancellationToken),
            "runtime.status" => await HandleStatusAsync(request, port, cancellationToken),
            "runtime.detach" => await HandleDetachAsync(request, port, cancellationToken),
            // S2: manifest
            "runtime.manifest" => await HandleManifestAsync(request, port, cancellationToken),
            // S3: query + exec
            "runtime.query" or "runtime.exec" => await HandleRuntimeExecAsync(request, port, cancellationToken),
            // S4: durable jobs
            "runtime.job.submit" => await HandleJobSubmitAsync(request, port, cancellationToken),
            "runtime.job.status" => await HandleJobStatusAsync(request, port, cancellationToken),
            "runtime.job.cancel" => await HandleJobCancelAsync(request, port, cancellationToken),
            "runtime.job.list" => await HandleJobListAsync(request, port, cancellationToken),
            // S5: streams + watches
            "runtime.stream.subscribe" => await HandleStreamSubscribeAsync(request, port, cancellationToken),
            "runtime.stream.unsubscribe" => await HandleStreamUnsubscribeAsync(request, port, cancellationToken),
            "runtime.watch.add" => await HandleWatchAddAsync(request, port, cancellationToken),
            "runtime.watch.remove" => await HandleWatchRemoveAsync(request, port, cancellationToken),
            "runtime.watch.list" => await HandleWatchListAsync(request, port, cancellationToken),
            "runtime.watch.poll" => await HandleWatchPollAsync(request, port, cancellationToken),
            // S6: scenario files
            "runtime.scenario.run" => await HandleScenarioRunAsync(request, port, cancellationToken),
            "runtime.scenario.list" => HandleScenarioList(request),
            "runtime.scenario.validate" => HandleScenarioValidate(request),
            _ => Rejected(request.RequestId, $"unknown runtime operation: {request.Operation}")
        };
    }

    private async Task<ExecV2Response> HandleTargetListAsync(
        ExecV2Request request, int port, CancellationToken ct)
    {
        var json = await SendGetAsync($"http://127.0.0.1:{port}/runtime/targets", ct);
        return ParseDaemonResponse(request.RequestId, json);
    }

    private async Task<ExecV2Response> HandleAttachAsync(
        ExecV2Request request, int port, CancellationToken ct)
    {
        var target = GetString(request.Args, "target") ?? "editor:playmode";
        var url = $"http://127.0.0.1:{port}/runtime/attach?target={Uri.EscapeDataString(target)}";
        var json = await SendPostAsync(url, ct);
        return ParseDaemonResponse(request.RequestId, json);
    }

    private async Task<ExecV2Response> HandleStatusAsync(
        ExecV2Request request, int port, CancellationToken ct)
    {
        var json = await SendGetAsync($"http://127.0.0.1:{port}/runtime/status", ct);
        return ParseDaemonResponse(request.RequestId, json);
    }

    private async Task<ExecV2Response> HandleDetachAsync(
        ExecV2Request request, int port, CancellationToken ct)
    {
        var json = await SendPostAsync($"http://127.0.0.1:{port}/runtime/detach", ct);
        return ParseDaemonResponse(request.RequestId, json);
    }

    // ── S2: Manifest ────────────────────────────────────────────────────

    private async Task<ExecV2Response> HandleManifestAsync(
        ExecV2Request request, int port, CancellationToken ct)
    {
        var json = await SendGetAsync($"http://127.0.0.1:{port}/runtime/manifest", ct);
        return ParseDaemonResponse(request.RequestId, json);
    }

    // ── S3: Query + Command Execution ───────────────────────────────────

    private async Task<ExecV2Response> HandleRuntimeExecAsync(
        ExecV2Request request, int port, CancellationToken ct)
    {
        var command = GetString(request.Args, "command") ?? "";
        var argsJson = "{}";
        if (request.Args.HasValue && request.Args.Value.TryGetProperty("args", out var argsProp))
        {
            argsJson = argsProp.GetRawText();
        }

        var body = JsonSerializer.Serialize(new { command, argsJson }, JsonOptions);
        var json = await SendPostWithBodyAsync($"http://127.0.0.1:{port}/runtime/exec", body, ct);
        return ParseDaemonResponse(request.RequestId, json);
    }

    // ── S4: Durable Jobs ────────────────────────────────────────────────

    private async Task<ExecV2Response> HandleJobSubmitAsync(
        ExecV2Request request, int port, CancellationToken ct)
    {
        var command = GetString(request.Args, "command") ?? "";
        var argsJson = "{}";
        if (request.Args.HasValue && request.Args.Value.TryGetProperty("args", out var argsProp))
        {
            argsJson = argsProp.GetRawText();
        }
        var timeoutMs = 60000;
        if (request.Args.HasValue && request.Args.Value.TryGetProperty("timeoutMs", out var tProp) && tProp.ValueKind == JsonValueKind.Number)
        {
            timeoutMs = tProp.GetInt32();
        }

        var body = JsonSerializer.Serialize(new { command, argsJson, timeoutMs }, JsonOptions);
        var json = await SendPostWithBodyAsync($"http://127.0.0.1:{port}/runtime/job/submit", body, ct);
        return ParseDaemonResponse(request.RequestId, json);
    }

    private async Task<ExecV2Response> HandleJobStatusAsync(
        ExecV2Request request, int port, CancellationToken ct)
    {
        var jobId = GetString(request.Args, "jobId") ?? "";
        var json = await SendGetAsync($"http://127.0.0.1:{port}/runtime/job/status?jobId={Uri.EscapeDataString(jobId)}", ct);
        return ParseDaemonResponse(request.RequestId, json);
    }

    private async Task<ExecV2Response> HandleJobCancelAsync(
        ExecV2Request request, int port, CancellationToken ct)
    {
        var jobId = GetString(request.Args, "jobId") ?? "";
        var json = await SendPostAsync($"http://127.0.0.1:{port}/runtime/job/cancel?jobId={Uri.EscapeDataString(jobId)}", ct);
        return ParseDaemonResponse(request.RequestId, json);
    }

    private async Task<ExecV2Response> HandleJobListAsync(
        ExecV2Request request, int port, CancellationToken ct)
    {
        var json = await SendGetAsync($"http://127.0.0.1:{port}/runtime/job/list", ct);
        return ParseDaemonResponse(request.RequestId, json);
    }

    // ── S5: Streams + Watches ───────────────────────────────────────────

    private async Task<ExecV2Response> HandleStreamSubscribeAsync(
        ExecV2Request request, int port, CancellationToken ct)
    {
        var channel = GetString(request.Args, "channel") ?? "";
        var filterJson = "{}";
        if (request.Args.HasValue && request.Args.Value.TryGetProperty("filter", out var fProp))
        {
            filterJson = fProp.GetRawText();
        }

        var body = JsonSerializer.Serialize(new { channel, filterJson }, JsonOptions);
        var json = await SendPostWithBodyAsync($"http://127.0.0.1:{port}/runtime/stream/subscribe", body, ct);
        return ParseDaemonResponse(request.RequestId, json);
    }

    private async Task<ExecV2Response> HandleStreamUnsubscribeAsync(
        ExecV2Request request, int port, CancellationToken ct)
    {
        var subId = GetString(request.Args, "subscriptionId") ?? "";
        var json = await SendPostAsync($"http://127.0.0.1:{port}/runtime/stream/unsubscribe?subscriptionId={Uri.EscapeDataString(subId)}", ct);
        return ParseDaemonResponse(request.RequestId, json);
    }

    private async Task<ExecV2Response> HandleWatchAddAsync(
        ExecV2Request request, int port, CancellationToken ct)
    {
        var expression = GetString(request.Args, "expression") ?? "";
        var target = GetString(request.Args, "target") ?? "";
        var intervalMs = 1000;
        if (request.Args.HasValue && request.Args.Value.TryGetProperty("intervalMs", out var iProp) && iProp.ValueKind == JsonValueKind.Number)
        {
            intervalMs = iProp.GetInt32();
        }

        var body = JsonSerializer.Serialize(new { expression, target, intervalMs }, JsonOptions);
        var json = await SendPostWithBodyAsync($"http://127.0.0.1:{port}/runtime/watch/add", body, ct);
        return ParseDaemonResponse(request.RequestId, json);
    }

    private async Task<ExecV2Response> HandleWatchRemoveAsync(
        ExecV2Request request, int port, CancellationToken ct)
    {
        var watchId = GetString(request.Args, "watchId") ?? "";
        var json = await SendPostAsync($"http://127.0.0.1:{port}/runtime/watch/remove?watchId={Uri.EscapeDataString(watchId)}", ct);
        return ParseDaemonResponse(request.RequestId, json);
    }

    private async Task<ExecV2Response> HandleWatchListAsync(
        ExecV2Request request, int port, CancellationToken ct)
    {
        var json = await SendGetAsync($"http://127.0.0.1:{port}/runtime/watch/list", ct);
        return ParseDaemonResponse(request.RequestId, json);
    }

    private async Task<ExecV2Response> HandleWatchPollAsync(
        ExecV2Request request, int port, CancellationToken ct)
    {
        var json = await SendGetAsync($"http://127.0.0.1:{port}/runtime/watch/poll", ct);
        return ParseDaemonResponse(request.RequestId, json);
    }

    // ── S6: Scenario Files ──────────────────────────────────────────────

    private async Task<ExecV2Response> HandleScenarioRunAsync(
        ExecV2Request request, int port, CancellationToken ct)
    {
        var session = _sessions.Get(request.SessionId);
        var projectPath = session?.ProjectPath ?? "";
        var scenarioPath = GetString(request.Args, "path") ?? "";
        if (string.IsNullOrWhiteSpace(scenarioPath))
        {
            return Rejected(request.RequestId, "runtime.scenario.run requires args.path");
        }

        var fullPath = Path.IsPathRooted(scenarioPath)
            ? scenarioPath
            : Path.Combine(projectPath, scenarioPath);

        if (!File.Exists(fullPath))
        {
            return Rejected(request.RequestId, $"scenario file not found: {fullPath}");
        }

        var scenarioService = new RuntimeScenarioService();
        var result = await scenarioService.RunAsync(fullPath, port, ct);
        return new ExecV2Response(
            result.AllPassed ? ExecV2Status.Completed : ExecV2Status.Failed,
            request.RequestId,
            Message: result.Summary,
            Result: result);
    }

    private ExecV2Response HandleScenarioList(ExecV2Request request)
    {
        var session = _sessions.Get(request.SessionId);
        var projectPath = session?.ProjectPath ?? "";
        var scenarioDir = Path.Combine(projectPath, ".unifocl", "scenarios");
        if (!Directory.Exists(scenarioDir))
        {
            return new ExecV2Response(ExecV2Status.Completed, request.RequestId,
                Message: "no scenario directory found",
                Result: new { scenarios = Array.Empty<string>() });
        }

        var files = Directory.GetFiles(scenarioDir, "*.yaml", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(scenarioDir, "*.yml", SearchOption.AllDirectories))
            .Select(f => Path.GetRelativePath(projectPath, f).Replace('\\', '/'))
            .OrderBy(f => f)
            .ToArray();

        return new ExecV2Response(ExecV2Status.Completed, request.RequestId,
            Message: $"{files.Length} scenario(s) found",
            Result: new { scenarios = files });
    }

    private ExecV2Response HandleScenarioValidate(ExecV2Request request)
    {
        var session = _sessions.Get(request.SessionId);
        var projectPath = session?.ProjectPath ?? "";
        var scenarioPath = GetString(request.Args, "path") ?? "";
        if (string.IsNullOrWhiteSpace(scenarioPath))
        {
            return Rejected(request.RequestId, "runtime.scenario.validate requires args.path");
        }

        var fullPath = Path.IsPathRooted(scenarioPath)
            ? scenarioPath
            : Path.Combine(projectPath, scenarioPath);

        if (!File.Exists(fullPath))
        {
            return Rejected(request.RequestId, $"scenario file not found: {fullPath}");
        }

        var scenarioService = new RuntimeScenarioService();
        var (valid, errors) = scenarioService.Validate(fullPath);
        return new ExecV2Response(
            valid ? ExecV2Status.Completed : ExecV2Status.Failed,
            request.RequestId,
            Message: valid ? "scenario is valid" : $"{errors.Count} validation error(s)",
            Result: new { valid, errors });
    }

    private static async Task<string?> SendGetAsync(string url, CancellationToken ct)
    {
        try
        {
            using var response = await Http.GetAsync(url, ct);
            return await response.Content.ReadAsStringAsync(ct);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> SendPostAsync(string url, CancellationToken ct)
    {
        try
        {
            using var response = await Http.PostAsync(url, null, ct);
            return await response.Content.ReadAsStringAsync(ct);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> SendPostWithBodyAsync(string url, string body, CancellationToken ct)
    {
        try
        {
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await Http.PostAsync(url, content, ct);
            return await response.Content.ReadAsStringAsync(ct);
        }
        catch
        {
            return null;
        }
    }

    private static ExecV2Response ParseDaemonResponse(string requestId, string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return new ExecV2Response(
                ExecV2Status.Failed,
                requestId,
                Message: "daemon did not respond to runtime request");
        }

        try
        {
            var result = JsonSerializer.Deserialize<JsonElement>(json, JsonOptions);
            var ok = result.TryGetProperty("ok", out var okProp) && okProp.GetBoolean();
            var message = result.TryGetProperty("message", out var msgProp)
                ? msgProp.GetString()
                : null;

            return new ExecV2Response(
                ok ? ExecV2Status.Completed : ExecV2Status.Failed,
                requestId,
                Message: message,
                Result: result);
        }
        catch
        {
            return new ExecV2Response(
                ExecV2Status.Failed,
                requestId,
                Message: "failed to parse daemon response");
        }
    }

    private static ExecV2Response Rejected(string requestId, string message)
        => new(ExecV2Status.Rejected, requestId, Message: message);

    private static string? GetString(JsonElement? args, string key)
    {
        if (!args.HasValue) return null;
        return args.Value.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
    }
}
