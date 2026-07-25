internal sealed record ProjectCommandRequestDto(
    string Action,
    string? AssetPath,
    string? NewAssetPath,
    string? Content,
    string? RequestId = null,
    MutationIntentDto? Intent = null);

internal sealed record ProjectCommandResponseDto(
    bool Ok,
    string Message,
    string? Kind,
    string? Content);

internal sealed record ProjectCommandAcceptedDto(
    bool Ok,
    string RequestId,
    string Action,
    bool Duplicated,
    string Stage,
    string Message);

internal sealed record ProjectCommandResultDto(
    bool Found,
    bool Completed,
    bool Success,
    string RequestId,
    string Action,
    string State,
    string Message,
    string? ResponsePayload);

internal sealed record BuildStatusDto(
    bool Running,
    bool CancelRequested,
    float Progress01,
    string Step,
    string Kind,
    string? LogPath,
    string? OutputPath,
    string? StartedAtUtc,
    string? FinishedAtUtc,
    bool Success,
    string Message,
    string? LastHeartbeatUtc,
    string? LastDiagnostic,
    string? LastException);

internal sealed record BuildLogChunkDto(
    long NextOffset,
    List<BuildLogLineDto> Lines);

internal sealed record CompileStatusDto(
    bool Running,
    bool Succeeded,
    string[] Errors,
    string? StartedAtUtc,
    string? FinishedAtUtc);

internal sealed record CompileRequestExtrasDto(
    string RequestId,
    bool Tracked,
    string ResultPath,
    string LockDir);

internal sealed record CompilePersistedIssueDto(
    string Message,
    string File,
    int Line,
    string Assembly);

internal sealed record CompilePersistedResultDto(
    string RequestId,
    string Outcome,
    bool Success,
    int ErrorCount,
    int WarningCount,
    string StartedAtUtc,
    string FinishedAtUtc,
    string Message,
    CompilePersistedIssueDto[] Errors,
    CompilePersistedIssueDto[] Warnings,
    bool ForceRecompile);

/// <summary>
/// Payload returned when the editor accepts a test job. Only <c>RequestId</c> is consumed:
/// <c>ResultPath</c> and <c>ArtifactsPath</c> are protocol documentation, and the CLI deliberately
/// recomputes both locally rather than following a path the editor reported. Leave them unread.
/// </summary>
internal sealed record TestJobAcceptedDto(
    string RequestId,
    string Kind,
    string ResultPath,
    string ArtifactsPath);

/// <summary>Completion marker the editor writes once an in-editor test job finishes.</summary>
internal sealed record TestJobResultDto(
    string RequestId,
    string Kind,
    bool Ok,
    string Message,
    string XmlPath,
    TestListEntryDto[] Tests);

internal sealed record TestListEntryDto(
    string TestName,
    string Assembly);

internal sealed record BuildLogLineDto(
    string Level,
    string Text);

internal sealed record ProjectCommandStatusDto(
    string RequestId,
    string Action,
    bool Active,
    bool Success,
    string Stage,
    string Detail,
    string StartedAtUtc,
    string LastUpdatedAtUtc,
    string FinishedAtUtc,
    bool IsCompiling,
    bool IsUpdating,
    bool IsDurable = false,
    string State = "",
    bool CancelRequested = false);
