using System.Text.Json;

internal sealed class AssetIndexDaemonBridge : IDisposable
{
    private readonly object _sync = new();
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _assetsRoot;
    private readonly Dictionary<int, string> _entries = [];
    private FileSystemWatcher? _watcher;
    private int _revision = 1;
    private bool _dirty = true;

    public AssetIndexDaemonBridge(string? projectPath)
    {
        var root = string.IsNullOrWhiteSpace(projectPath)
            ? Directory.GetCurrentDirectory()
            : projectPath;
        _assetsRoot = Path.Combine(root, "Assets");
        StartWatcher();
    }

    public bool TryHandle(string? commandLine, out string response)
    {
        response = string.Empty;
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return false;
        }

        var command = commandLine.Trim();
        if (string.Equals(command, "ASSET_INDEX_GET", StringComparison.Ordinal))
        {
            response = BuildSyncJson(null);
            return true;
        }

        if (!command.StartsWith("ASSET_INDEX_SYNC ", StringComparison.Ordinal))
        {
            return false;
        }

        var rawRevision = command["ASSET_INDEX_SYNC ".Length..];
        var knownRevision = int.TryParse(rawRevision, out var revision) ? revision : (int?)null;
        response = BuildSyncJson(knownRevision);
        return true;
    }

    public void Dispose()
    {
        if (_watcher is null)
        {
            return;
        }

        _watcher.EnableRaisingEvents = false;
        _watcher.Created -= OnAssetTreeChanged;
        _watcher.Changed -= OnAssetTreeChanged;
        _watcher.Deleted -= OnAssetTreeChanged;
        _watcher.Renamed -= OnAssetTreeRenamed;
        _watcher.Dispose();
        _watcher = null;
    }

    private string BuildSyncJson(int? knownRevision)
    {
        lock (_sync)
        {
            if (_dirty)
            {
                RebuildUnsafe();
            }

            if (knownRevision is not null && knownRevision.Value == _revision)
            {
                return JsonSerializer.Serialize(new AssetIndexSyncResponseDto(_revision, true, []), _jsonOptions);
            }

            var entries = _entries
                .Select(kvp => new AssetIndexEntryDto(kvp.Key, kvp.Value))
                .OrderBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return JsonSerializer.Serialize(new AssetIndexSyncResponseDto(_revision, false, entries), _jsonOptions);
        }
    }

    private void StartWatcher()
    {
        if (!Directory.Exists(_assetsRoot))
        {
            return;
        }

        _watcher = new FileSystemWatcher(_assetsRoot)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.DirectoryName | NotifyFilters.FileName | NotifyFilters.LastWrite,
            EnableRaisingEvents = true
        };
        _watcher.Created += OnAssetTreeChanged;
        _watcher.Changed += OnAssetTreeChanged;
        _watcher.Deleted += OnAssetTreeChanged;
        _watcher.Renamed += OnAssetTreeRenamed;
    }

    private void OnAssetTreeChanged(object sender, FileSystemEventArgs e)
    {
        MarkDirty();
    }

    private void OnAssetTreeRenamed(object sender, RenamedEventArgs e)
    {
        MarkDirty();
    }

    private void MarkDirty()
    {
        lock (_sync)
        {
            _dirty = true;
            _revision++;
        }
    }

    private void RebuildUnsafe()
    {
        _entries.Clear();
        if (Directory.Exists(_assetsRoot))
        {
            foreach (var file in Directory.EnumerateFiles(_assetsRoot, "*", SearchOption.AllDirectories))
            {
                if (file.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var relative = "Assets/" + Path.GetRelativePath(_assetsRoot, file).Replace('\\', '/');
                _entries[ComputeStableId(relative)] = relative;
            }
        }

        _dirty = false;
    }

    private static int ComputeStableId(string path)
    {
        unchecked
        {
            var hash = 2166136261u;
            foreach (var ch in path)
            {
                hash ^= char.ToLowerInvariant(ch);
                hash *= 16777619;
            }

            return (int)(hash & 0x7FFFFFFF);
        }
    }
}

