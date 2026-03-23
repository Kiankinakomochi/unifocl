using System.Collections.Specialized;

/// <summary>
/// Abstracts a single inbound request/response cycle regardless of transport (HTTP, UDS, etc.).
/// Implementations must be single-use — once a response is written the context is considered closed.
/// </summary>
internal interface IExecRequestContext
{
    string Method { get; }
    string Path { get; }
    NameValueCollection Query { get; }
    /// <summary>
    /// True for UDS connections. Internal-only endpoints (/stop, /touch) are
    /// restricted to internal transports and reject HTTP callers.
    /// </summary>
    bool IsInternal { get; }

    Task<string> ReadBodyAsync(CancellationToken ct = default);
    Task WriteJsonAsync(string json, int statusCode = 200, CancellationToken ct = default);
    Task WriteTextAsync(string text, int statusCode = 200, CancellationToken ct = default);
}
