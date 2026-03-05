internal sealed record ProjectCommandRequestDto(
    string Action,
    string? AssetPath,
    string? NewAssetPath,
    string? Content);

internal sealed record ProjectCommandResponseDto(
    bool Ok,
    string Message,
    string? Kind,
    string? Content);

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
