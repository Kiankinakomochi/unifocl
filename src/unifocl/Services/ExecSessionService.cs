using System.Collections.Concurrent;

/// <summary>
/// Manages ExecV2 sessions: open, close, status, trust level resolution, and orphan cleanup.
/// Replaces the implicit AttachedPort-based "session" with an explicit sessionId.
/// Sprint 4 completes the migration from AttachedPort to SessionId as the primary key.
/// </summary>
internal sealed class ExecSessionService : IDisposable
{
    private static readonly TimeSpan OrphanIdleThreshold = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan OrphanCleanupInterval = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, ExecSession> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<int, string> _portToSessionId = new();
    private readonly Timer _cleanupTimer;

    public ExecSessionService()
    {
        _cleanupTimer = new Timer(_ => CleanupOrphans(OrphanIdleThreshold), null, OrphanCleanupInterval, OrphanCleanupInterval);
    }

    /// <summary>
    /// Opens a new session for the given project path.
    /// Returns the session. The session is created in Trusted state
    /// (safe_write operations do not require approval).
    /// </summary>
    public ExecSession Open(string projectPath, string? runtimeType = null, int? port = null)
    {
        var sessionId = Guid.NewGuid().ToString("N");
        var session = new ExecSession(
            sessionId,
            projectPath,
            runtimeType ?? "bridge",
            Trusted: true,
            Port: port,
            OpenedAtUtc: DateTime.UtcNow,
            LastActivityUtc: DateTime.UtcNow);
        _sessions[sessionId] = session;
        if (port is int p)
        {
            _portToSessionId[p] = sessionId;
        }

        return session;
    }

    /// <summary>
    /// Opens or replaces the session bound to a specific port.
    /// Called when DaemonControlService attaches to a port.
    /// </summary>
    public ExecSession OpenForPort(int port, string projectPath, string? runtimeType = null)
    {
        // Close any existing session for this port first.
        if (_portToSessionId.TryRemove(port, out var existingId))
        {
            _sessions.TryRemove(existingId, out _);
        }

        return Open(projectPath, runtimeType, port);
    }

    /// <summary>Closes the session with the given sessionId and removes it from the registry.</summary>
    public bool Close(string sessionId)
    {
        if (!_sessions.TryRemove(sessionId, out var session))
        {
            return false;
        }

        if (session.Port is int p)
        {
            _portToSessionId.TryRemove(p, out _);
        }

        return true;
    }

    /// <summary>Closes the session bound to a port, if any.</summary>
    public bool CloseByPort(int port)
    {
        if (!_portToSessionId.TryRemove(port, out var sessionId))
        {
            return false;
        }

        _sessions.TryRemove(sessionId, out _);
        return true;
    }

    /// <summary>Returns the session or null if not found.</summary>
    public ExecSession? Get(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        return _sessions.TryGetValue(sessionId, out var session) ? session : null;
    }

    /// <summary>Returns the session bound to a port, or null if none.</summary>
    public ExecSession? GetByPort(int port)
        => _portToSessionId.TryGetValue(port, out var sessionId) ? Get(sessionId) : null;

    /// <summary>
    /// Updates the last activity timestamp for a session.
    /// Returns true if the session was found.
    /// </summary>
    public bool Touch(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            return false;
        }

        _sessions[sessionId] = session with { LastActivityUtc = DateTime.UtcNow };
        return true;
    }

    /// <summary>
    /// Returns true if the session is present and trusted
    /// (i.e., safe_write does not require approval).
    /// </summary>
    public bool IsTrusted(string? sessionId)
    {
        var session = Get(sessionId);
        return session?.Trusted ?? false;
    }

    /// <summary>
    /// Removes all sessions that have been idle longer than <paramref name="maxIdle"/>.
    /// Returns the number of sessions removed.
    /// </summary>
    public int CleanupOrphans(TimeSpan maxIdle)
    {
        var cutoff = DateTime.UtcNow - maxIdle;
        var removed = 0;
        foreach (var (id, session) in _sessions)
        {
            if (session.LastActivityUtc < cutoff && _sessions.TryRemove(id, out _))
            {
                if (session.Port is int p)
                {
                    _portToSessionId.TryRemove(p, out _);
                }

                removed++;
            }
        }

        return removed;
    }

    public void Dispose() => _cleanupTimer.Dispose();
}

internal sealed record ExecSession(
    string SessionId,
    string ProjectPath,
    string RuntimeType,
    bool Trusted,
    int? Port,
    DateTime OpenedAtUtc,
    DateTime LastActivityUtc);
