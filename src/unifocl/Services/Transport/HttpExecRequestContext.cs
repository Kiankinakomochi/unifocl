using System.Collections.Specialized;
using System.Net;
using System.Text;

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

    public async Task<string> ReadBodyAsync(CancellationToken ct = default)
    {
        using var reader = new StreamReader(
            _ctx.Request.InputStream,
            _ctx.Request.ContentEncoding ?? Encoding.UTF8);
        return await reader.ReadToEndAsync(ct);
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
