internal sealed record AssetIndexEntryDto(
    int InstanceId,
    string Path);

internal sealed record AssetIndexSyncResponseDto(
    int Revision,
    bool Unchanged,
    List<AssetIndexEntryDto> Entries);

internal sealed record HierarchySearchRequestDto(
    string Query,
    int Limit,
    int? ParentId);

internal sealed record HierarchySearchResultDto(
    int NodeId,
    string Path,
    bool Active,
    double Score);

internal sealed record HierarchySearchResponseDto(
    bool Ok,
    List<HierarchySearchResultDto> Results,
    string? Message);

internal sealed record ProjectFuzzyMatch(
    int Index,
    int InstanceId,
    string Path,
    double Score);

internal sealed record InspectorSearchResultDto(
    string Scope,
    int? ComponentIndex,
    string Name,
    string Path,
    double Score);

