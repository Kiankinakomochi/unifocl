internal sealed record UpmListRequestPayload(
    bool IncludeOutdated,
    bool IncludeBuiltin,
    bool IncludeGit);

internal sealed record MkAssetRequestPayload(
    string Type,
    int Count,
    string? Name);

internal sealed record MkAssetResponsePayload(
    List<string>? CreatedPaths);

internal sealed record UpmInstallRequestPayload(
    string Target);

internal sealed record UpmRemoveRequestPayload(
    string PackageId);

internal sealed record UpmListResponsePayload(
    List<UpmListPackagePayload>? Packages);

internal sealed record UpmInstallResponsePayload(
    string? PackageId,
    string? Version,
    string? Source,
    string? TargetType);

internal sealed record UpmListPackagePayload(
    string? PackageId,
    string? DisplayName,
    string? Version,
    string? Source,
    string? LatestCompatibleVersion,
    bool IsOutdated,
    bool IsDeprecated,
    bool IsPreview);
