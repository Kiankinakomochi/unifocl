/// <summary>
/// IExecTransportClient backed by HTTP, connecting to the Unity daemon's
/// loopback HttpListener (CLIDaemon) via the durable mutation protocol.
///
/// Promoted from the private HttpProjectMutationTransport in HierarchyDaemonClient (Sprint 3 leftover).
/// A future UdsExecTransportClient can replace this when CLIDaemon gains UDS support.
/// </summary>
internal sealed class HttpExecTransportClient : IExecTransportClient
{
    public Task<ProjectCommandResponseDto?> ExecuteProjectCommandAsync(
        int port,
        ProjectCommandRequestDto request,
        TimeSpan timeout,
        Action<string>? onStatus = null)
    {
        return HierarchyDaemonClient.ExecuteDurableMutationOverHttpAsync(port, request, timeout, onStatus);
    }
}
