/// <summary>
/// Clones a Unity project directory to an isolated path for use by a separate agent session.
/// Copies Assets/, Packages/, ProjectSettings/, UserSettings/ (always) and optionally Library/
/// so the cloned project can open without re-importing.
/// </summary>
internal static class ProjectCloneService
{
    private static readonly string[] RequiredFolders = ["Assets", "Packages", "ProjectSettings"];
    private static readonly string[] OptionalFolders = ["UserSettings"];

    // Folders that must never be carried across — they are stale/volatile by definition.
    private static readonly HashSet<string> SkipFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Temp", "Logs", "obj", ".unifocl", "Library"
    };

    internal sealed record CloneResult(
        bool Ok,
        string? ClonedPath,
        bool LibraryCopied,
        long TotalBytesCopied,
        string Message);

    /// <summary>
    /// Clones <paramref name="sourcePath"/> to <paramref name="destPath"/>.
    /// Returns a <see cref="CloneResult"/> describing the outcome.
    /// </summary>
    public static CloneResult Clone(
        string sourcePath,
        string destPath,
        bool seedLibrary = true,
        Action<string>? log = null)
    {
        sourcePath = Path.GetFullPath(sourcePath);
        destPath   = Path.GetFullPath(destPath);

        if (!Directory.Exists(sourcePath))
        {
            return Fail($"source path does not exist: {sourcePath}");
        }

        if (!Directory.Exists(Path.Combine(sourcePath, "Assets"))
            || !Directory.Exists(Path.Combine(sourcePath, "ProjectSettings")))
        {
            return Fail("source is not a Unity project (missing Assets/ or ProjectSettings/)");
        }

        if (string.Equals(sourcePath, destPath, StringComparison.OrdinalIgnoreCase))
        {
            return Fail("source and destination paths are the same");
        }

        if (Directory.Exists(destPath) && Directory.EnumerateFileSystemEntries(destPath).Any())
        {
            return Fail($"destination already exists and is not empty: {destPath}");
        }

        try
        {
            Directory.CreateDirectory(destPath);
        }
        catch (Exception ex)
        {
            return Fail($"could not create destination directory: {ex.Message}");
        }

        long totalBytes = 0;

        // Required and optional content folders
        foreach (var folder in RequiredFolders.Concat(OptionalFolders))
        {
            var src = Path.Combine(sourcePath, folder);
            if (!Directory.Exists(src))
            {
                continue;
            }

            log?.Invoke($"[grey]clone[/]: copying {folder}/");
            totalBytes += CopyDirectory(src, Path.Combine(destPath, folder));
        }

        // Library seeding: copies pre-compiled cache so Unity doesn't re-import on first open
        var libraryCopied = false;
        if (seedLibrary)
        {
            var srcLib = Path.Combine(sourcePath, "Library");
            if (Directory.Exists(srcLib))
            {
                log?.Invoke("[grey]clone[/]: seeding Library/ cache");
                totalBytes += CopyDirectory(srcLib, Path.Combine(destPath, "Library"));
                libraryCopied = true;
            }
            else
            {
                log?.Invoke("[yellow]clone[/]: Library/ not found in source — skipping seed (Unity will re-import on first open)");
            }
        }

        var sizeMb = totalBytes / 1_048_576.0;
        var summary = $"cloned to {destPath} ({sizeMb:F1} MB){(libraryCopied ? ", Library seeded" : "")}";
        log?.Invoke($"[green]clone[/]: {summary}");
        return new CloneResult(true, destPath, libraryCopied, totalBytes, summary);
    }

    private static long CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        long bytes = 0;

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target   = Path.Combine(dest, relative);
            var dir      = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.Copy(file, target, overwrite: false);
            bytes += new FileInfo(target).Length;
        }

        return bytes;
    }

    private static CloneResult Fail(string message)
        => new(false, null, false, 0, message);
}
