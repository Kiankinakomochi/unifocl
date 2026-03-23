using System.Net.Sockets;

/// <summary>
/// IExecTransportServer backed by a Unix Domain Socket.
/// Binds to a .sock file path. On each accepted connection, reads one request envelope and returns
/// the context. The connection is closed after the response is written.
///
/// Security benefit over HttpListener:
/// - Not reachable from browser or other non-OS-process callers
/// - Access controlled by filesystem permissions on the socket file
/// - Loopback TCP attack surface eliminated when HTTP is also disabled
/// </summary>
internal sealed class UdsExecTransportServer : IExecTransportServer
{
    private readonly string _socketPath;
    private Socket? _server;

    public UdsExecTransportServer(string socketPath)
    {
        _socketPath = socketPath;
    }

    public void Start()
    {
        if (File.Exists(_socketPath))
        {
            File.Delete(_socketPath);
        }

        var directory = Path.GetDirectoryName(_socketPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _server = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _server.Bind(new UnixDomainSocketEndPoint(_socketPath));
        _server.Listen(backlog: 16);
    }

    public async Task<IExecRequestContext> AcceptAsync(CancellationToken ct)
    {
        if (_server is null)
        {
            throw new InvalidOperationException("Call Start() before AcceptAsync()");
        }

        while (true)
        {
            var client = await _server.AcceptAsync(ct);
            var ctx = await UdsExecRequestContext.ReadAsync(client, ct);
            if (ctx is not null)
            {
                return ctx;
            }

            // Malformed or empty connection — close and wait for next
            client.Dispose();
        }
    }

    public void Dispose()
    {
        _server?.Dispose();
        try
        {
            if (File.Exists(_socketPath))
            {
                File.Delete(_socketPath);
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }
}
