using System.Collections.Specialized;
using System.Net;
using System.Text;

/// <summary>Thrown when an HTTP request body exceeds the configured size limit.</summary>
internal sealed class RequestTooLargeException : Exception
{
    public RequestTooLargeException() : base("Request body exceeds 1 MB limit.") { }
}

/// <summary>IExecRequestContext backed by HttpListenerContext.</summary>
internal sealed class HttpExecRequestContext : IExecRequestContext
{
    private readonly HttpListenerContext _ctx;

    public HttpExecRequestContext(HttpListenerContext ctx)
    {
        _ctx = ctx;
        Path = (ctx.Request.Url?.AbsolutePath ?? "/").TrimEnd('/');
        if (Path.Length == 0)
        {
            Path = "/";
        }
    }

    public string Method => _ctx.Request.HttpMethod;
    public string Path { get; }
    public NameValueCollection Query => _ctx.Request.QueryString;
    public bool IsInternal => false;

    private const long MaxBodyBytes = 1 * 1024 * 1024; // 1 MB

    public async Task<string> ReadBodyAsync(CancellationToken ct = default)
    {
        if (_ctx.Request.ContentLength64 > MaxBodyBytes)
        {
            throw new RequestTooLargeException();
        }

        var encoding = _ctx.Request.ContentEncoding ?? Encoding.UTF8;
        var buffer = new byte[MaxBodyBytes + 1];
        var totalRead = 0;
        int bytesRead;
        while ((bytesRead = await _ctx.Request.InputStream.ReadAsync(buffer.AsMemory(totalRead), ct)) > 0)
        {
            totalRead += bytesRead;
            if (totalRead > MaxBodyBytes)
            {
                throw new RequestTooLargeException();
            }
        }

        return encoding.GetString(buffer, 0, totalRead);
    }

    public async Task WriteJsonAsync(string json, int statusCode = 200, CancellationToken ct = default)
    {
        var bytes = Encoding.UTF8.GetBytes(json + Environment.NewLine);
        _ctx.Response.StatusCode = statusCode;
        _ctx.Response.ContentType = "application/json; charset=utf-8";
        _ctx.Response.ContentLength64 = bytes.Length;
        try
        {
            await _ctx.Response.OutputStream.WriteAsync(bytes.AsMemory(), ct);
        }
        finally
        {
            _ctx.Response.Close();
        }
    }

    public async Task WriteTextAsync(string text, int statusCode = 200, CancellationToken ct = default)
    {
        var bytes = Encoding.UTF8.GetBytes(text + Environment.NewLine);
        _ctx.Response.StatusCode = statusCode;
        _ctx.Response.ContentType = "text/plain; charset=utf-8";
        _ctx.Response.ContentLength64 = bytes.Length;
        try
        {
            await _ctx.Response.OutputStream.WriteAsync(bytes.AsMemory(), ct);
        }
        finally
        {
            _ctx.Response.Close();
        }
    }
}
