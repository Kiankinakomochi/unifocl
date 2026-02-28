internal enum InspectorDepth
{
    ComponentList = 0,
    ComponentFields = 1
}

internal sealed class InspectorContext
{
    public string TargetPath { get; set; } = "/Player";
    public InspectorDepth Depth { get; set; } = InspectorDepth.ComponentList;
    public List<InspectorComponentEntry> Components { get; } = [];
    public List<InspectorFieldEntry> Fields { get; } = [];
    public int? SelectedComponentIndex { get; set; }
    public string? SelectedComponentName { get; set; }
    public List<string> CommandStream { get; } = [];

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
internal sealed record InspectorFieldEntry(string Name, string Value, string Type, bool IsBoolean);
