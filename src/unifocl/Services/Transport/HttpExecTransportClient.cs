/// <summary>
/// IExecTransportClient that runs project commands through the durable mutation protocol, on an
/// <see cref="EditorLinkHandler"/> client that reaches the Editor through the Unity CLI.
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
