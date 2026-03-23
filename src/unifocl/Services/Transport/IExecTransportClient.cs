/// <summary>
/// Abstracts an outbound transport client for sending project mutation commands to the Unity daemon.
/// Promoted from the private IProjectMutationTransport in HierarchyDaemonClient.
/// </summary>
internal interface IExecTransportClient
{
    /// <summary>
    /// Executes a project command against the Unity daemon reachable at the given port.
    /// Returns null if the durable mutation endpoints are unavailable (signals fallback to legacy path).
    /// </summary>
    Task<ProjectCommandResponseDto?> ExecuteProjectCommandAsync(
        int port,
        ProjectCommandRequestDto request,
        TimeSpan timeout,
        Action<string>? onStatus = null);
}
