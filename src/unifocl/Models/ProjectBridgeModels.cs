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
