using System.Net;

/// <summary>IExecTransportServer backed by HttpListener on loopback TCP.</summary>
internal sealed class HttpExecTransportServer : IExecTransportServer
{
    private readonly HttpListener _listener;

    public HttpExecTransportServer(int port)
    {
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
    }

    public void Start() => _listener.Start();

    public async Task<IExecRequestContext> AcceptAsync(CancellationToken ct)
    {
        var ctx = await _listener.GetContextAsync().WaitAsync(ct);
        return new HttpExecRequestContext(ctx);
    }

    public void Dispose() => _listener.Close();
}
