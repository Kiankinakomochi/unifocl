using Spectre.Console;
using System.Text.Json;
using System.Text.RegularExpressions;

internal static class ProjectVcsProfileService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private const string VcsModeNone = "none";
    private const string VcsModeUvcsAll = "uvcs_all";
    private const string VcsModeUvcsHybridGitIgnore = "uvcs_hybrid_gitignore";
    private const string OwnerUvcs = "uvcs";
    private const string OwnerGit = "git";

    public static bool IsProjectMutationCommand(string input)
    {
        var tokens = ProjectViewServiceUtils.Tokenize(input);
        if (tokens.Count == 0)
        {
            return false;
        }

        var head = tokens[0].ToLowerInvariant();
        return head is "mk" or "make" or "rename" or "rm" or "remove";
    }

    public static VcsMutationGuardResult EnsureInteractiveMutationReady(string projectPath, CliSessionState session)
    {
        var detection = DetectProjectMode(projectPath);
        if (detection.Mode == VcsModeNone)
        {
            return VcsMutationGuardResult.Allow();
        }

        var configured = TryReadConfig(projectPath);
        if (configured?.SetupComplete == true && string.Equals(configured.DetectedMode, detection.Mode, StringComparison.Ordinal))
        {
            return VcsMutationGuardResult.Allow();
        }

        if (session.VcsSetupDeclinedForProject
            && string.Equals(session.VcsSetupPromptProjectPath, projectPath, StringComparison.OrdinalIgnoreCase))
        {
            return VcsMutationGuardResult.Block(
                $"VCS setup is required for {detection.Mode}. Previous setup prompt was declined for this project.");
        }

        if (CliRuntimeState.SuppressConsoleOutput)
        {
            return VcsMutationGuardResult.Block(
                $"VCS setup is required for {detection.Mode}. Open interactive mode and approve setup before mutating.");
        }

        session.VcsSetupPromptProjectPath = projectPath;
        session.VcsSetupDeclinedForProject = false;

        var shouldSetup = AnsiConsole.Confirm(
            $"Detected [yellow]{Markup.Escape(detection.Mode)}[/] for this project. Configure unifocl VCS safety now?",
            defaultValue: true);
        if (!shouldSetup)
        {
            session.VcsSetupDeclinedForProject = true;
            return VcsMutationGuardResult.Block(
                $"Mutation aborted. VCS setup is required for {detection.Mode} before project file mutations.");
        }

        var writeResult = WriteConfig(projectPath, detection);
        if (!writeResult.Ok)
        {
            return VcsMutationGuardResult.Block(writeResult.Error ?? "failed to persist VCS setup");
        }

        return VcsMutationGuardResult.Allow(
            $"Configured project VCS safety mode: {detection.Mode}");
    }

    public static VcsMutationGuardResult EvaluateMutationGuardForAgentic(string projectPath)
    {
        var detection = DetectProjectMode(projectPath);
        if (detection.Mode == VcsModeNone)
        {
            return VcsMutationGuardResult.Allow();
        }

        var configured = TryReadConfig(projectPath);
        if (configured?.SetupComplete == true && string.Equals(configured.DetectedMode, detection.Mode, StringComparison.Ordinal))
        {
            return VcsMutationGuardResult.Allow();
        }

        return VcsMutationGuardResult.Block(
            $"VCS setup is required for {detection.Mode}. Run an interactive project mutation once and approve setup.");
    }

    public static ProjectCommandRequestDto EnrichProjectMutationIntent(
        ProjectCommandRequestDto request,
        string projectPath,
        params string?[] candidatePaths)
    {
        var intentReady = MutationIntentFactory.EnsureProjectIntent(request);
        if (intentReady.Intent is null)
        {
            return intentReady;
        }

        var detection = DetectProjectMode(projectPath);
        if (detection.Mode == VcsModeNone)
        {
            return intentReady;
        }

        var modeForIntent = TryReadConfig(projectPath)?.DetectedMode ?? detection.Mode;
        var ownedPaths = candidatePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => NormalizeRelativePath(projectPath, path!))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path =>
            {
                var owner = ResolveOwner(projectPath, modeForIntent, path);
                return new MutationIntentVcsPathDto(path, owner, RequiresCheckout(owner));
            })
            .ToList();

        var flags = intentReady.Intent.Flags with
        {
            VcsMode = modeForIntent,
            VcsOwnedPaths = ownedPaths
        };

        return intentReady with
        {
            Intent = intentReady.Intent with
            {
                Flags = flags
            }
        };
    }

    private static string ResolveOwner(string projectPath, string mode, string relativePath)
    {
        if (!string.Equals(mode, VcsModeUvcsHybridGitIgnore, StringComparison.Ordinal))
        {
            return OwnerUvcs;
        }

        return IsGitOwnedByGitIgnore(projectPath, relativePath)
            ? OwnerGit
            : OwnerUvcs;
    }

    private static bool RequiresCheckout(string owner)
    {
        return string.Equals(owner, OwnerUvcs, StringComparison.Ordinal);
    }

    private static ProjectVcsDetection DetectProjectMode(string projectPath)
    {
        var hasGit = Directory.Exists(Path.Combine(projectPath, ".git")) || File.Exists(Path.Combine(projectPath, ".git"));
        var hasActiveGitIgnore = HasActiveGitIgnoreRules(projectPath);
        var hasUvcs = HasUvcsMarkers(projectPath);

        var mode = hasUvcs
            ? (hasGit && hasActiveGitIgnore ? VcsModeUvcsHybridGitIgnore : VcsModeUvcsAll)
            : VcsModeNone;

        var signature = BuildDetectionSignature(projectPath, hasGit, hasActiveGitIgnore, hasUvcs, mode);
        return new ProjectVcsDetection(mode, signature);
    }

    private static bool HasUvcsMarkers(string projectPath)
    {
        var versionControlSettings = Path.Combine(projectPath, "ProjectSettings", "VersionControlSettings.asset");
        if (File.Exists(versionControlSettings))
        {
            try
            {
                var text = File.ReadAllText(versionControlSettings);
                if (text.Contains("Plastic", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("Unity Version Control", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("Version Control", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch
            {
            }
        }

        return Directory.Exists(Path.Combine(projectPath, ".plastic"))
               || File.Exists(Path.Combine(projectPath, "plastic.workspace"));
    }

    private static bool HasActiveGitIgnoreRules(string projectPath)
    {
        var gitIgnorePath = Path.Combine(projectPath, ".gitignore");
        if (!File.Exists(gitIgnorePath))
        {
            return false;
        }

        try
        {
            return File.ReadLines(gitIgnorePath).Any(IsActiveGitIgnoreLine);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsGitOwnedByGitIgnore(string projectPath, string relativePath)
    {
        var gitIgnorePath = Path.Combine(projectPath, ".gitignore");
        if (!File.Exists(gitIgnorePath))
        {
            return false;
        }

        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        var matched = false;
        try
        {
            foreach (var raw in File.ReadLines(gitIgnorePath))
            {
                var line = raw.Trim();
                if (!IsActiveGitIgnoreLine(line))
                {
                    continue;
                }

                var negated = line.StartsWith('!');
                if (negated)
                {
                    line = line[1..].Trim();
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }
                }

                if (!TryBuildGitIgnoreRegex(line, out var regex))
                {
                    continue;
                }

                if (!regex.IsMatch(normalized))
                {
                    continue;
                }

                matched = !negated;
            }
        }
        catch
        {
            return false;
        }

        return matched;
    }

    private static bool IsActiveGitIgnoreLine(string rawLine)
    {
        var line = rawLine.Trim();
        return !string.IsNullOrWhiteSpace(line) && !line.StartsWith('#');
    }

    private static bool TryBuildGitIgnoreRegex(string pattern, out Regex regex)
    {
        regex = null!;
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return false;
        }

        var normalized = pattern.Replace('\\', '/');
        var anchored = normalized.StartsWith('/');
        if (anchored)
        {
            normalized = normalized[1..];
        }

        var suffixWildcard = normalized.EndsWith('/');
        if (suffixWildcard)
        {
            normalized += "**";
        }

        var escaped = Regex.Escape(normalized)
            .Replace(@"\*\*", ".*")
            .Replace(@"\*", @"[^/]*")
            .Replace(@"\?", @"[^/]");

        var expression = anchored
            ? $"^{escaped}$"
            : $"(^|.*/){escaped}$";
        regex = new Regex(expression, RegexOptions.Compiled | RegexOptions.CultureInvariant);
        return true;
    }

    private static string NormalizeRelativePath(string projectPath, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var candidate = path.Replace('\\', '/').Trim();
        if (Path.IsPathRooted(candidate))
        {
            try
            {
                var relative = Path.GetRelativePath(projectPath, candidate).Replace('\\', '/');
                if (relative.StartsWith("..", StringComparison.Ordinal))
                {
                    return candidate.TrimStart('/');
                }

                return relative.TrimStart('/');
            }
            catch
            {
                return candidate.TrimStart('/');
            }
        }

        return candidate.TrimStart('/');
    }

    private static string BuildDetectionSignature(string projectPath, bool hasGit, bool hasActiveGitIgnore, bool hasUvcs, string mode)
    {
        var versionControlSettings = Path.Combine(projectPath, "ProjectSettings", "VersionControlSettings.asset");
        var gitIgnorePath = Path.Combine(projectPath, ".gitignore");
        var vcsStamp = File.Exists(versionControlSettings)
            ? File.GetLastWriteTimeUtc(versionControlSettings).Ticks.ToString()
            : "none";
        var ignoreStamp = File.Exists(gitIgnorePath)
            ? File.GetLastWriteTimeUtc(gitIgnorePath).Ticks.ToString()
            : "none";
        return $"{mode}|git={hasGit}|gitignore={hasActiveGitIgnore}|uvcs={hasUvcs}|vcsStamp={vcsStamp}|ignoreStamp={ignoreStamp}";
    }

    private static ProjectVcsConfig? TryReadConfig(string projectPath)
    {
        var path = ResolveConfigPath(projectPath);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ProjectVcsConfig>(File.ReadAllText(path), JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static OperationResult WriteConfig(string projectPath, ProjectVcsDetection detection)
    {
        try
        {
            var path = ResolveConfigPath(projectPath);
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var payload = new ProjectVcsConfig(
                1,
                detection.Mode,
                SetupComplete: true,
                detection.DetectionSignature,
                DateTime.UtcNow.ToString("O"));
            File.WriteAllText(path, JsonSerializer.Serialize(payload, JsonOptions) + Environment.NewLine);
            return OperationResult.Success();
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"failed to persist VCS setup config ({ex.Message})");
        }
    }

    private static string ResolveConfigPath(string projectPath)
    {
        return Path.Combine(projectPath, ".unifocl", "vcs-config.json");
    }

    private sealed record ProjectVcsDetection(
        string Mode,
        string DetectionSignature);

    private sealed record ProjectVcsConfig(
        int SchemaVersion,
        string DetectedMode,
        bool SetupComplete,
        string DetectionSignature,
        string UpdatedAtUtc);
}

internal readonly record struct VcsMutationGuardResult(bool Allowed, string Message)
{
    public static VcsMutationGuardResult Allow(string message = "")
    {
        return new VcsMutationGuardResult(true, message);
    }

    public static VcsMutationGuardResult Block(string message)
    {
        return new VcsMutationGuardResult(false, message);
    }
}
