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
    public List<string> UnityLogPane { get; } = [];
    public List<RecentProjectEntry> RecentProjectEntries { get; } = [];
    public bool RecentSelectionAllowUnsafe { get; set; }
    public bool SafeModeEnabled { get; set; }
    public CompileErrorState? LastCompileError { get; set; }
    public string? VcsSetupPromptProjectPath { get; set; }
    public bool VcsSetupDeclinedForProject { get; set; }

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
        ProjectView.FocusHighlightedEntryIndex = null;
        ProjectView.AssetIndexRevision = 0;
        ProjectView.AssetPathByInstanceId.Clear();
        ProjectView.LastFuzzyMatches.Clear();
        ProjectView.LastUpmPackages.Clear();
        ProjectView.ExpandTranscriptForUpmList = false;
        ProjectView.UpmFocusModeEnabled = false;
        ProjectView.UpmFocusSelectedIndex = 0;
        ProjectView.UpmActionMenuVisible = false;
        ProjectView.UpmActionSelectedIndex = 0;
        UnityLogPane.Clear();
        RecentProjectEntries.Clear();
        RecentSelectionAllowUnsafe = false;
        SafeModeEnabled = false;
        LastCompileError = null;
        VcsSetupPromptProjectPath = null;
        VcsSetupDeclinedForProject = false;
    }
}

internal sealed record CompileErrorState(
    string ProjectPath,
    DateTimeOffset OccurredAtUtc,
    string Summary,
    IReadOnlyList<string> Lines);
