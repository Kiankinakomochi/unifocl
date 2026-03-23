using System.Text.Json.Serialization;

// ── Input ────────────────────────────────────────────────────────────────────

/// <summary>
/// A single mutation operation inside a /mutate batch.
/// Context (hierarchy vs inspector) is inferred from "op" — no mode switching needed.
/// </summary>
internal sealed record MutateOp(
    /// <summary>
    /// Operation type. One of:
    ///   Hierarchy: create | rename | remove | move | toggle_active
    ///   Inspector: add_component | remove_component | set_field | toggle_field | toggle_component
    /// </summary>
    [property: JsonPropertyName("op")] string Op,

    /// <summary>
    /// Scene-hierarchy path of the target object.
    /// Format: "/Name" or "/Parent/Child". Use "/" for scene root.
    /// Required by: rename, remove, move, toggle_active, add_component,
    ///              remove_component, set_field, toggle_field, toggle_component.
    /// </summary>
    [property: JsonPropertyName("target")] string? Target = null,

    /// <summary>
    /// Parent path for create (defaults to "/") and destination path for move.
    /// </summary>
    [property: JsonPropertyName("parent")] string? Parent = null,

    /// <summary>
    /// GameObject or component type.
    /// For create: "canvas", "empty", "image", "text", "button", "scrollview",
    ///             "panel", "inputfield", "rawimage", "toggle", "slider",
    ///             "dropdown", "scrollbar", "eventSystem", or any Unity type name.
    /// For add_component: component type name (e.g., "CanvasScaler", "Image", "Rigidbody").
    /// </summary>
    [property: JsonPropertyName("type")] string? Type = null,

    /// <summary>New name for create or rename.</summary>
    [property: JsonPropertyName("name")] string? Name = null,

    /// <summary>
    /// Component selector for remove_component, set_field, toggle_field, toggle_component.
    /// Accepts: component name (e.g., "Image") or 0-based index as string (e.g., "2").
    /// </summary>
    [property: JsonPropertyName("component")] string? Component = null,

    /// <summary>Field name for set_field or toggle_field.</summary>
    [property: JsonPropertyName("field")] string? Field = null,

    /// <summary>Serialized field value string for set_field (same format as inspector set command).</summary>
    [property: JsonPropertyName("value")] string? Value = null,

    /// <summary>Desired active state for toggle_active. Omit to unconditionally flip.</summary>
    [property: JsonPropertyName("active")] bool? Active = null,

    /// <summary>Desired enabled state for toggle_component. Omit to unconditionally flip.</summary>
    [property: JsonPropertyName("enabled")] bool? Enabled = null,

    /// <summary>Number of objects to create (create op only, defaults to 1).</summary>
    [property: JsonPropertyName("count")] int? Count = null);

// ── Output ───────────────────────────────────────────────────────────────────

internal sealed record MutateOpResult(
    [property: JsonPropertyOrder(1)] int Index,
    [property: JsonPropertyOrder(2)] string Op,
    [property: JsonPropertyOrder(3)] string? Target,
    [property: JsonPropertyOrder(4)] bool Ok,
    [property: JsonPropertyOrder(5)] string? Message = null,
    [property: JsonPropertyOrder(6)] int? CreatedId = null);

internal sealed record MutateBatchResult(
    [property: JsonPropertyOrder(1)] bool AllOk,
    [property: JsonPropertyOrder(2)] int Total,
    [property: JsonPropertyOrder(3)] int Succeeded,
    [property: JsonPropertyOrder(4)] int Failed,
    [property: JsonPropertyOrder(5)] List<MutateOpResult> Results,
    [property: JsonPropertyOrder(6)] bool DryRun = false);
