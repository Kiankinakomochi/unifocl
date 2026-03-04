internal enum ProjectDbState
{
    IdleSafe,
    LockedImporting
}

internal sealed class ProjectViewState
{
    public bool Initialized { get; set; }
    public string RelativeCwd { get; set; } = string.Empty;
    public int? FocusHighlightedEntryIndex { get; set; }
    public List<ProjectTreeEntry> VisibleEntries { get; } = [];
    public HashSet<string> ExpandedDirectories { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> CommandTranscript { get; } = [];
    public ProjectDbState DbState { get; set; } = ProjectDbState.IdleSafe;
    public int AssetIndexRevision { get; set; }
    public Dictionary<int, string> AssetPathByInstanceId { get; } = [];
    public List<ProjectFuzzyMatch> LastFuzzyMatches { get; } = [];
    public List<UpmPackageEntry> LastUpmPackages { get; } = [];
}

internal sealed record ProjectTreeEntry(
    int Index,
    int Depth,
    string Name,
    string RelativePath,
    bool IsDirectory);

internal sealed record UpmPackageEntry(
    int Index,
    string PackageId,
    string DisplayName,
    string Version,
    string Source,
    string? LatestCompatibleVersion,
    bool IsOutdated,
    bool IsDeprecated,
    bool IsPreview);
