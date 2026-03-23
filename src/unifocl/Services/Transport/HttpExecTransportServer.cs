using System.Net;

/// <summary>IExecTransportServer backed by HttpListener on loopback TCP.</summary>
internal sealed class HttpExecTransportServer : IExecTransportServer
{
    private readonly HttpListener _listener;
    private readonly string? _requiredToken;

    /// <param name="port">TCP port to listen on (loopback only).</param>
    /// <param name="requiredToken">
    /// When non-null, every request must carry a matching <c>X-Unifocl-Token</c> header.
    /// Requests with a missing or wrong token are rejected with HTTP 401 and skipped.
    /// </param>
    public HttpExecTransportServer(int port, string? requiredToken = null)
    {
        _requiredToken = requiredToken;
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
    }

    public void Start() => _listener.Start();

    public async Task<IExecRequestContext> AcceptAsync(CancellationToken ct)
    {
        while (true)
        {
            var ctx = await _listener.GetContextAsync().WaitAsync(ct);

            if (_requiredToken is not null)
            {
                var presented = ctx.Request.Headers["X-Unifocl-Token"];
                if (presented != _requiredToken)
                {
                    ctx.Response.StatusCode = 401;
                    ctx.Response.Close();
                    continue; // wait for the next connection
                }
            }

            return new HttpExecRequestContext(ctx);
        }
    }

    public void Dispose() => _listener.Close();
}
