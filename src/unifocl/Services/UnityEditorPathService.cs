using System.Text.Json;

internal static class UnityEditorPathService
{
    private sealed class UnityEditorStore
    {
        public string? DefaultEditorPath { get; set; }
        public Dictionary<string, string> ProjectEditors { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    internal sealed record UnityEditorInstallation(string Version, string EditorPath);

    public static IReadOnlyList<UnityEditorInstallation> DetectInstalledEditors(out string? hubRoot)
    {
        hubRoot = ResolveUnityHubRoot();
        var discovered = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(hubRoot))
        {
            var editorRoot = Path.Combine(hubRoot, "Editor");
            if (Directory.Exists(editorRoot))
            {
                foreach (var versionDirectory in Directory.EnumerateDirectories(editorRoot))
                {
                    var version = Path.GetFileName(versionDirectory);
                    if (string.IsNullOrWhiteSpace(version))
                    {
                        continue;
                    }

                    var editorPath = ResolveEditorExecutablePath(versionDirectory);
                    if (string.IsNullOrWhiteSpace(editorPath) || !File.Exists(editorPath))
                    {
                        continue;
                    }

                    discovered[version] = editorPath;
                }
            }
        }

        var fromEnv = Environment.GetEnvironmentVariable("UNITY_PATH");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            var normalized = Path.GetFullPath(fromEnv);
            if (File.Exists(normalized))
            {
                var envVersion = TryInferVersionFromUnityPath(normalized);
                if (!string.IsNullOrWhiteSpace(envVersion) && !discovered.ContainsKey(envVersion))
                {
                    discovered[envVersion] = normalized;
                }
            }
        }

        return discovered
            .OrderByDescending(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .Select(entry => new UnityEditorInstallation(entry.Key, entry.Value))
            .ToList();
    }

    public static bool TryReadProjectEditorVersion(string projectPath, out string version, out string? error)
    {
        version = string.Empty;
        error = null;
        try
        {
            var projectVersionPath = Path.Combine(projectPath, "ProjectSettings", "ProjectVersion.txt");
            if (!File.Exists(projectVersionPath))
            {
                error = "ProjectVersion.txt not found";
                return false;
            }

            foreach (var raw in File.ReadLines(projectVersionPath))
            {
                var line = raw.Trim();
                if (!line.StartsWith("m_EditorVersion:", StringComparison.Ordinal))
                {
                    continue;
                }

                var parsed = line["m_EditorVersion:".Length..].Trim();
                if (string.IsNullOrWhiteSpace(parsed))
                {
                    break;
                }

                version = parsed;
                return true;
            }

            error = "m_EditorVersion not found in ProjectVersion.txt";
            return false;
        }
        catch (Exception ex)
        {
            error = $"failed to read ProjectVersion.txt ({ex.Message})";
            return false;
        }
    }

    public static bool TryResolveEditorForProject(string projectPath, out string editorPath, out string version, out string? error)
    {
        editorPath = string.Empty;
        version = string.Empty;
        error = null;

        if (TryReadProjectEditorVersion(projectPath, out version, out error))
        {
            var requiredVersion = version;
            if (TryGetProjectEditorPath(projectPath, out var persistedPath))
            {
                var persistedVersion = TryInferVersionFromUnityPath(persistedPath);
                if (string.Equals(persistedVersion, requiredVersion, StringComparison.OrdinalIgnoreCase))
                {
                    editorPath = persistedPath;
                    return true;
                }
            }

            var installed = DetectInstalledEditors(out _);
            var exact = installed.FirstOrDefault(x => x.Version.Equals(requiredVersion, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
            {
                editorPath = exact.EditorPath;
                TrySaveProjectEditorPath(projectPath, editorPath, out _);
                return true;
            }

            error = installed.Count == 0
                ? $"Unity editor version {requiredVersion} is required by project, but no Unity editors were detected."
                : $"Unity editor version {requiredVersion} is required by project, but it was not found on this machine.";
            return false;
        }

        return false;
    }

    public static bool TryGetProjectEditorPath(string projectPath, out string editorPath)
    {
        editorPath = string.Empty;
        if (!TryLoadStore(out var store, out _))
        {
            return false;
        }

        var normalizedProjectPath = NormalizePath(projectPath);
        if (string.IsNullOrWhiteSpace(normalizedProjectPath))
        {
            return false;
        }

        if (!store.ProjectEditors.TryGetValue(normalizedProjectPath, out var saved))
        {
            return false;
        }

        if (!File.Exists(saved))
        {
            return false;
        }

        editorPath = saved;
        return true;
    }

    public static bool TrySaveProjectEditorPath(string projectPath, string editorPath, out string? error)
    {
        error = null;
        if (!File.Exists(editorPath))
        {
            error = $"Unity editor executable not found: {editorPath}";
            return false;
        }

        if (!TryLoadStore(out var store, out var loadError))
        {
            error = loadError;
            return false;
        }

        var normalizedProjectPath = NormalizePath(projectPath);
        var normalizedEditorPath = NormalizePath(editorPath);
        if (string.IsNullOrWhiteSpace(normalizedProjectPath) || string.IsNullOrWhiteSpace(normalizedEditorPath))
        {
            error = "invalid project/editor path";
            return false;
        }

        store.ProjectEditors[normalizedProjectPath] = normalizedEditorPath;
        return TrySaveStore(store, out error);
    }

    public static bool TryGetDefaultEditorPath(out string editorPath)
    {
        editorPath = string.Empty;
        if (!TryLoadStore(out var store, out _))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(store.DefaultEditorPath) || !File.Exists(store.DefaultEditorPath))
        {
            return false;
        }

        editorPath = store.DefaultEditorPath;
        return true;
    }

    public static bool TrySetDefaultEditorPath(string editorPath, out string? error)
    {
        error = null;
        var normalized = NormalizePath(editorPath);
        if (string.IsNullOrWhiteSpace(normalized) || !File.Exists(normalized))
        {
            error = $"Unity editor executable not found: {editorPath}";
            return false;
        }

        if (!TryLoadStore(out var store, out var loadError))
        {
            error = loadError;
            return false;
        }

        store.DefaultEditorPath = normalized;
        return TrySaveStore(store, out error);
    }

    public static string? TryInferVersionFromUnityPath(string unityPath)
    {
        try
        {
            var normalized = NormalizePath(unityPath);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return null;
            }

            var segments = normalized.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var editorIndex = Array.FindLastIndex(segments, x => x.Equals("Editor", StringComparison.OrdinalIgnoreCase));
            if (editorIndex > 0 && editorIndex - 1 < segments.Length)
            {
                var candidate = segments[editorIndex - 1];
                if (!string.IsNullOrWhiteSpace(candidate) && char.IsDigit(candidate[0]))
                {
                    return candidate;
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private static string? ResolveUnityHubRoot()
    {
        var fromEnv = Environment.GetEnvironmentVariable("UNITY_HUB_PATH");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            var normalized = Path.GetFullPath(fromEnv);
            if (Directory.Exists(normalized))
            {
                if (Path.GetFileName(normalized).Equals("Editor", StringComparison.OrdinalIgnoreCase))
                {
                    return Directory.GetParent(normalized)?.FullName;
                }

                if (Directory.Exists(Path.Combine(normalized, "Editor")))
                {
                    return normalized;
                }
            }
        }

        var candidates = new List<string>();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS())
        {
            candidates.Add("/Applications/Unity/Hub");
            if (!string.IsNullOrWhiteSpace(home))
            {
                candidates.Add(Path.Combine(home, "Applications", "Unity", "Hub"));
            }
        }
        else if (OperatingSystem.IsWindows())
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrWhiteSpace(programFiles))
            {
                candidates.Add(Path.Combine(programFiles, "Unity", "Hub"));
            }

            var programFilesX86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
            if (!string.IsNullOrWhiteSpace(programFilesX86))
            {
                candidates.Add(Path.Combine(programFilesX86, "Unity", "Hub"));
            }
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(home))
            {
                candidates.Add(Path.Combine(home, "Unity", "Hub"));
            }
        }

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Directory.Exists(Path.Combine(candidate, "Editor")))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string? ResolveEditorExecutablePath(string versionDirectory)
    {
        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(versionDirectory, "Unity.app", "Contents", "MacOS", "Unity");
        }

        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(versionDirectory, "Editor", "Unity.exe");
        }

        return Path.Combine(versionDirectory, "Editor", "Unity");
    }

    private static bool TryLoadStore(out UnityEditorStore store, out string? error)
    {
        store = new UnityEditorStore();
        error = null;
        var path = GetStorePath();

        try
        {
            if (!File.Exists(path))
            {
                return true;
            }

            var json = File.ReadAllText(path);
            var parsed = JsonSerializer.Deserialize<UnityEditorStore>(json);
            if (parsed is null)
            {
                error = "invalid unity editor store format";
                return false;
            }

            parsed.ProjectEditors ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            store = parsed;
            return true;
        }
        catch (Exception ex)
        {
            error = $"failed to load unity editor store ({ex.Message})";
            return false;
        }
    }

    private static bool TrySaveStore(UnityEditorStore store, out string? error)
    {
        error = null;
        var path = GetStorePath();

        try
        {
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory))
            {
                error = "failed to resolve store directory";
                return false;
            }

            Directory.CreateDirectory(directory);
            var payload = JsonSerializer.Serialize(store, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, payload + Environment.NewLine);
            return true;
        }
        catch (Exception ex)
        {
            error = $"failed to save unity editor store ({ex.Message})";
            return false;
        }
    }

    private static string GetStorePath()
    {
        var explicitConfigPath = Environment.GetEnvironmentVariable("UNIFOCL_CONFIG_PATH");
        if (!string.IsNullOrWhiteSpace(explicitConfigPath))
        {
            var configDirectory = Path.GetDirectoryName(Path.GetFullPath(explicitConfigPath));
            if (!string.IsNullOrWhiteSpace(configDirectory))
            {
                return Path.Combine(configDirectory, "unity-editors.json");
            }
        }

        var configRoot = Environment.GetEnvironmentVariable("UNIFOCL_CONFIG_ROOT");
        if (!string.IsNullOrWhiteSpace(configRoot))
        {
            return Path.Combine(Path.GetFullPath(configRoot), "unity-editors.json");
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".unifocl", "unity-editors.json");
    }

    private static string NormalizePath(string path)
    {
        return string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetFullPath(path);
    }
}
