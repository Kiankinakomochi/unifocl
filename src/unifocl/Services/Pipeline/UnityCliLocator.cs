/// <summary>
/// Finds the official Unity CLI binary (<c>unity</c>), which unifocl uses for every Editor call.
/// </summary>
internal static class UnityCliLocator
{
    /// <summary>Overrides discovery with an explicit path to the binary.</summary>
    public const string OverrideEnvironmentVariable = "UNIFOCL_UNITY_CLI";

    public const string InstallHint =
        "The official Unity CLI was not found. Install it, put `unity` on PATH, " +
        $"or set {OverrideEnvironmentVariable} to its path.";

    private static readonly object Gate = new();
    private static string? _cached;

    /// <summary>The binary's path, or <c>null</c> when it isn't installed.</summary>
    public static string? Locate()
    {
        lock (Gate)
        {
            if (_cached is not null && File.Exists(_cached))
            {
                return _cached;
            }

            _cached = Resolve(
                Environment.GetEnvironmentVariable(OverrideEnvironmentVariable),
                Environment.GetEnvironmentVariable("PATH"),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                OperatingSystem.IsWindows(),
                File.Exists);
            return _cached;
        }
    }

    /// <summary>
    /// The lookup order, without process state so it can be tested: the override, then PATH, then the
    /// installer's default location (<c>~/.unity/bin</c>).
    /// </summary>
    internal static string? Resolve(
        string? overridePath,
        string? pathVariable,
        string? userProfile,
        bool isWindows,
        Func<string, bool> fileExists)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return fileExists(overridePath) ? overridePath : null;
        }

        var names = isWindows ? new[] { "unity.exe", "unity.cmd" } : new[] { "unity" };
        var separator = isWindows ? ';' : ':';
        foreach (var directory in (pathVariable ?? string.Empty).Split(separator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in names)
            {
                var candidate = Path.Combine(directory.Trim(), name);
                if (fileExists(candidate))
                {
                    return candidate;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            foreach (var name in names)
            {
                var candidate = Path.Combine(userProfile, ".unity", "bin", name);
                if (fileExists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}
