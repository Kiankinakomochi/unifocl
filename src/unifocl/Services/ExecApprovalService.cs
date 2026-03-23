using System.Collections.Concurrent;

internal sealed class ExecApprovalService
{
    private readonly ConcurrentDictionary<string, PendingApproval> _pending = new(StringComparer.Ordinal);

    /// <summary>
    /// Returns true if the given risk level requires user approval before execution.
    /// sessionTrusted=true allows safe_write operations to proceed without approval.
    /// </summary>
    public bool IsApprovalRequired(ExecRiskLevel risk, bool sessionTrusted = false)
    {
        return risk switch
        {
            ExecRiskLevel.DestructiveWrite => true,
            ExecRiskLevel.PrivilegedExec   => true,
            ExecRiskLevel.SafeWrite        => !sessionTrusted,
            _                              => false
        };
    }

    /// <summary>
    /// Creates a pending approval ticket and returns the approval token.
    /// The token must be presented in a subsequent exec request to authorize execution.
    /// </summary>
    public string CreatePendingApproval(string requestId, string operation, string? argsJson)
    {
        var token = Guid.NewGuid().ToString("N");
        _pending[token] = new PendingApproval(token, requestId, operation, argsJson, DateTime.UtcNow);
        return token;
    }

    /// <summary>
    /// Attempts to consume (remove) a pending approval by token.
    /// Returns true and the approval record if found; false otherwise.
    /// </summary>
    public bool TryConsume(string approvalToken, out PendingApproval? approval)
        => _pending.TryRemove(approvalToken, out approval);

    /// <summary>Discards a pending approval without executing.</summary>
    public void Reject(string approvalToken)
        => _pending.TryRemove(approvalToken, out _);

    internal sealed record PendingApproval(
        string Token,
        string RequestId,
        string Operation,
        string? ArgsJson,
        DateTime CreatedAtUtc);
}
