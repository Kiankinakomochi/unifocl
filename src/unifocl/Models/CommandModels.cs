internal sealed record CommandSpec(string Signature, string Description, string Trigger);
internal sealed record DaemonStartOptions(int Port, string? UnityPath, bool Headless);
internal sealed record DaemonServiceOptions(int Port, string? UnityPath, bool Headless);
internal sealed record DaemonInstance(
    int Port,
    int Pid,
    DateTime StartedAtUtc,
    string? UnityPath,
    bool Headless,
    string? ProjectPath,
    DateTime LastHeartbeatUtc);
internal sealed record DaemonSessionInfo(int Port, DateTimeOffset StartedAtUtc, bool Created);
internal sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);
internal sealed record OperationResult(bool Ok, string Error)
{
    public static OperationResult Success() => new(true, string.Empty);
    public static OperationResult Fail(string error) => new(false, error);
}
