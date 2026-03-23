using System.Collections.Concurrent;
using System.Text.Json;

internal sealed class ExecApprovalService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan StaleApprovalTtl = TimeSpan.FromHours(24);

    private readonly ConcurrentDictionary<string, PendingApproval> _pending = new(StringComparer.Ordinal);
    private readonly string? _storePath;
    private readonly object _ioLock = new();

    /// <param name="storePath">
    /// Optional path to a JSON file used for crash-survival persistence.
    /// When set, pending approvals are written on every change and loaded at startup.
    /// Stale entries (older than 24 h) are discarded on load.
    /// </param>
    public ExecApprovalService(string? storePath = null)
    {
        _storePath = storePath;
        if (storePath is not null)
        {
            TryLoadFromDisk();
        }
    }

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
        FlushToDisk();
        return token;
    }

    /// <summary>
    /// Attempts to consume (remove) a pending approval by token.
    /// Returns true and the approval record if found; false otherwise.
    /// </summary>
    public bool TryConsume(string approvalToken, out PendingApproval? approval)
    {
        var consumed = _pending.TryRemove(approvalToken, out approval);
        if (consumed)
        {
            FlushToDisk();
        }

        return consumed;
    }

    /// <summary>Discards a pending approval without executing.</summary>
    public void Reject(string approvalToken)
    {
        if (_pending.TryRemove(approvalToken, out _))
        {
            FlushToDisk();
        }
    }

    /// <summary>Deletes the backing store file on shutdown.</summary>
    public void Dispose()
    {
        if (_storePath is null)
        {
            return;
        }

        try
        {
            File.Delete(_storePath);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    private void FlushToDisk()
    {
        if (_storePath is null)
        {
            return;
        }

        try
        {
            var snapshot = _pending.ToDictionary(kv => kv.Key, kv => kv.Value);
            var json = JsonSerializer.Serialize(snapshot, JsonOptions);
            lock (_ioLock)
            {
                var dir = Path.GetDirectoryName(_storePath);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.WriteAllText(_storePath, json);
            }
        }
        catch
        {
            // best-effort persistence
        }
    }

    private void TryLoadFromDisk()
    {
        if (_storePath is null || !File.Exists(_storePath))
        {
            return;
        }

        try
        {
            string json;
            lock (_ioLock)
            {
                json = File.ReadAllText(_storePath);
            }

            var loaded = JsonSerializer.Deserialize<Dictionary<string, PendingApproval>>(json, JsonOptions);
            if (loaded is null)
            {
                return;
            }

            var cutoff = DateTime.UtcNow - StaleApprovalTtl;
            foreach (var (k, v) in loaded)
            {
                if (v.CreatedAtUtc >= cutoff)
                {
                    _pending[k] = v;
                }
            }
        }
        catch
        {
            // best-effort load — corrupt or missing file is fine
        }
    }

    internal sealed record PendingApproval(
        string Token,
        string RequestId,
        string Operation,
        string? ArgsJson,
        DateTime CreatedAtUtc);
}
