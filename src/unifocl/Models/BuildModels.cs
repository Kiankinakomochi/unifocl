internal sealed record BuildSnapshotResult(
    string SnapshotPath, string Timestamp, int PackageCount, bool LockfilePresent);

internal sealed record BuildPreflightResult(
    bool Passed, int ErrorCount, int WarningCount,
    ValidateResult? SceneList, ValidateResult? BuildSettings, ValidateResult? Packages);
