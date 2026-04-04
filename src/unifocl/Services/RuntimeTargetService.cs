using System.Net.Http;
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
