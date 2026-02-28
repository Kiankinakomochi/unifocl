internal enum CliMode
{
    Boot,
    Project
}

internal sealed class CliSessionState
{
    public CliMode Mode { get; set; } = CliMode.Boot;
    public int? AttachedPort { get; set; }
    public string? CurrentProjectPath { get; set; }
    public DateTimeOffset? LastOpenedUtc { get; set; }
    public string FocusPath { get; set; } = "/Player";
    public InspectorContext? Inspector { get; set; }
    public ProjectViewState ProjectView { get; } = new();

    public void ResetToBoot()
    {
        Mode = CliMode.Boot;
        AttachedPort = null;
        CurrentProjectPath = null;
        LastOpenedUtc = null;
        FocusPath = "/Player";
        Inspector = null;

        ProjectView.Initialized = false;
        ProjectView.RelativeCwd = string.Empty;
        ProjectView.VisibleEntries.Clear();
        ProjectView.ExpandedDirectories.Clear();
        ProjectView.CommandTranscript.Clear();
        ProjectView.DbState = ProjectDbState.IdleSafe;
    }
}
