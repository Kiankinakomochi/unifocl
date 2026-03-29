using System.Text.Json;

/// <summary>
/// Persists test run history to <c>.unifocl-runtime/test-history/&lt;slug&gt;.json</c>.
/// Used by <c>test.flaky-report</c> to identify tests with mixed outcomes across runs.
/// Thread-safe via a per-file lock.
/// </summary>
internal sealed class TestHistoryStore
{
    private const int MaxEntries = 100;

    private static readonly JsonSerializerOptions SerializeOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };
    private static readonly JsonSerializerOptions DeserializeOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    // One lock per store path to allow different projects to append concurrently.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> FileLocks = new();

    private readonly string _storePath;

    public TestHistoryStore(string projectPath)
    {
        var runtimeDir = Path.Combine(projectPath, ".unifocl-runtime", "test-history");
        Directory.CreateDirectory(runtimeDir);

        // Slug: last two path segments joined by underscore, sanitised.
        var slug = BuildSlug(projectPath);
        _storePath = Path.Combine(runtimeDir, $"{slug}.json");
    }

    /// <summary>
    /// Appends one run to the store. Trims the rolling window to <see cref="MaxEntries"/>.
    /// </summary>
    public void Append(TestHistoryRecord record)
    {
        var fileLock = FileLocks.GetOrAdd(_storePath, _ => new object());
        lock (fileLock)
        {
            var entries = ReadAll();
            entries.Add(record);

            // Rolling window — keep only the most recent N entries.
            if (entries.Count > MaxEntries)
            {
                entries.RemoveRange(0, entries.Count - MaxEntries);
            }

            var wrapper = new HistoryFile { Entries = entries };
            File.WriteAllText(_storePath, JsonSerializer.Serialize(wrapper, SerializeOptions));
        }
    }

    /// <summary>
    /// Returns all stored runs, oldest-first.
    /// </summary>
    public List<TestHistoryRecord> ReadAll()
    {
        if (!File.Exists(_storePath))
        {
            return [];
        }

        try
        {
            var json = File.ReadAllText(_storePath);
            var wrapper = JsonSerializer.Deserialize<HistoryFile>(json, DeserializeOptions);
            return wrapper?.Entries ?? [];
        }
        catch
        {
            return [];
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string BuildSlug(string projectPath)
    {
        var normalized = projectPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parts = normalized.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries);

        var raw = parts.Length >= 2
            ? string.Join("_", parts[^2], parts[^1])
            : parts.Length == 1 ? parts[0] : "project";

        // Sanitise to [a-z0-9_-]
        var chars = raw.ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '_')
            .ToArray();
        return new string(chars);
    }

    // ── Inner types (for JSON serialisation) ─────────────────────────────────

    private sealed class HistoryFile
    {
        public List<TestHistoryRecord> Entries { get; set; } = [];
    }
}
