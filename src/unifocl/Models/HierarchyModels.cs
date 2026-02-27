using System.Text.Json.Serialization;

internal sealed record HierarchyNodeDto(
    int Id,
    string Name,
    bool Active,
    List<HierarchyNodeDto> Children);

internal sealed record HierarchySnapshotDto(
    string Scene,
    int SnapshotVersion,
    HierarchyNodeDto Root);

internal sealed record HierarchyCommandRequestDto(
    string Action,
    int? ParentId,
    int? TargetId,
    string? Name,
    bool Primitive);

internal sealed record HierarchyCommandResponseDto(
    bool Ok,
    string Message,
    int? NodeId,
    [property: JsonPropertyName("isActive")] bool? IsActive);
