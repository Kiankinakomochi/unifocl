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
}
