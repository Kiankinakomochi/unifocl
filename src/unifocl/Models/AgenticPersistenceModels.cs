internal sealed record AgenticSessionSnapshot(
    string SessionSeed,
    string Mode,
    string ContextMode,
    string? CurrentProjectPath,
    int? AttachedPort,
    string FocusPath,
    string? InspectorTargetPath,
    bool SafeModeEnabled,
    string UpdatedAtUtc,
    string LastRequestId,
    string LastCommandText);

internal sealed record AgenticRequestStatusSnapshot(
    string RequestId,
    string SessionSeed,
    string CommandText,
    string OutputMode,
    string State,
    string Mode,
    string Action,
    int? ExitCode,
    string StartedAtUtc,
    string? CompletedAtUtc,
    string? ErrorCode,
    string? ErrorMessage);
