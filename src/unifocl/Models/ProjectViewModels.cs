internal enum ProjectDbState
{
    IdleSafe,
    LockedImporting
}

internal sealed class ProjectViewState
{
    public bool Initialized { get; set; }
    public string RelativeCwd { get; set; } = string.Empty;
    public List<ProjectTreeEntry> VisibleEntries { get; } = [];
    public HashSet<string> ExpandedDirectories { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> CommandTranscript { get; } = [];
    public ProjectDbState DbState { get; set; } = ProjectDbState.IdleSafe;
}

internal sealed record ProjectTreeEntry(
    int Index,
    int Depth,
    string Name,
    string RelativePath,
    bool IsDirectory);
