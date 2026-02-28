using System.Text.Json;

internal sealed class RecentProjectHistoryService
{
    private const int MaxStoredEntries = 100;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    public bool TryRecordProjectOpen(string projectPath, DateTimeOffset openedAtUtc, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(projectPath))
        {
            error = "project path is empty";
            return false;
        }

        if (!TryLoadEntries(out var entries, out error))
        {
            return false;
        }

        entries.RemoveAll(entry => string.Equals(entry.ProjectPath, projectPath, StringComparison.Ordinal));
        entries.Insert(0, new RecentProjectEntry(projectPath, openedAtUtc));

        if (entries.Count > MaxStoredEntries)
        {
            entries = entries.Take(MaxStoredEntries).ToList();
        }

        return TrySaveEntries(entries, out error);
    }

    public bool TryGetRecentProjects(int maxCount, out List<RecentProjectEntry> entries, out string? error)
    {
        entries = [];
        error = null;

        if (maxCount <= 0)
        {
            error = "max count must be greater than zero";
            return false;
        }

        if (!TryLoadEntries(out var loadedEntries, out error))
        {
            return false;
        }

        entries = loadedEntries
            .OrderByDescending(entry => entry.LastOpenedUtc)
            .Take(maxCount)
            .ToList();
        return true;
    }

    private bool TryLoadEntries(out List<RecentProjectEntry> entries, out string? error)
    {
        entries = [];
        error = null;
        var historyPath = GetHistoryPath();

        if (!File.Exists(historyPath))
        {
            return true;
        }

        try
        {
            var raw = File.ReadAllText(historyPath);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return true;
            }

            var parsed = JsonSerializer.Deserialize<List<RecentProjectEntry>>(raw);
            if (parsed is null)
            {
                return true;
            }

            entries = parsed
                .Where(entry => !string.IsNullOrWhiteSpace(entry.ProjectPath))
                .ToList();
            return true;
        }
        catch (Exception ex)
        {
            error = $"failed to read recent project history ({ex.Message})";
            return false;
        }
    }

    private bool TrySaveEntries(List<RecentProjectEntry> entries, out string? error)
    {
        error = null;
        var historyPath = GetHistoryPath();
        var directory = Path.GetDirectoryName(historyPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            error = "failed to resolve recent project history directory";
            return false;
        }

        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(historyPath, JsonSerializer.Serialize(entries, _jsonOptions) + Environment.NewLine);
            return true;
        }
        catch (Exception ex)
        {
            error = $"failed to write recent project history ({ex.Message})";
            return false;
        }
    }

    private static string GetHistoryPath()
    {
        var overridePath = Environment.GetEnvironmentVariable("UNIFOCL_RECENT_PROJECTS_PATH");
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return Path.GetFullPath(overridePath);
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".unifocl", "recent-projects.json");
    }
}
