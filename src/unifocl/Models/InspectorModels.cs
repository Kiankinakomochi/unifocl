internal enum InspectorDepth
{
    ComponentList = 0,
    ComponentFields = 1
}

internal sealed class InspectorContext
{
    public string TargetPath { get; set; } = "/Player";
    public InspectorDepth Depth { get; set; } = InspectorDepth.ComponentList;
    public int? FocusHighlightedComponentIndex { get; set; }
    public string? FocusHighlightedFieldName { get; set; }
    public List<InspectorComponentEntry> Components { get; } = [];
    public List<InspectorFieldEntry> Fields { get; } = [];
    public int? SelectedComponentIndex { get; set; }
    public string? SelectedComponentName { get; set; }
    public List<string> CommandStream { get; } = [];
    public int BodyScrollOffset { get; set; }
    public int StreamScrollOffset { get; set; } = int.MaxValue;
    public bool FollowStreamScroll { get; set; } = true;
    public bool InteractiveEditActive { get; set; }
    public string? InteractiveEditFieldName { get; set; }
    public int InteractiveEditPartIndex { get; set; }
    public int InteractiveEditPartCount { get; set; }
    public string? InteractiveEditMode { get; set; }
    public bool InteractiveOverlayActive { get; set; }
    public string? InteractiveOverlayTitle { get; set; }
    public string? InteractiveOverlayValue { get; set; }

    public string PromptPath =>
        Depth == InspectorDepth.ComponentFields && !string.IsNullOrWhiteSpace(SelectedComponentName)
            ? $"{TargetPath}/{SelectedComponentName}"
            : TargetPath;

    public string PromptLabel =>
        Depth == InspectorDepth.ComponentList
            ? $"UnityCLI:{TargetPath} [inspect]"
            : $"UnityCLI:{PromptPath}";
}

internal sealed record InspectorComponentEntry(int Index, string Name, bool Enabled);
internal sealed record InspectorFieldEntry(
    string Name,
    string Value,
    string Type,
    bool IsBoolean,
    IReadOnlyList<string>? EnumOptions = null);
