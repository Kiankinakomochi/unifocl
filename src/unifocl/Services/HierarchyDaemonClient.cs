using System.Net.Http;
using System.Text;
using System.Text.Json;

internal sealed class HierarchyDaemonClient
{
    private static readonly HttpClient Http = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ProjectCommandTimeout = TimeSpan.FromSeconds(8);
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
        var timeout = isSceneLoad ? SceneLoadTimeout : ProjectCommandTimeout;
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
                request,
                timeout,
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

                return new ProjectCommandResponseDto(false, result.Error, null);
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

                return new ProjectCommandResponseDto(false, "daemon returned an empty project response payload", null);
            }

            try
            {
                var parsed = JsonSerializer.Deserialize<ProjectCommandResponseDto>(payload, JsonOptions);
                return parsed ?? new ProjectCommandResponseDto(false, "daemon returned empty project response", null);
            }
            catch
            {
                return new ProjectCommandResponseDto(false, "daemon returned invalid project response", null);
            }
        }

        return new ProjectCommandResponseDto(false, "daemon did not return a project response", null);
    }

    private static async Task<string?> SendGetAsync(string uri)
    {
        try
        {
            using var cts = new CancellationTokenSource(DefaultRequestTimeout);
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
        Action<TimeSpan>? onProgress = null)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            var json = JsonSerializer.Serialize(payload, JsonOptions);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            var requestStarted = DateTime.UtcNow;
            var responseTask = Http.PostAsync(uri, content, cts.Token);

            while (!responseTask.IsCompleted)
            {
                await Task.Delay(SceneLoadStatusInterval, cts.Token);
                if (responseTask.IsCompleted)
                {
                    break;
                }

                onProgress?.Invoke(DateTime.UtcNow - requestStarted);
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
            return new ProjectCommandTransportResult(null, $"daemon project command timed out after {(int)timeout.TotalSeconds}s", true);
        }
        catch (Exception ex)
        {
            return new ProjectCommandTransportResult(null, $"daemon project command request failed: {ex.Message}", false);
        }
    }

    private readonly record struct ProjectCommandTransportResult(
        string? Payload,
        string? Error,
        bool TimedOut);
}
