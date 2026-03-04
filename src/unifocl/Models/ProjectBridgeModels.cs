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
