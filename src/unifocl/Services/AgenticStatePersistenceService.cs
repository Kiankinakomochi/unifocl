using System.Text.Json;

internal static class AgenticStatePersistenceService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly object IoSync = new();

    public static string NormalizeSessionSeed(string? rawSessionSeed)
    {
        if (string.IsNullOrWhiteSpace(rawSessionSeed))
        {
            return Guid.NewGuid().ToString("N");
        }

        var sanitized = new string(rawSessionSeed
            .Trim()
            .Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_')
            .Take(80)
            .ToArray());

        return string.IsNullOrWhiteSpace(sanitized) ? Guid.NewGuid().ToString("N") : sanitized;
    }

    public static AgenticSessionSnapshot? TryReadSessionSnapshot(string sessionSeed)
    {
        var path = ResolveSessionPath(sessionSeed);
        lock (IoSync)
        {
            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<AgenticSessionSnapshot>(json, JsonOptions);
            }
            catch
            {
                return null;
            }
        }
    }

    public static void WriteSessionSnapshot(string sessionSeed, CliSessionState session, string requestId, string commandText)
    {
        var snapshot = new AgenticSessionSnapshot(
            sessionSeed,
            session.Mode == CliMode.Project ? "project" : "boot",
            session.ContextMode switch
            {
                CliContextMode.Project => "project",
                CliContextMode.Hierarchy => "hierarchy",
                CliContextMode.Inspector => "inspector",
                _ => "none"
            },
            session.CurrentProjectPath,
            DaemonControlService.GetPort(session),
            session.FocusPath,
            session.Inspector?.TargetPath,
            session.SafeModeEnabled,
            DateTime.UtcNow.ToString("O"),
            requestId,
            commandText);

        var path = ResolveSessionPath(sessionSeed);
        lock (IoSync)
        {
            WriteJsonAtomic(path, snapshot);
        }
    }

    /// <summary>
    /// Scans persisted session snapshots to find which session seed currently owns a daemon
    /// port. Returns the owner's session seed, or null if no live session claims the port.
    /// </summary>
    public static string? FindSessionSeedByPort(int port)
    {
        var sessionsDir = Path.Combine(ResolveRuntimeRoot(), "sessions");
        if (!Directory.Exists(sessionsDir))
        {
            return null;
        }

        lock (IoSync)
        {
            foreach (var file in Directory.EnumerateFiles(sessionsDir, "*.json"))
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var snapshot = JsonSerializer.Deserialize<AgenticSessionSnapshot>(json, JsonOptions);
                    if (snapshot?.AttachedPort == port)
                    {
                        return snapshot.SessionSeed;
                    }
                }
                catch
                {
                    // Ignore malformed snapshot files.
                }
            }
        }

        return null;
    }

    public static AgenticRequestStatusSnapshot? TryReadRequestStatus(string requestId)
    {
        var path = ResolveRequestPath(requestId);
        lock (IoSync)
        {
            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<AgenticRequestStatusSnapshot>(json, JsonOptions);
            }
            catch
            {
                return null;
            }
        }
    }

    public static void MarkRequestStarted(string requestId, string sessionSeed, string commandText, string outputMode)
    {
        var path = ResolveRequestPath(requestId);
        var snapshot = new AgenticRequestStatusSnapshot(
            requestId,
            sessionSeed,
            commandText,
            outputMode,
            "running",
            "none",
            "exec",
            null,
            DateTime.UtcNow.ToString("O"),
            null,
            null,
            null);

        lock (IoSync)
        {
            WriteJsonAtomic(path, snapshot);
        }
    }

    public static void MarkRequestCompleted(
        string requestId,
        string sessionSeed,
        string commandText,
        string outputMode,
        int processExitCode,
        string payloadText)
    {
        var startedAtUtc = DateTime.UtcNow.ToString("O");
        var previous = TryReadRequestStatus(requestId);
        if (previous is not null)
        {
            startedAtUtc = previous.StartedAtUtc;
        }

        var (state, mode, action, exitCode, errorCode, errorMessage) =
            ParseAgenticPayload(outputMode, payloadText, processExitCode);

        var snapshot = new AgenticRequestStatusSnapshot(
            requestId,
            sessionSeed,
            commandText,
            outputMode,
            state,
            mode,
            action,
            exitCode,
            startedAtUtc,
            DateTime.UtcNow.ToString("O"),
            errorCode,
            errorMessage);

        lock (IoSync)
        {
            WriteJsonAtomic(ResolveRequestPath(requestId), snapshot);
        }
    }

    private static (string State, string Mode, string Action, int? ExitCode, string? ErrorCode, string? ErrorMessage) ParseAgenticPayload(
        string outputMode,
        string payloadText,
        int processExitCode)
    {
        if (!outputMode.Equals("json", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(payloadText))
        {
            return (processExitCode == 0 ? "success" : "error", "none", "exec", processExitCode, null, null);
        }

        try
        {
            using var json = JsonDocument.Parse(payloadText);
            var root = json.RootElement;

            var status = root.TryGetProperty("status", out var statusEl) && statusEl.ValueKind == JsonValueKind.String
                ? statusEl.GetString() ?? "error"
                : (processExitCode == 0 ? "success" : "error");
            var mode = root.TryGetProperty("mode", out var modeEl) && modeEl.ValueKind == JsonValueKind.String
                ? modeEl.GetString() ?? "none"
                : "none";
            var action = root.TryGetProperty("action", out var actionEl) && actionEl.ValueKind == JsonValueKind.String
                ? actionEl.GetString() ?? "exec"
                : "exec";

            int? exitCode = processExitCode;
            if (root.TryGetProperty("meta", out var metaEl)
                && metaEl.ValueKind == JsonValueKind.Object
                && metaEl.TryGetProperty("exitCode", out var exitCodeEl)
                && exitCodeEl.TryGetInt32(out var parsedExitCode))
            {
                exitCode = parsedExitCode;
            }

            string? errorCode = null;
            string? errorMessage = null;
            if (root.TryGetProperty("errors", out var errorsEl)
                && errorsEl.ValueKind == JsonValueKind.Array
                && errorsEl.GetArrayLength() > 0)
            {
                var first = errorsEl[0];
                if (first.ValueKind == JsonValueKind.Object)
                {
                    if (first.TryGetProperty("code", out var codeEl) && codeEl.ValueKind == JsonValueKind.String)
                    {
                        errorCode = codeEl.GetString();
                    }

                    if (first.TryGetProperty("message", out var messageEl) && messageEl.ValueKind == JsonValueKind.String)
                    {
                        errorMessage = messageEl.GetString();
                    }
                }
            }

            return (status, mode, action, exitCode, errorCode, errorMessage);
        }
        catch
        {
            return (processExitCode == 0 ? "success" : "error", "none", "exec", processExitCode, null, null);
        }
    }

    private static void WriteJsonAtomic<T>(string path, T value)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        Directory.CreateDirectory(directory);
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            var json = JsonSerializer.Serialize(value, JsonOptions);
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
            }
        }
    }

    private static string ResolveRuntimeRoot()
    {
        return Path.Combine(Environment.CurrentDirectory, ".unifocl-runtime", "agentic");
    }

    private static string ResolveSessionPath(string sessionSeed)
    {
        return Path.Combine(ResolveRuntimeRoot(), "sessions", $"{sessionSeed}.json");
    }

    private static string ResolveRequestPath(string requestId)
    {
        var sanitizedRequestId = NormalizeSessionSeed(requestId);
        return Path.Combine(ResolveRuntimeRoot(), "requests", $"{sanitizedRequestId}.json");
    }
}
