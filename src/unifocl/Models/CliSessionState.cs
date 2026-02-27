internal sealed class CliSessionState
{
    public int? AttachedPort { get; set; }
    public string? CurrentProjectPath { get; set; }
    public DateTimeOffset? LastOpenedUtc { get; set; }
    public ProjectViewState ProjectView { get; } = new();
}
