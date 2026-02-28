using System.Net.Http;
using System.Text;
using System.Text.Json;

internal sealed class HierarchyDaemonClient
{
    private static readonly HttpClient Http = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

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

    private static async Task<string?> SendGetAsync(string uri)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
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
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
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
}
