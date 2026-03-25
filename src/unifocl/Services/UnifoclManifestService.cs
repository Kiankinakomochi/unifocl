using System.Text.Json;

/// <summary>
/// Loads the compile-time tool manifest (.local/unifocl-manifest.json) for a Unity project
/// and tracks which tool categories the LLM has activated in the current MCP session.
/// </summary>
public sealed class UnifoclManifestService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private UnifoclManifest? _manifest;
    private string? _loadedProjectPath;
    private readonly HashSet<string> _activeCategories = new(StringComparer.OrdinalIgnoreCase);

    // Indexed for O(1) lookup: tool name → (category name, tool manifest)
    private readonly Dictionary<string, (string Category, UnifoclToolManifest Tool)> _toolIndex
        = new(StringComparer.OrdinalIgnoreCase);

    public bool IsManifestLoaded => _manifest is not null;

    /// <summary>
    /// Ensures the manifest is loaded for the given project path.
    /// If the project path hasn't changed and the manifest is already loaded, this is a no-op.
    /// Automatically called by meta-tools when no explicit LoadFromProject call has been made.
    /// </summary>
    public void EnsureLoaded(string projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            return;
        }

        if (_manifest is not null
            && string.Equals(_loadedProjectPath, projectPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        LoadFromProject(projectPath);
    }

    /// <summary>
    /// Forces a reload of the manifest from disk, bypassing the EnsureLoaded idempotency guard.
    /// Use after Unity recompiles to pick up newly registered [UnifoclCommand] methods.
    /// </summary>
    public void ForceReload(string projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath)) return;
        LoadFromProject(projectPath);
    }

    /// <summary>
    /// Resolves the active Unity project path for the current MCP server session.
    /// Checks the UNIFOCL_UNITY_PROJECT_PATH environment variable first,
    /// then falls back to scanning the daemon runtime registry.
    /// Returns empty string if no project is active.
    /// </summary>
    public static string ResolveActiveProjectPath()
    {
        var envPath = Environment.GetEnvironmentVariable("UNIFOCL_UNITY_PROJECT_PATH");
        if (!string.IsNullOrWhiteSpace(envPath) && Directory.Exists(envPath))
        {
            return envPath;
        }

        // Fallback: find the first live daemon's project path via the runtime registry
        var runtimeRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".unifocl-runtime");
        if (!Directory.Exists(runtimeRoot))
        {
            return string.Empty;
        }

        var daemonRuntime = new DaemonRuntime(runtimeRoot);
        var instance = daemonRuntime.GetAll().FirstOrDefault(d => !string.IsNullOrWhiteSpace(d.ProjectPath));
        return instance?.ProjectPath ?? string.Empty;
    }

    /// <summary>
    /// Loads (or reloads) the manifest from the project's .local directory.
    /// Missing or malformed files are silently ignored — the service falls back to an empty state.
    /// </summary>
    public void LoadFromProject(string projectPath)
    {
        _manifest = null;
        _loadedProjectPath = null;
        _toolIndex.Clear();

        if (string.IsNullOrWhiteSpace(projectPath))
        {
            return;
        }

        _loadedProjectPath = projectPath;

        var manifestPath = Path.Combine(projectPath, ".local", "unifocl-manifest.json");
        if (!File.Exists(manifestPath))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(manifestPath);
            _manifest = JsonSerializer.Deserialize<UnifoclManifest>(json, JsonOptions);
            if (_manifest is null)
            {
                return;
            }

            foreach (var category in _manifest.Categories)
            {
                foreach (var tool in category.Tools)
                {
                    _toolIndex[tool.Name] = (category.Name, tool);
                }
            }
        }
        catch (Exception ex)
        {
            // Graceful degradation: log but never throw during daemon boot
            Console.Error.WriteLine($"[unifocl] failed to load manifest at '{manifestPath}': {ex.Message}");
            _manifest = null;
            _toolIndex.Clear();
        }
    }

    /// <summary>Returns all category names present in the manifest.</summary>
    public IReadOnlyList<string> GetAllCategoryNames()
    {
        if (_manifest is null)
        {
            return [];
        }

        return _manifest.Categories.ConvertAll(static c => c.Name);
    }

    /// <summary>Returns per-category info (name, tool count, active state).</summary>
    public IReadOnlyList<(string Name, int ToolCount, bool Active)> GetCategoryInfos()
    {
        if (_manifest is null)
        {
            return [];
        }

        return _manifest.Categories.ConvertAll(c =>
            (c.Name, c.Tools.Count, _activeCategories.Contains(c.Name)));
    }

    /// <summary>
    /// Activates a category. Returns true if the category exists in the manifest and was newly added.
    /// Returns false if the category is unknown or was already active.
    /// </summary>
    public bool LoadCategory(string name)
    {
        if (_manifest is null || string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var exists = _manifest.Categories.Exists(
            c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

        if (!exists)
        {
            return false;
        }

        return _activeCategories.Add(name);
    }

    /// <summary>
    /// Deactivates a category. Returns true if it was present and has been removed.
    /// </summary>
    public bool UnloadCategory(string name)
    {
        return !string.IsNullOrWhiteSpace(name) && _activeCategories.Remove(name);
    }

    /// <summary>Returns all tools that belong to currently active categories.</summary>
    public IReadOnlyList<UnifoclToolManifest> GetActiveTools()
    {
        if (_manifest is null || _activeCategories.Count == 0)
        {
            return [];
        }

        var result = new List<UnifoclToolManifest>();
        foreach (var category in _manifest.Categories)
        {
            if (_activeCategories.Contains(category.Name))
            {
                result.AddRange(category.Tools);
            }
        }

        return result;
    }

    /// <summary>Returns all tools that belong to the named category, or an empty list if the category is not found.</summary>
    public IReadOnlyList<UnifoclToolManifest> GetToolsForCategory(string categoryName)
    {
        if (_manifest is null || string.IsNullOrWhiteSpace(categoryName))
        {
            return [];
        }

        var category = _manifest.Categories.Find(
            c => string.Equals(c.Name, categoryName, StringComparison.OrdinalIgnoreCase));

        return category?.Tools ?? (IReadOnlyList<UnifoclToolManifest>)[];
    }

    /// <summary>
    /// Locates a tool by name across all categories.
    /// Returns false if the manifest is not loaded or the tool does not exist.
    /// </summary>
    public bool TryFindTool(string toolName, out UnifoclToolManifest? tool, out string categoryName)
    {
        tool = null;
        categoryName = string.Empty;

        if (!_toolIndex.TryGetValue(toolName, out var entry))
        {
            return false;
        }

        tool = entry.Tool;
        categoryName = entry.Category;
        return true;
    }
}
