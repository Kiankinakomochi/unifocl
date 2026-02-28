using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

internal sealed class HierarchyDaemonClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<HierarchySnapshotDto?> GetSnapshotAsync(int port)
    {
        var payload = await SendRequestAsync(port, "HIERARCHY_GET");
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
        var json = JsonSerializer.Serialize(request, JsonOptions);
        var payload = await SendRequestAsync(port, $"HIERARCHY_CMD {json}");
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

    private static async Task<string?> SendRequestAsync(int port, string request)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await client.ConnectAsync(IPAddress.Loopback, port, cts.Token);
            await using var stream = client.GetStream();
            using var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            await writer.WriteLineAsync(request);
            return await reader.ReadLineAsync();
        }
        catch
        {
            return null;
        }
    }
}
