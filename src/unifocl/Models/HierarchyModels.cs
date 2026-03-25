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
    bool Primitive,
    string? Type = null,
    int? Count = null,
    MutationIntentDto? Intent = null);

internal sealed record HierarchyCommandResponseDto(
    bool Ok,
    string Message,
    int? NodeId,
    [property: JsonPropertyName("isActive")] bool? IsActive,
    string? Content = null,
    [property: JsonPropertyName("assignedName")] string? AssignedName = null);
