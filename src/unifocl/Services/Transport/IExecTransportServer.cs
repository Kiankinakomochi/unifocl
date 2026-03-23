/// <summary>
/// Abstracts an inbound transport server that accepts one request context at a time.
/// The caller is responsible for handling each accepted context concurrently.
/// </summary>
internal interface IExecTransportServer : IDisposable
{
    /// <summary>Starts listening. Must be called before AcceptAsync.</summary>
    void Start();

    /// <summary>
    /// Waits for the next inbound request and returns a context wrapping it.
    /// Throws OperationCanceledException when ct is cancelled.
    /// </summary>
    Task<IExecRequestContext> AcceptAsync(CancellationToken ct);
}
