using System.Collections.Concurrent;

/// <summary>How a caller wants its Pipeline calls carried.</summary>
internal enum PipelineTransportKind
{
    /// <summary>A <c>unity command</c> process per call: for one-shot CLI use.</summary>
    Command,

    /// <summary>A long-lived <c>unity mcp</c> process per project: for agent sessions and the MCP server.</summary>
    McpSession,
}

/// <summary>
/// Hands out Pipeline transports. Both kinds run Unity's own CLI binary, so every unifocl call to an
/// Editor goes through the interface Unity's terms of service name as authorized agentic access.
/// </summary>
internal static class PipelineTransports
{
    private static readonly ConcurrentDictionary<string, UnityMcpTransport> Sessions =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    static PipelineTransports()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => DisposeSessionsAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <summary>
    /// A transport for <paramref name="projectPath"/>, or <c>null</c> with <paramref name="error"/> set
    /// when the Unity CLI isn't installed. An <see cref="PipelineTransportKind.McpSession"/> is shared by
    /// every caller for the same project and lives until the process exits; don't dispose it.
    /// </summary>
    public static IPipelineTransport? For(string projectPath, PipelineTransportKind kind, out string? error)
    {
        var unityCli = UnityCliLocator.Locate();
        if (unityCli is null)
        {
            error = UnityCliLocator.InstallHint;
            return null;
        }

        error = null;
        var key = Path.GetFullPath(projectPath);
        return kind switch
        {
            PipelineTransportKind.McpSession => Sessions.GetOrAdd(key, path => new UnityMcpTransport(unityCli, path)),
            _ => new UnityCommandTransport(unityCli, key),
        };
    }

    /// <summary>Stops every shared <c>unity mcp</c> process.</summary>
    public static async ValueTask DisposeSessionsAsync()
    {
        foreach (var key in Sessions.Keys.ToArray())
        {
            if (Sessions.TryRemove(key, out var session))
            {
                await session.DisposeAsync();
            }
        }
    }
}
