internal enum CliMode
{
    Boot,
    Project
}

internal enum CliContextMode
{
    None,
    Project,
    Hierarchy,
    Inspector
}

internal sealed class CliSessionState
{
    public CliMode Mode { get; set; } = CliMode.Boot;
    public CliContextMode ContextMode { get; set; } = CliContextMode.None;
    public int? AttachedPort { get; set; }
    public string? CurrentProjectPath { get; set; }
    public DateTimeOffset? LastOpenedUtc { get; set; }
    public string FocusPath { get; set; } = "/Player";
    public InspectorContext? Inspector { get; set; }
    public bool AutoEnterHierarchyRequested { get; set; }
    public ProjectViewState ProjectView { get; } = new();

    public void ResetToBoot()
    {
        Mode = CliMode.Boot;
        ContextMode = CliContextMode.None;
        AttachedPort = null;
        CurrentProjectPath = null;
        LastOpenedUtc = null;
        FocusPath = "/Player";
        Inspector = null;
        AutoEnterHierarchyRequested = false;

        ProjectView.Initialized = false;
        ProjectView.RelativeCwd = string.Empty;
        ProjectView.VisibleEntries.Clear();
        ProjectView.ExpandedDirectories.Clear();
        ProjectView.CommandTranscript.Clear();
        ProjectView.DbState = ProjectDbState.IdleSafe;
    }
}
