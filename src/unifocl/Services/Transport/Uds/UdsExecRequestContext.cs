using System.Collections.Specialized;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

/// <summary>
/// IExecRequestContext backed by a Unix Domain Socket connection.
/// Wire protocol: 4-byte big-endian uint32 length prefix + UTF-8 JSON payload.
///
/// Request envelope JSON: { "method": "POST", "path": "/agent/exec", "query": "k=v&k2=v2", "body": "..." }
/// Response envelope JSON: { "statusCode": 200, "contentType": "application/json", "body": "..." }
/// </summary>
internal sealed class UdsExecRequestContext : IExecRequestContext
{
    private readonly Socket _socket;
    private readonly UdsRequestEnvelope _envelope;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private UdsExecRequestContext(Socket socket, UdsRequestEnvelope envelope)
    {
        _socket = socket;
        _envelope = envelope;
        Method = envelope.Method ?? "GET";
        Path = (envelope.Path ?? "/").TrimEnd('/');
        if (Path.Length == 0)
        {
            Path = "/";
        }

        Query = ParseQuery(envelope.Query);
    }

    public string Method { get; }
    public string Path { get; }
    public System.Collections.Specialized.NameValueCollection Query { get; }
    public bool IsInternal => true;

    public Task<string> ReadBodyAsync(CancellationToken ct = default)
        => Task.FromResult(_envelope.Body ?? string.Empty);

    public async Task WriteJsonAsync(string json, int statusCode = 200, CancellationToken ct = default)
        => await WriteResponseAsync(statusCode, "application/json", json, ct);

    public async Task WriteTextAsync(string text, int statusCode = 200, CancellationToken ct = default)
        => await WriteResponseAsync(statusCode, "text/plain", text, ct);

    private async Task WriteResponseAsync(int statusCode, string contentType, string body, CancellationToken ct)
    {
        var envelope = new UdsResponseEnvelope(statusCode, contentType, body);
        var json = JsonSerializer.Serialize(envelope, JsonOptions);
        var payload = Encoding.UTF8.GetBytes(json);

        var lengthBytes = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(lengthBytes, (uint)payload.Length);

        using var stream = new NetworkStream(_socket, ownsSocket: true);
        await stream.WriteAsync(lengthBytes.AsMemory(), ct);
        await stream.WriteAsync(payload.AsMemory(), ct);
        await stream.FlushAsync(ct);
    }

    /// <summary>
    /// Reads a length-prefixed JSON request envelope from the socket.
    /// Returns null if the connection was closed cleanly with no data.
    /// </summary>
    public static async Task<UdsExecRequestContext?> ReadAsync(Socket socket, CancellationToken ct)
    {
        using var stream = new NetworkStream(socket, ownsSocket: false);

        var lengthBuf = new byte[4];
        var read = 0;
        while (read < 4)
        {
            var n = await stream.ReadAsync(lengthBuf.AsMemory(read, 4 - read), ct);
            if (n == 0)
            {
                return null; // connection closed
            }

            read += n;
        }

        var length = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(lengthBuf);
        if (length == 0 || length > 8 * 1024 * 1024) // sanity: 8 MB max
        {
            return null;
        }

        var payloadBuf = new byte[length];
        read = 0;
        while (read < length)
        {
            var n = await stream.ReadAsync(payloadBuf.AsMemory(read, length - read), ct);
            if (n == 0)
            {
                return null;
            }

            read += n;
        }

        UdsRequestEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<UdsRequestEnvelope>(payloadBuf, JsonOptions);
        }
        catch
        {
            envelope = null;
        }

        if (envelope is null)
        {
            return null;
        }

        return new UdsExecRequestContext(socket, envelope);
    }

    private static NameValueCollection ParseQuery(string? raw)
    {
        var col = new NameValueCollection();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return col;
        }

        foreach (var part in raw.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq < 0)
            {
                col[Uri.UnescapeDataString(part)] = string.Empty;
            }
            else
            {
                col[Uri.UnescapeDataString(part[..eq])] = Uri.UnescapeDataString(part[(eq + 1)..]);
            }
        }

        return col;
    }

    internal sealed record UdsRequestEnvelope(string? Method, string? Path, string? Query, string? Body);
    internal sealed record UdsResponseEnvelope(int StatusCode, string ContentType, string Body);
}
