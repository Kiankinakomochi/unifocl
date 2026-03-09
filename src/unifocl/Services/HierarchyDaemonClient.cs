using System.Net.Http;
using System.Text;
using System.Text.Json;

internal sealed class HierarchyDaemonClient
{
    private static readonly HttpClient Http = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ProjectCommandTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan UpmMutationTimeout = TimeSpan.FromSeconds(135);
    private static readonly TimeSpan BuildStatusTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SceneLoadTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan SceneLoadStatusInterval = TimeSpan.FromSeconds(5);
    private const int SceneLoadRetryCount = 1;

    public async Task<HierarchySnapshotDto?> GetSnapshotAsync(int port)
    {
        var payload = await SendGetAsync($"http://127.0.0.1:{port}/hierarchy/snapshot");
        if (payload is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<HierarchySnapshotDto>(payload, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public async Task<HierarchyCommandResponseDto> ExecuteAsync(int port, HierarchyCommandRequestDto request)
    {
        var payload = await SendPostJsonAsync($"http://127.0.0.1:{port}/hierarchy/command", request);
        if (payload is null)
        {
            return new HierarchyCommandResponseDto(false, "daemon did not return a hierarchy response", null, null);
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<HierarchyCommandResponseDto>(payload, JsonOptions);
            return parsed ?? new HierarchyCommandResponseDto(false, "daemon returned empty hierarchy response", null, null);
        }
        catch
        {
            return new HierarchyCommandResponseDto(false, "daemon returned invalid hierarchy response", null, null);
        }
    }

    public async Task<HierarchySearchResponseDto?> SearchAsync(int port, HierarchySearchRequestDto request)
    {
        var payload = await SendPostJsonAsync($"http://127.0.0.1:{port}/hierarchy/find", request);
        if (payload is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<HierarchySearchResponseDto>(payload, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public async Task<AssetIndexSyncResponseDto?> SyncAssetIndexAsync(int port, int knownRevision)
    {
        var uri = knownRevision <= 0
            ? $"http://127.0.0.1:{port}/asset-index"
            : $"http://127.0.0.1:{port}/asset-index?revision={knownRevision}";
        var payload = await SendGetAsync(uri);
        if (payload is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<AssetIndexSyncResponseDto>(payload, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public async Task<ProjectCommandResponseDto> ExecuteProjectCommandAsync(int port, ProjectCommandRequestDto request, Action<string>? onStatus = null)
    {
        var isSceneLoad = request.Action.Equals("load-asset", StringComparison.OrdinalIgnoreCase);
        var isBuildDispatch = request.Action.StartsWith("build-", StringComparison.OrdinalIgnoreCase);
        var isUpmMutation = request.Action.Equals("upm-install", StringComparison.OrdinalIgnoreCase)
                            || request.Action.Equals("upm-remove", StringComparison.OrdinalIgnoreCase);
        var requestId = isUpmMutation ? Guid.NewGuid().ToString("N") : null;
        var requestWithId = string.IsNullOrWhiteSpace(requestId)
            ? request
            : request with { RequestId = requestId };
        var timeout = isSceneLoad
            ? SceneLoadTimeout
            : (isBuildDispatch ? TimeSpan.FromSeconds(20) : (isUpmMutation ? UpmMutationTimeout : ProjectCommandTimeout));
        var maxAttempts = isSceneLoad ? SceneLoadRetryCount + 1 : 1;
        if (isSceneLoad)
        {
            onStatus?.Invoke($"dispatching scene load request (timeout {timeout.TotalSeconds:0}s)");
        }

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (isSceneLoad)
            {
                onStatus?.Invoke($"scene load request attempt {attempt}/{maxAttempts}");
            }

            var result = await SendPostJsonWithDiagnosticsAsync(
                $"http://127.0.0.1:{port}/project/command",
                requestWithId,
                timeout,
                requestId: requestId,
                detectDaemonRestart: isUpmMutation,
                onProgress: elapsed =>
                {
                    if (isSceneLoad)
                    {
                        onStatus?.Invoke($"waiting for daemon response... {elapsed.TotalSeconds:0}s elapsed");
                    }
                });
            if (!string.IsNullOrWhiteSpace(result.Error))
            {
                var retrying = isSceneLoad && result.TimedOut && attempt < maxAttempts;
                if (retrying)
                {
                    onStatus?.Invoke("scene load timed out; retrying once");
                    continue;
                }

                return new ProjectCommandResponseDto(false, result.Error, null, null);
            }

            var payload = result.Payload;
            if (string.IsNullOrWhiteSpace(payload))
            {
                var retrying = isSceneLoad && attempt < maxAttempts;
                if (retrying)
                {
                    onStatus?.Invoke("daemon returned empty payload; retrying once");
                    continue;
                }

                return new ProjectCommandResponseDto(false, "daemon returned an empty project response payload", null, null);
            }

            try
            {
                var parsed = JsonSerializer.Deserialize<ProjectCommandResponseDto>(payload, JsonOptions);
                return parsed ?? new ProjectCommandResponseDto(false, "daemon returned empty project response", null, null);
            }
            catch
            {
                return new ProjectCommandResponseDto(false, "daemon returned invalid project response", null, null);
            }
        }

        return new ProjectCommandResponseDto(false, "daemon did not return a project response", null, null);
    }

    public async Task<BuildStatusDto?> GetBuildStatusAsync(int port)
    {
        var payload = await SendGetAsync($"http://127.0.0.1:{port}/build/status", BuildStatusTimeout);
        if (payload is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<BuildStatusDto>(payload, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public async Task<BuildLogChunkDto?> GetBuildLogChunkAsync(int port, long offset, int limit, bool errorsOnly)
    {
        var query = $"offset={Math.Max(0, offset)}&limit={Math.Clamp(limit, 1, 400)}&errorsOnly={errorsOnly.ToString().ToLowerInvariant()}";
        var payload = await SendGetAsync($"http://127.0.0.1:{port}/build/log?{query}", BuildStatusTimeout);
        if (payload is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<BuildLogChunkDto>(payload, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> SendGetAsync(string uri, TimeSpan? timeout = null)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeout ?? DefaultRequestTimeout);
            var response = await Http.GetAsync(uri, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return (await response.Content.ReadAsStringAsync(cts.Token)).Trim();
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> SendPostJsonAsync<T>(string uri, T payload)
    {
        try
        {
            using var cts = new CancellationTokenSource(DefaultRequestTimeout);
            var json = JsonSerializer.Serialize(payload, JsonOptions);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await Http.PostAsync(uri, content, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return (await response.Content.ReadAsStringAsync(cts.Token)).Trim();
        }
        catch
        {
            return null;
        }
    }

    private static async Task<ProjectCommandTransportResult> SendPostJsonWithDiagnosticsAsync<T>(
        string uri,
        T payload,
        TimeSpan timeout,
        string? requestId = null,
        bool detectDaemonRestart = false,
        Action<TimeSpan>? onProgress = null)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            var json = JsonSerializer.Serialize(payload, JsonOptions);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            var requestStarted = DateTime.UtcNow;
            var initialRuntimeId = detectDaemonRestart
                ? await ProbeDaemonRuntimeIdAsync(uri)
                : null;
            var responseTask = Http.PostAsync(uri, content, cts.Token);

            while (!responseTask.IsCompleted)
            {
                await Task.Delay(SceneLoadStatusInterval, cts.Token);
                if (responseTask.IsCompleted)
                {
                    break;
                }

                onProgress?.Invoke(DateTime.UtcNow - requestStarted);
                if (!string.IsNullOrWhiteSpace(requestId))
                {
                    var status = await ProbeProjectCommandStatusAsync(uri, requestId);
                    if (status is not null && !status.Active && !string.IsNullOrWhiteSpace(status.FinishedAtUtc))
                    {
                        if (status.Success)
                        {
                            try
                            {
                                cts.Cancel();
                            }
                            catch
                            {
                            }

                            return new ProjectCommandTransportResult(
                                null,
                                $"daemon project command completed successfully but response channel was interrupted (requestId={requestId}, stage={status.Stage}, detail={status.Detail})",
                                true);
                        }

                        if (!string.IsNullOrWhiteSpace(status.Detail))
                        {
                            return new ProjectCommandTransportResult(
                                null,
                                $"daemon project command failed before response was returned (requestId={requestId}, stage={status.Stage}, detail={status.Detail})",
                                false);
                        }
                    }
                }

                if (!detectDaemonRestart || string.IsNullOrWhiteSpace(initialRuntimeId))
                {
                    continue;
                }

                var runtimeId = await ProbeDaemonRuntimeIdAsync(uri);
                if (string.IsNullOrWhiteSpace(runtimeId))
                {
                    continue;
                }

                if (!runtimeId.Equals(initialRuntimeId, StringComparison.Ordinal))
                {
                    try
                    {
                        cts.Cancel();
                    }
                    catch
                    {
                    }

                    return new ProjectCommandTransportResult(
                        null,
                        $"daemon runtime restarted during project command (runtime={initialRuntimeId}->{runtimeId}, endpoint={uri}, hint=domain reload interrupted in-flight response; verify resulting package state)",
                        true);
                }
            }

            var response = await responseTask;
            var body = (await response.Content.ReadAsStringAsync(cts.Token)).Trim();
            if (!response.IsSuccessStatusCode)
            {
                var statusCode = (int)response.StatusCode;
                if (string.IsNullOrWhiteSpace(body))
                {
                    return new ProjectCommandTransportResult(null, $"daemon returned HTTP {statusCode} for project command", false);
                }

                return new ProjectCommandTransportResult(null, $"daemon returned HTTP {statusCode}: {body}", false);
            }

            return new ProjectCommandTransportResult(body, null, false);
        }
        catch (OperationCanceledException)
        {
            var ping = await ProbeDaemonHealthAsync(uri);
            return new ProjectCommandTransportResult(
                null,
                $"daemon project command timed out after {(int)timeout.TotalSeconds}s (endpoint={uri}, daemonPing={ping}, hint=Unity main thread may be blocked or package resolution is stuck)",
                true);
        }
        catch (Exception ex)
        {
            return new ProjectCommandTransportResult(null, $"daemon project command request failed: {ex.Message}", false);
        }
    }

    private static async Task<string> ProbeDaemonHealthAsync(string requestUri)
    {
        try
        {
            if (!Uri.TryCreate(requestUri, UriKind.Absolute, out var uri))
            {
                return "unknown";
            }

            var pingUri = $"{uri.Scheme}://{uri.Host}:{uri.Port}/ping";
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var response = await Http.GetAsync(pingUri, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                return $"http-{(int)response.StatusCode}";
            }

            var body = (await response.Content.ReadAsStringAsync(cts.Token)).Trim();
            return body.Equals("PONG", StringComparison.OrdinalIgnoreCase) ? "ok" : $"unexpected:{body}";
        }
        catch (OperationCanceledException)
        {
            return "timeout";
        }
        catch (Exception ex)
        {
            return ex.GetType().Name;
        }
    }

    private static async Task<string?> ProbeDaemonRuntimeIdAsync(string requestUri)
    {
        try
        {
            if (!Uri.TryCreate(requestUri, UriKind.Absolute, out var uri))
            {
                return null;
            }

            var runtimeUri = $"{uri.Scheme}://{uri.Host}:{uri.Port}/runtime-id";
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var response = await Http.GetAsync(runtimeUri, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = (await response.Content.ReadAsStringAsync(cts.Token)).Trim();
            return string.IsNullOrWhiteSpace(body) ? null : body;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<ProjectCommandStatusDto?> ProbeProjectCommandStatusAsync(string requestUri, string requestId)
    {
        try
        {
            if (!Uri.TryCreate(requestUri, UriKind.Absolute, out var uri))
            {
                return null;
            }

            var statusUri = $"{uri.Scheme}://{uri.Host}:{uri.Port}/project/command-status?requestId={Uri.EscapeDataString(requestId)}";
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var response = await Http.GetAsync(statusUri, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = (await response.Content.ReadAsStringAsync(cts.Token)).Trim();
            if (string.IsNullOrWhiteSpace(body))
            {
                return null;
            }

            return JsonSerializer.Deserialize<ProjectCommandStatusDto>(body, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private readonly record struct ProjectCommandTransportResult(
        string? Payload,
        string? Error,
        bool TimedOut);
}
