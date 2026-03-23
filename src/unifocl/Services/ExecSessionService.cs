using System.Collections.Concurrent;

/// <summary>
/// Manages ExecV2 sessions: open, close, status, and trust level resolution.
/// Replaces the implicit AttachedPort-based "session" with an explicit sessionId.
/// Sprint 4 will complete the migration from AttachedPort to SessionId.
/// </summary>
internal sealed class ExecSessionService
{
    private readonly ConcurrentDictionary<string, ExecSession> _sessions = new(StringComparer.Ordinal);

    /// <summary>
    /// Opens a new session for the given project path.
    /// Returns the sessionId. The session is created in Trusted state
    /// (safe_write operations do not require approval).
    /// </summary>
    public ExecSession Open(string projectPath, string? runtimeType = null)
    {
        var sessionId = Guid.NewGuid().ToString("N");
        var session = new ExecSession(
            sessionId,
            projectPath,
            runtimeType ?? "bridge",
            Trusted: true,
            OpenedAtUtc: DateTime.UtcNow,
            LastActivityUtc: DateTime.UtcNow);
        _sessions[sessionId] = session;
        return session;
    }

    /// <summary>Closes the session and removes it from the registry.</summary>
    public bool Close(string sessionId)
        => _sessions.TryRemove(sessionId, out _);

    /// <summary>Returns the session or null if not found.</summary>
    public ExecSession? Get(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        return _sessions.TryGetValue(sessionId, out var session) ? session : null;
    }

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
}

internal sealed record ExecSession(
    string SessionId,
    string ProjectPath,
    string RuntimeType,
    bool Trusted,
    DateTime OpenedAtUtc,
    DateTime LastActivityUtc);
