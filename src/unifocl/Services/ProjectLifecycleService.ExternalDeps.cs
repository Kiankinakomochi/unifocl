using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Spectre.Console;

internal sealed partial class ProjectLifecycleService
{
    private static async Task<OperationResult> EnsureRequiredMcpHostDependenciesAsync(Action<string> log)
    {
        var requirements = await ResolveRequiredMcpHostDependenciesAsync();
        if (requirements.Count == 0)
        {
            return OperationResult.Success();
        }

        var uvRequirement = requirements.FirstOrDefault(r => r.Id.Equals("uv", StringComparison.OrdinalIgnoreCase));
        var pythonRequirement = requirements.FirstOrDefault(r => r.Id.Equals("python3", StringComparison.OrdinalIgnoreCase));
        var requiredPackageManager = ResolveRequiredPackageManagerForHost();
        if (requiredPackageManager is not null && uvRequirement is not null && pythonRequirement is not null)
        {
            var requiredManager = requiredPackageManager.Value;
            var hasRequiredPackageManager = await IsCommandAvailableAsync(requiredManager.Command);
            if (!hasRequiredPackageManager)
            {
                var hasUv = (await ProbeExternalDependencyAsync(uvRequirement)).Satisfied;
                var hasPython = (await ProbeExternalDependencyAsync(pythonRequirement)).Satisfied;
                if (!hasUv && !hasPython)
                {
                    log($"[yellow]init[/]: install [white]{requiredManager.Label}[/] or [white]python/uv[/] manually");
                    return OperationResult.Fail($"install {requiredManager.Label} or python/uv manually");
                }
            }
        }

        var autoInstall = IsAutoInstallExternalDependenciesEnabled();
        var installedAny = false;
        foreach (var requirement in requirements)
        {
            var probe = await ProbeExternalDependencyAsync(requirement);
            if (probe.Satisfied)
            {
                continue;
            }

            LogMissingExternalDependency(probe, log);

            var installOptions = await ResolveDependencyInstallOptionsAsync(requirement, probe);
            if (installOptions.Count == 0)
            {
                return OperationResult.Fail(
                    $"no supported package manager was detected for installing {requirement.DisplayName}");
            }

            LogInstallOptions(requirement, installOptions, log);

            if (Console.IsInputRedirected && !autoInstall)
            {
                log($"[yellow]init[/]: cannot prompt for {Markup.Escape(requirement.DisplayName)} in redirected mode; set [white]{ExternalDependencyAutoInstallEnv}=1[/] to auto-install");
                continue;
            }

            DependencyInstallOption selectedOption;
            if (autoInstall || Console.IsInputRedirected)
            {
                selectedOption = installOptions[0];
                log($"[grey]init[/]: auto-selected installer for [white]{Markup.Escape(requirement.DisplayName)}[/] -> [white]{Markup.Escape(selectedOption.Label)}[/]");
            }
            else
            {
                selectedOption = CliTheme.PromptWithDividers(() =>
                    AnsiConsole.Prompt(
                        new SelectionPrompt<DependencyInstallOption>()
                            .Title($"Choose installation method for [yellow]{Markup.Escape(requirement.DisplayName)}[/]")
                            .PageSize(ResolvePromptPageSize(installOptions.Count, 10))
                            .HighlightStyle(CliTheme.SelectionHighlightStyle)
                            .UseConverter(option => $"{option.Label} ({option.Source})")
                            .AddChoices(installOptions)));
            }

            var shouldInstall = autoInstall
                                || CliTheme.ConfirmWithDividers(
                                    $"Install [white]{Markup.Escape(requirement.DisplayName)}[/] using [white]{Markup.Escape(selectedOption.Label)}[/]?",
                                    defaultValue: true);
            if (!shouldInstall)
            {
                log($"[yellow]init[/]: skipped installation for [white]{Markup.Escape(requirement.DisplayName)}[/]");
                continue;
            }

            var installResult = await TryInstallExternalDependencyAsync(requirement, selectedOption, log);
            if (!installResult.Ok)
            {
                return installResult;
            }

            installedAny = true;
            var afterInstall = await ProbeExternalDependencyAsync(requirement);
            if (!afterInstall.Satisfied)
            {
                return OperationResult.Fail(
                    $"dependency still missing after install attempt: {requirement.DisplayName}");
            }
        }

        if (installedAny)
        {
            log("[green]init[/]: installed required MCP runtime dependencies");
            return OperationResult.Success();
        }

        log("[grey]init[/]: MCP runtime dependencies already satisfied");
        return OperationResult.Success();
    }

    private static (string Command, string Label)? ResolveRequiredPackageManagerForHost()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return ("winget", "winget");
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return ("brew", "homebrew");
        }

        return null;
    }

    private static Task<List<ExternalDependencyRequirement>> ResolveRequiredMcpHostDependenciesAsync()
    {
        // Manual baseline list. Keep this updated as MCP host requirements change.
        return Task.FromResult(GetBaselineMcpHostDependencies());
    }

    private static List<ExternalDependencyRequirement> GetBaselineMcpHostDependencies()
    {
        // Manual baseline list. Keep this section updated as MCP host requirements change.
        return
        [
            new ExternalDependencyRequirement(
                Id: "uv",
                DisplayName: "uv",
                ProbeCommand: "uv",
                ProbeArgs: "--version",
                MinimumVersion: null),
            new ExternalDependencyRequirement(
                Id: "python3",
                DisplayName: "Python 3",
                ProbeCommand: "python3",
                ProbeArgs: "--version",
                MinimumVersion: "3.10")
        ];
    }

    private static async Task<ExternalDependencyProbeResult> ProbeExternalDependencyAsync(
        ExternalDependencyRequirement requirement)
    {
        if (requirement.Id.Equals("python3", StringComparison.OrdinalIgnoreCase))
        {
            var uvResolved = await TryProbePythonViaUvAsync(requirement);
            if (uvResolved is not null)
            {
                return uvResolved;
            }
        }

        var commandCandidates = ResolveProbeCommandCandidates(requirement);
        ProcessResult? lastFailure = null;
        foreach (var commandCandidate in commandCandidates)
        {
            var probe = await RunProcessAsync(
                commandCandidate.Command,
                commandCandidate.Args,
                Directory.GetCurrentDirectory(),
                ExternalDependencyProbeTimeout);
            if (probe.ExitCode != 0)
            {
                lastFailure = probe;
                continue;
            }

            if (!string.Equals(commandCandidate.Command, requirement.ProbeCommand, StringComparison.Ordinal))
            {
                TryAddToolDirectoryToProcessPath(commandCandidate.Command);
            }

            var versionText = ExtractVersionToken(probe.Stdout, probe.Stderr);
            var minimum = NormalizeMinimumVersion(requirement.MinimumVersion);
            if (minimum is null)
            {
                return new ExternalDependencyProbeResult(
                    requirement,
                    Satisfied: true,
                    InstalledVersion: versionText,
                    Error: null);
            }

            if (!TryParseVersion(versionText, out var installedVersion)
                || !TryParseVersion(minimum, out var requiredVersion))
            {
                return new ExternalDependencyProbeResult(
                    requirement,
                    Satisfied: false,
                    InstalledVersion: versionText,
                    Error: $"could not validate version (required >= {minimum})");
            }

            if (installedVersion < requiredVersion)
            {
                return new ExternalDependencyProbeResult(
                    requirement,
                    Satisfied: false,
                    InstalledVersion: versionText,
                    Error: $"version {installedVersion} is below required {minimum}");
            }

            return new ExternalDependencyProbeResult(
                requirement,
                Satisfied: true,
                InstalledVersion: versionText,
                Error: null);
        }

        var probeError = BuildUserFriendlyProbeError(lastFailure);
        return new ExternalDependencyProbeResult(
            requirement,
            Satisfied: false,
            InstalledVersion: null,
            Error: probeError);
    }

    private static async Task<ExternalDependencyProbeResult?> TryProbePythonViaUvAsync(
        ExternalDependencyRequirement requirement)
    {
        ProcessResult? uvListResult = null;
        foreach (var args in new[] { "python list --only-installed", "python list" })
        {
            var candidate = await RunProcessAsync(
                "uv",
                args,
                Directory.GetCurrentDirectory(),
                ExternalDependencyProbeTimeout);
            if (candidate.ExitCode == 0)
            {
                uvListResult = candidate;
                break;
            }
        }

        if (uvListResult is null)
        {
            return null;
        }

        var minimum = NormalizeMinimumVersion(requirement.MinimumVersion);
        var uvOutput = $"{uvListResult.Stdout}\n{uvListResult.Stderr}";
        if (TryExtractPythonVersionFromUvOutput(uvOutput, minimum, out var uvManagedVersion))
        {
            return new ExternalDependencyProbeResult(
                requirement,
                Satisfied: true,
                InstalledVersion: uvManagedVersion,
                Error: null);
        }

        var discoveredPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(
                     uvOutput,
                     @"(?<unix>/[^ \t\r\n]+python[0-9.]*)|(?<win>[A-Za-z]:\\[^ \t\r\n]+python[0-9.]*\.exe)"))
        {
            var path = match.Groups["unix"].Success
                ? match.Groups["unix"].Value
                : match.Groups["win"].Value;
            if (!string.IsNullOrWhiteSpace(path))
            {
                discoveredPaths.Add(path);
            }
        }

        foreach (var discoveredPath in discoveredPaths)
        {
            if (!File.Exists(discoveredPath))
            {
                continue;
            }

            var probe = await RunProcessAsync(
                discoveredPath,
                "--version",
                Directory.GetCurrentDirectory(),
                ExternalDependencyProbeTimeout);
            if (probe.ExitCode != 0)
            {
                continue;
            }

            TryAddToolDirectoryToProcessPath(discoveredPath);
            var versionText = ExtractVersionToken(probe.Stdout, probe.Stderr);
            if (minimum is null)
            {
                return new ExternalDependencyProbeResult(
                    requirement,
                    Satisfied: true,
                    InstalledVersion: versionText,
                    Error: null);
            }

            if (!TryParseVersion(versionText, out var installedVersion)
                || !TryParseVersion(minimum, out var requiredVersion))
            {
                continue;
            }

            if (installedVersion < requiredVersion)
            {
                continue;
            }

            return new ExternalDependencyProbeResult(
                requirement,
                Satisfied: true,
                InstalledVersion: versionText,
                Error: null);
        }

        return null;
    }

    private static bool TryExtractPythonVersionFromUvOutput(
        string uvOutput,
        string? minimumVersion,
        out string versionText)
    {
        versionText = string.Empty;
        var minimum = NormalizeMinimumVersion(minimumVersion);
        Version? requiredVersion = null;
        if (minimum is not null)
        {
            if (!TryParseVersion(minimum, out var parsedRequiredVersion))
            {
                return false;
            }

            requiredVersion = parsedRequiredVersion;
        }

        Version? bestVersion = null;
        foreach (Match match in Regex.Matches(
                     uvOutput ?? string.Empty,
                     @"(?i)\bpython(?:\s+|@|-)?(?<version>\d+\.\d+(?:\.\d+)?)\b"))
        {
            var candidate = match.Groups["version"].Value;
            if (!TryParseVersion(candidate, out var parsed))
            {
                continue;
            }

            if (requiredVersion is not null && parsed < requiredVersion)
            {
                continue;
            }

            if (bestVersion is null || parsed > bestVersion)
            {
                bestVersion = parsed;
            }
        }

        if (bestVersion is null)
        {
            return false;
        }

        versionText = bestVersion.ToString();
        return true;
    }

    private static async Task<OperationResult> TryInstallExternalDependencyAsync(
        ExternalDependencyRequirement requirement,
        DependencyInstallOption option,
        Action<string> log)
    {
        log($"[grey]init[/]: running install command for [white]{Markup.Escape(requirement.DisplayName)}[/]: [white]{Markup.Escape(option.Command)}[/]");
        var shellFile = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "cmd.exe" : "/bin/bash";
        var shellArgs = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? $"/c {option.Command}"
            : $"-lc \"{option.Command.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

        var result = await RunProcessAsync(
            shellFile,
            shellArgs,
            Directory.GetCurrentDirectory(),
            ExternalDependencyInstallTimeout);
        if (result.ExitCode == 0)
        {
            return OperationResult.Success();
        }

        return OperationResult.Fail(
            $"failed to install {requirement.DisplayName} via {option.Label}: {SummarizeProcessError(result)}");
    }

    private static List<(string Command, string Args)> ResolveProbeCommandCandidates(ExternalDependencyRequirement requirement)
    {
        var candidates = new List<(string Command, string Args)>();
        void AddCandidate(string? command, string? args = null)
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                return;
            }

            var resolvedArgs = string.IsNullOrWhiteSpace(args) ? requirement.ProbeArgs : args;
            if (candidates.Any(existing =>
                    existing.Command.Equals(command, StringComparison.Ordinal)
                    && existing.Args.Equals(resolvedArgs, StringComparison.Ordinal)))
            {
                return;
            }

            candidates.Add((command, resolvedArgs));
        }

        AddCandidate(requirement.ProbeCommand, requirement.ProbeArgs);

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
        {
            if (requirement.Id.Equals("uv", StringComparison.OrdinalIgnoreCase))
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    AddCandidate(Path.Combine(home, ".local", "bin", "uv.exe"));
                    AddCandidate(Path.Combine(home, ".cargo", "bin", "uv.exe"));
                }
                else
                {
                    AddCandidate(Path.Combine(home, ".local", "bin", "uv"));
                    AddCandidate(Path.Combine(home, ".cargo", "bin", "uv"));
                }
            }
            else if (requirement.Id.Equals("python3", StringComparison.OrdinalIgnoreCase))
            {
                AddCandidate("python", "--version");
                AddCandidate("python3.12", "--version");
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    AddCandidate("py", "-3 --version");
                    AddCandidate("py", "--version");
                }
                else
                {
                    AddCandidate("/opt/homebrew/bin/python3.12", "--version");
                    AddCandidate("/usr/bin/python3", "--version");
                    AddCandidate(Path.Combine(home, ".pyenv", "shims", "python3"), "--version");
                    AddCandidate(Path.Combine(home, ".pyenv", "shims", "python"), "--version");
                }

                foreach (var candidatePath in DiscoverDynamicPythonCandidates())
                {
                    AddCandidate(candidatePath, "--version");
                }
            }
        }

        return candidates;
    }

    private static IEnumerable<string> DiscoverDynamicPythonCandidates()
    {
        var discovered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        var venv = Environment.GetEnvironmentVariable("VIRTUAL_ENV")
                   ?? Environment.GetEnvironmentVariable("CONDA_PREFIX");
        if (!string.IsNullOrWhiteSpace(venv))
        {
            var venvBin = isWindows ? Path.Combine(venv, "Scripts") : Path.Combine(venv, "bin");
            if (isWindows)
            {
                TryAddDiscovered(Path.Combine(venvBin, "python3.exe"), discovered);
                TryAddDiscovered(Path.Combine(venvBin, "python.exe"), discovered);
            }
            else
            {
                TryAddDiscovered(Path.Combine(venvBin, "python3"), discovered);
                TryAddDiscovered(Path.Combine(venvBin, "python"), discovered);
            }
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathEnv))
        {
            var pathDirs = pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var candidateNames = isWindows
                ? new[] { "python3.exe", "python.exe", "python3.12.exe", "python3.11.exe", "python3.10.exe" }
                : new[] { "python3", "python", "python3.12", "python3.11", "python3.10" };

            foreach (var pathDir in pathDirs)
            {
                if (!Directory.Exists(pathDir))
                {
                    continue;
                }

                foreach (var candidateName in candidateNames)
                {
                    TryAddDiscovered(Path.Combine(pathDir, candidateName), discovered);
                }

                IEnumerable<string> dynamicPythonFiles;
                try
                {
                    dynamicPythonFiles = Directory.EnumerateFiles(pathDir, "python*");
                }
                catch
                {
                    continue;
                }

                foreach (var dynamicFile in dynamicPythonFiles)
                {
                    TryAddDiscovered(dynamicFile, discovered);
                }
            }
        }

        if (!isWindows)
        {
            foreach (var homebrewPython in DiscoverPythonExecutablesUnderHomebrew())
            {
                discovered.Add(homebrewPython);
            }
        }

        return discovered.Where(File.Exists);
    }

    private static IEnumerable<string> DiscoverPythonExecutablesUnderHomebrew()
    {
        const string homebrewRoot = "/opt/homebrew";
        if (!Directory.Exists(homebrewRoot))
        {
            yield break;
        }

        var discovered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push(homebrewRoot);
        var visitedDirectoryCount = 0;
        const int maxDirectories = 4096;

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            visitedDirectoryCount++;
            if (visitedDirectoryCount > maxDirectories)
            {
                yield break;
            }

            IEnumerable<string> childDirectories;
            try
            {
                childDirectories = Directory.EnumerateDirectories(current);
            }
            catch
            {
                continue;
            }

            foreach (var child in childDirectories)
            {
                pending.Push(child);
            }

            if (!current.EndsWith("/bin", StringComparison.Ordinal))
            {
                continue;
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(current, "python*");
            }
            catch
            {
                continue;
            }

            foreach (var file in files)
            {
                var name = Path.GetFileName(file);
                if (!name.StartsWith("python", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!discovered.Add(file))
                {
                    continue;
                }

                yield return file;
            }
        }
    }

    private static void TryAddDiscovered(string candidatePath, HashSet<string> discovered)
    {
        try
        {
            if (File.Exists(candidatePath))
            {
                discovered.Add(candidatePath);
            }
        }
        catch
        {
        }
    }

    private static void TryAddToolDirectoryToProcessPath(string commandPath)
    {
        try
        {
            var directory = Path.GetDirectoryName(commandPath);
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return;
            }

            var currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            var segments = currentPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Any(segment => segment.Equals(directory, StringComparison.Ordinal)))
            {
                return;
            }

            var updatedPath = string.IsNullOrWhiteSpace(currentPath)
                ? directory
                : $"{currentPath}{Path.PathSeparator}{directory}";
            Environment.SetEnvironmentVariable("PATH", updatedPath);
        }
        catch
        {
        }
    }

    private static string BuildUserFriendlyProbeError(ProcessResult? failure)
    {
        if (failure is null)
        {
            return "not installed or not available on PATH";
        }

        var summary = SummarizeProcessError(failure);
        if (string.IsNullOrWhiteSpace(summary))
        {
            return "not installed or not available on PATH";
        }

        var normalized = summary.ToLowerInvariant();
        if (normalized.Contains("no such file")
            || normalized.Contains("cannot find the file")
            || normalized.Contains("could not start process")
            || normalized.Contains("not found"))
        {
            return "not installed or not available on PATH";
        }

        return summary;
    }

    private static async Task<List<DependencyInstallOption>> ResolveDependencyInstallOptionsAsync(
        ExternalDependencyRequirement requirement,
        ExternalDependencyProbeResult probeResult)
    {
        var options = new List<DependencyInstallOption>();
        void AddOption(string label, string command, string source)
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                return;
            }

            if (options.Any(existing => existing.Command.Equals(command, StringComparison.Ordinal)))
            {
                return;
            }

            options.Add(new DependencyInstallOption(label, command, source));
        }

        var hasBrew = await IsCommandAvailableAsync("brew");
        var hasWinget = await IsCommandAvailableAsync("winget");
        var hasUv = await IsCommandAvailableAsync("uv");
        var hasCurl = await IsCommandAvailableAsync("curl");
        var brewCmd = ResolveToolCommand("brew", "/opt/homebrew/bin/brew", "/usr/local/bin/brew");

        if (requirement.Id.Equals("python3", StringComparison.OrdinalIgnoreCase))
        {
            if (hasBrew) AddOption($"Homebrew ({brewCmd} install python@3.12)", $"{brewCmd} install python@3.12", "package-manager");
            if (hasUv) AddOption("uv (uv python install 3.12)", "uv python install 3.12", "uv");
        }
        else if (requirement.Id.Equals("uv", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(probeResult.InstalledVersion))
            {
                AddOption("uv self update", "uv self update", "uv");
            }

            if (hasBrew) AddOption($"Homebrew ({brewCmd} install uv)", $"{brewCmd} install uv", "package-manager");
            if (hasWinget) AddOption("WinGet (winget install --id=astral-sh.uv -e)", "winget install --id=astral-sh.uv -e", "package-manager");
            if (hasCurl) AddOption("Standalone installer (curl -LsSf https://astral.sh/uv/install.sh | sh)", "curl -LsSf https://astral.sh/uv/install.sh | sh", "installer");
        }

        return options;
    }

    private static async Task<bool> IsCommandAvailableAsync(string command)
    {
        var result = await RunProcessAsync(command, "--version", Directory.GetCurrentDirectory(), ExternalDependencyProbeTimeout);
        return result.ExitCode == 0;
    }

    private static string ResolveToolCommand(string preferred, params string[] fallbackAbsolutePaths)
    {
        foreach (var fallback in fallbackAbsolutePaths)
        {
            if (string.IsNullOrWhiteSpace(fallback))
            {
                continue;
            }

            try
            {
                if (File.Exists(fallback))
                {
                    return fallback;
                }
            }
            catch
            {
            }
        }

        return preferred;
    }

    private static void LogInstallOptions(
        ExternalDependencyRequirement requirement,
        IReadOnlyList<DependencyInstallOption> options,
        Action<string> log)
    {
        log($"[grey]init[/]: available installers for [white]{Markup.Escape(requirement.DisplayName)}[/]");
        for (var i = 0; i < options.Count; i++)
        {
            var option = options[i];
            log($"[grey]init[/]: {i + 1}. [white]{Markup.Escape(option.Label)}[/] ({Markup.Escape(option.Source)}) -> [white]{Markup.Escape(option.Command)}[/]");
        }
    }

    private static void LogMissingExternalDependency(ExternalDependencyProbeResult result, Action<string> log)
    {
        var versionLabel = string.IsNullOrWhiteSpace(result.InstalledVersion)
            ? "not detected"
            : result.InstalledVersion;
        var minimumLabel = string.IsNullOrWhiteSpace(result.Requirement.MinimumVersion)
            ? "installed"
            : $">= {result.Requirement.MinimumVersion}";
        var detail = string.IsNullOrWhiteSpace(result.Error)
            ? string.Empty
            : $" ({result.Error})";

        log(
            $"[yellow]init[/]: missing [white]{Markup.Escape(result.Requirement.DisplayName)}[/] "
            + $"(detected: [white]{Markup.Escape(versionLabel)}[/], required: [white]{Markup.Escape(minimumLabel)}[/])"
            + $"{Markup.Escape(detail)}");
    }

    private static bool IsAutoInstallExternalDependenciesEnabled()
    {
        var raw = Environment.GetEnvironmentVariable(ExternalDependencyAutoInstallEnv);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        return raw.Equals("1", StringComparison.OrdinalIgnoreCase)
               || raw.Equals("true", StringComparison.OrdinalIgnoreCase)
               || raw.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeMinimumVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        var cleaned = version.Trim().TrimStart('v', 'V');
        while (cleaned.EndsWith("+", StringComparison.Ordinal))
        {
            cleaned = cleaned[..^1].TrimEnd();
        }

        return cleaned;
    }

    private static string? ExtractVersionToken(string stdout, string stderr)
    {
        var source = string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;
        if (string.IsNullOrWhiteSpace(source))
        {
            return null;
        }

        var match = Regex.Match(source, @"\d+(?:\.\d+){0,3}");
        return match.Success ? match.Value : null;
    }

    private static bool TryParseVersion(string? version, out Version parsed)
    {
        parsed = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(version))
        {
            return false;
        }

        var cleaned = version.Trim().TrimStart('v', 'V');
        var parts = cleaned.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        if (parts.Length == 1)
        {
            cleaned = $"{parts[0]}.0";
        }

        if (parts.Length > 4)
        {
            cleaned = string.Join(".", parts.Take(4));
        }

        if (!Version.TryParse(cleaned, out var parsedVersion) || parsedVersion is null)
        {
            return false;
        }

        parsed = parsedVersion;
        return true;
    }

    private async Task<bool> HandleAgentInstallAsync(
        string input,
        CommandSpec matched,
        Action<string> log)
    {
        var args = ParseCommandArgs(input, matched.Trigger);
        if (!TryParseAgentInstallArgs(
                args,
                out var target,
                out var workspacePathRaw,
                out var serverName,
                out var configRootRaw,
                out var dryRun,
                out var error))
        {
            log($"[red]error[/]: {Markup.Escape(error)}");
            return true;
        }

        if (target.Equals("codex", StringComparison.OrdinalIgnoreCase))
        {
            return await HandleAgentInstallCodexAsync(workspacePathRaw, serverName, configRootRaw, dryRun, log);
        }

        return await HandleAgentInstallClaudeAsync(workspacePathRaw, serverName, configRootRaw, dryRun, log);
    }

    private async Task<bool> HandleAgentSetupFromInputAsync(
        string input,
        CommandSpec matched,
        Action<string> log)
    {
        var args = ParseCommandArgs(input, matched.Trigger);
        string? projectPath = null;
        var dryRun = false;

        foreach (var arg in args)
        {
            if (arg.Equals("--dry-run", StringComparison.OrdinalIgnoreCase))
            {
                dryRun = true;
                continue;
            }

            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                log($"[red]error[/]: unrecognized option {Markup.Escape(arg)}; usage: /agent setup [path-to-unity-project] [--dry-run]");
                return true;
            }

            if (projectPath is not null)
            {
                log("[red]error[/]: too many arguments; usage: /agent setup [path-to-unity-project] [--dry-run]");
                return true;
            }

            projectPath = arg;
        }

        await HandleAgentSetupAsync(projectPath, dryRun, AgentSetupTarget.Both, log);
        return true;
    }

    private static bool TryParseAgentInstallArgs(
        IReadOnlyList<string> args,
        out string target,
        out string? workspacePath,
        out string serverName,
        out string? configRootPath,
        out bool dryRun,
        out string error)
    {
        target = string.Empty;
        workspacePath = null;
        serverName = "unifocl";
        configRootPath = null;
        dryRun = false;
        error = string.Empty;

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg.Equals("--workspace", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Count)
                {
                    error = "missing value for --workspace; usage /agent install <codex|claude> [--workspace <path>] [--server-name <name>] [--config-root <path>] [--dry-run]";
                    return false;
                }

                workspacePath = args[++i];
                continue;
            }

            if (arg.Equals("--server-name", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Count)
                {
                    error = "missing value for --server-name; usage /agent install <codex|claude> [--workspace <path>] [--server-name <name>] [--config-root <path>] [--dry-run]";
                    return false;
                }

                serverName = args[++i].Trim();
                continue;
            }

            if (arg.Equals("--config-root", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Count)
                {
                    error = "missing value for --config-root; usage /agent install <codex|claude> [--workspace <path>] [--server-name <name>] [--config-root <path>] [--dry-run]";
                    return false;
                }

                configRootPath = args[++i];
                continue;
            }

            if (arg.Equals("--dry-run", StringComparison.OrdinalIgnoreCase))
            {
                dryRun = true;
                continue;
            }

            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                error = $"unrecognized option {arg}; usage /agent install <codex|claude> [--workspace <path>] [--server-name <name>] [--config-root <path>] [--dry-run]";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(target))
            {
                error = "too many positional arguments; usage /agent install <codex|claude> [--workspace <path>] [--server-name <name>] [--config-root <path>] [--dry-run]";
                return false;
            }

            target = arg.Trim();
        }

        if (string.IsNullOrWhiteSpace(target))
        {
            error = "usage /agent install <codex|claude> [--workspace <path>] [--server-name <name>] [--config-root <path>] [--dry-run]";
            return false;
        }

        if (!target.Equals("codex", StringComparison.OrdinalIgnoreCase)
            && !target.Equals("claude", StringComparison.OrdinalIgnoreCase))
        {
            error = $"unsupported target '{target}'; use codex or claude";
            return false;
        }

        if (string.IsNullOrWhiteSpace(serverName))
        {
            error = "--server-name cannot be empty";
            return false;
        }

        return true;
    }

    private async Task<bool> HandleAgentInstallCodexAsync(
        string? workspacePathRaw,
        string serverName,
        string? configRootRaw,
        bool dryRun,
        Action<string> log)
    {
        var currentDirectory = Directory.GetCurrentDirectory();
        var workspacePath = ResolveAbsolutePath(
            string.IsNullOrWhiteSpace(workspacePathRaw) ? currentDirectory : workspacePathRaw,
            currentDirectory);
        if (!Directory.Exists(workspacePath))
        {
            log($"[red]agent[/]: workspace path does not exist: [white]{Markup.Escape(workspacePath)}[/]");
            return false;
        }

        var configRoot = string.IsNullOrWhiteSpace(configRootRaw)
            ? Path.Combine(workspacePath, ".local", "unifocl-config")
            : ResolveAbsolutePath(configRootRaw, currentDirectory);

        var removeArgs = BuildProcessArgumentString(["mcp", "remove", serverName]);
        var addArgs = BuildProcessArgumentString([
            "mcp",
            "add",
            serverName,
            "--env",
            $"UNIFOCL_CONFIG_ROOT={configRoot}",
            "--",
            "unifocl",
            "--mcp-server"
        ]);

        var agentsMdPath = Path.Combine(workspacePath, "AGENTS.md");

        if (dryRun)
        {
            log("[grey]agent[/]: dry-run (no changes applied)");
            log($"[grey]agent[/]: workspace [white]{Markup.Escape(workspacePath)}[/]");
            log($"[grey]agent[/]: config-root [white]{Markup.Escape(configRoot)}[/]");
            log($"[grey]agent[/]: would run [white]codex {Markup.Escape(removeArgs)}[/]");
            log($"[grey]agent[/]: would run [white]codex {Markup.Escape(addArgs)}[/]");
            log($"[grey]agent[/]: would merge [white]{Markup.Escape(agentsMdPath)}[/] — unifocl fenced section");
            return true;
        }

        if (!await IsCommandAvailableAsync("codex"))
        {
            log("[red]agent[/]: codex is not installed or not available on PATH");
            return false;
        }

        Directory.CreateDirectory(configRoot);
        _ = await RunProcessAsync("codex", removeArgs, workspacePath, ExternalDependencyProbeTimeout);
        var addResult = await RunProcessAsync("codex", addArgs, workspacePath, ExternalDependencyProbeTimeout);
        if (addResult.ExitCode != 0)
        {
            var reason = SummarizeProcessError(addResult);
            log($"[red]agent[/]: failed to install codex integration ({Markup.Escape(reason)})");
            return false;
        }

        try
        {
            MergeMarkdownFile(agentsMdPath, BuildAgentsMdSection(serverName));
            log("[grey]agent[/]: merged [white]AGENTS.md[/] — unifocl fenced section");
        }
        catch (Exception ex)
        {
            log($"[red]agent[/]: failed to merge AGENTS.md: {Markup.Escape(ex.Message)}");
        }

        log("[green]agent[/]: codex integration installed");
        log($"[grey]agent[/]: server [white]{Markup.Escape(serverName)}[/] -> [white]unifocl --mcp-server[/] (registered globally in ~/.codex/config.toml)");
        log($"[grey]agent[/]: config-root [white]{Markup.Escape(configRoot)}[/]");
        log("[grey]agent[/]: commit [white]AGENTS.md[/] to share agent instructions with your team");
        log("[grey]agent[/]: restart Codex session to load the MCP tools");
        return true;
    }

    private async Task<bool> HandleAgentInstallClaudeAsync(
        string? workspacePathRaw,
        string serverName,
        string? configRootRaw,
        bool dryRun,
        Action<string> log)
    {
        var currentDirectory = Directory.GetCurrentDirectory();
        var workspacePath = ResolveAbsolutePath(
            string.IsNullOrWhiteSpace(workspacePathRaw) ? currentDirectory : workspacePathRaw,
            currentDirectory);
        if (!Directory.Exists(workspacePath))
        {
            log($"[red]agent[/]: workspace path does not exist: [white]{Markup.Escape(workspacePath)}[/]");
            return true;
        }

        var configRoot = string.IsNullOrWhiteSpace(configRootRaw)
            ? Path.Combine(workspacePath, ".local")
            : ResolveAbsolutePath(configRootRaw, currentDirectory);

        var claudeDir = Path.Combine(workspacePath, ".claude");
        var mcpJsonPath = Path.Combine(workspacePath, ".mcp.json");
        var settingsLocalJsonPath = Path.Combine(claudeDir, "settings.local.json");
        var claudeMdPath = Path.Combine(workspacePath, "CLAUDE.md");
        var gitignorePath = Path.Combine(workspacePath, ".gitignore");

        if (dryRun)
        {
            log("[grey]agent[/]: dry-run (no changes applied)");
            log($"[grey]agent[/]: workspace [white]{Markup.Escape(workspacePath)}[/]");
            log($"[grey]agent[/]: config-root [white]{Markup.Escape(configRoot)}[/]");
            log($"[grey]agent[/]: would merge [white]{Markup.Escape(mcpJsonPath)}[/] — mcpServers.{Markup.Escape(serverName)}");
            log($"[grey]agent[/]: would merge [white]{Markup.Escape(claudeMdPath)}[/] — unifocl fenced section");
            log($"[grey]agent[/]: would merge [white]{Markup.Escape(settingsLocalJsonPath)}[/] — permissions.allow");
            log($"[grey]agent[/]: would check [white]{Markup.Escape(gitignorePath)}[/] — .claude/settings.local.json entry");
            return true;
        }

        var errorCount = 0;

        // 1. Merge .mcp.json — project-scoped MCP registration (commit this)
        try
        {
            MergeMcpJson(mcpJsonPath, serverName, configRoot);
            log($"[grey]agent[/]: merged [white].mcp.json[/] — mcpServers.{Markup.Escape(serverName)}");
        }
        catch (Exception ex)
        {
            errorCount++;
            log($"[red]agent[/]: failed to merge .mcp.json: {Markup.Escape(ex.Message)}");
        }

        // 2. Merge CLAUDE.md — project instructions for Claude Code (commit this)
        try
        {
            MergeClaudeMd(claudeMdPath, serverName);
            log("[grey]agent[/]: merged [white]CLAUDE.md[/] — unifocl fenced section");
        }
        catch (Exception ex)
        {
            errorCount++;
            log($"[red]agent[/]: failed to merge CLAUDE.md: {Markup.Escape(ex.Message)}");
        }

        // 3. Merge .claude/settings.local.json — machine-local permission allows (do not commit)
        try
        {
            Directory.CreateDirectory(claudeDir);
            MergeClaudeSettingsLocal(settingsLocalJsonPath, serverName);
            log("[grey]agent[/]: merged [white]settings.local.json[/] — permissions.allow");
        }
        catch (Exception ex)
        {
            errorCount++;
            log($"[red]agent[/]: failed to merge settings.local.json: {Markup.Escape(ex.Message)}");
        }

        // 4. Ensure settings.local.json is gitignored
        try
        {
            MergeGitignore(gitignorePath);
            log("[grey]agent[/]: checked [white].gitignore[/] — .claude/settings.local.json");
        }
        catch (Exception ex)
        {
            errorCount++;
            log($"[red]agent[/]: failed to update .gitignore: {Markup.Escape(ex.Message)}");
        }

        if (errorCount == 0)
        {
            log("[green]agent[/]: claude integration installed");
            log("[grey]agent[/]: commit [white].mcp.json[/] and [white]CLAUDE.md[/] to share with all users");
            log("[grey]agent[/]: [white]settings.local.json[/] is machine-local — do not commit");
            log("[grey]agent[/]: restart Claude Code to load the MCP tools");
        }
        else
        {
            log($"[yellow]agent[/]: install completed with {errorCount} error(s) — review output above");
        }

        return true;
    }

    // ── agent setup ──────────────────────────────────────────────────────────

    internal static string? FindAgentSetupRoot(string startDirectory)
    {
        var dir = new DirectoryInfo(startDirectory);
        while (dir is not null)
        {
            var agentsMdPath = Path.Combine(dir.FullName, "AGENTS.md");
            if (File.Exists(agentsMdPath) && HasUnifoclAgentsSection(agentsMdPath))
            {
                return dir.FullName;
            }

            var mcpJsonPath = Path.Combine(dir.FullName, ".mcp.json");
            if (File.Exists(mcpJsonPath) && HasUnifoclMcpEntry(mcpJsonPath))
            {
                return dir.FullName;
            }

            // Legacy: also recognise older installs that used .claude/settings.json
            var settingsPath = Path.Combine(dir.FullName, ".claude", "settings.json");
            if (File.Exists(settingsPath) && HasUnifoclMcpEntry(settingsPath))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }

    private static bool HasUnifoclAgentsSection(string agentsMdPath)
    {
        try
        {
            var content = File.ReadAllText(agentsMdPath, Encoding.UTF8);
            return content.Contains(MdBeginMarker, StringComparison.Ordinal)
                && content.Contains("unifocl", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool HasUnifoclMcpEntry(string settingsPath)
    {
        try
        {
            var raw = File.ReadAllText(settingsPath, Encoding.UTF8);
            if (JsonNode.Parse(raw, nodeOptions: null, documentOptions: LenientJsonDocumentOptions) is not JsonObject root)
            {
                return false;
            }

            if (root["mcpServers"] is not JsonObject mcpServers)
            {
                return false;
            }

            return mcpServers.Any(kvp =>
                kvp.Value is JsonObject server &&
                server["command"]?.GetValue<string>()
                    .Equals("unifocl", StringComparison.OrdinalIgnoreCase) == true);
        }
        catch
        {
            return false;
        }
    }

    internal async Task<bool> HandleAgentSetupAsync(
        string? projectPathRaw,
        bool dryRun,
        AgentSetupTarget target,
        Action<string> log)
    {
        var currentDirectory = Directory.GetCurrentDirectory();
        var absolutePath = string.IsNullOrWhiteSpace(projectPathRaw)
            ? currentDirectory
            : ResolveAbsolutePath(projectPathRaw, currentDirectory);

        if (!Directory.Exists(absolutePath))
        {
            log($"[red]setup[/]: directory does not exist: [white]{Markup.Escape(absolutePath)}[/]");
            return false;
        }

        if (!IsLikelyUnityProject(absolutePath))
        {
            log($"[yellow]setup[/]: [white]{Markup.Escape(absolutePath)}[/] does not look like a Unity project (no Assets/, ProjectSettings/, or Packages/manifest.json found)");
            log("[grey]setup[/]: proceeding — re-run from the Unity project root if results are unexpected");
        }

        log($"[grey]setup[/]: configuring agent MCP integration for [white]{Markup.Escape(absolutePath)}[/]");

        var installed = 0;

        if (target is AgentSetupTarget.Claude or AgentSetupTarget.Both)
        {
            if (await IsCommandAvailableAsync("claude"))
            {
                log("[grey]setup[/]: detected [white]claude[/] — configuring Claude Code integration");
                await HandleAgentInstallClaudeAsync(absolutePath, "unifocl", null, dryRun, log);
                installed++;
            }
            else
            {
                log("[grey]setup[/]: [white]claude[/] not found on PATH — skipping Claude Code integration");
                log("[grey]setup[/]:   install Claude Code then run [white]unifocl agent setup[/] again");
            }
        }

        if (target is AgentSetupTarget.Codex or AgentSetupTarget.Both)
        {
            if (await IsCommandAvailableAsync("codex"))
            {
                log("[grey]setup[/]: detected [white]codex[/] — configuring Codex integration");
                var codexInstalled = await HandleAgentInstallCodexAsync(absolutePath, "unifocl", null, dryRun, log);
                if (codexInstalled)
                {
                    installed++;
                }
                else
                {
                    log("[yellow]setup[/]: Codex integration failed — see errors above");
                }
            }
            else if (target == AgentSetupTarget.Codex)
            {
                log("[grey]setup[/]: [white]codex[/] not found on PATH");
                log("[grey]setup[/]:   install Codex then run [white]unifocl agent setup[/] again");
            }
        }

        if (installed == 0)
        {
            log("[yellow]setup[/]: no supported agent tool found on PATH (claude, codex)");
            log("[grey]setup[/]:   install Claude Code (https://claude.ai/code) and re-run");
            return false;
        }

        return true;
    }

    private static bool IsLikelyUnityProject(string path) =>
        Directory.Exists(Path.Combine(path, "Assets")) ||
        Directory.Exists(Path.Combine(path, "ProjectSettings")) ||
        File.Exists(Path.Combine(path, "Packages", "manifest.json"));

    // ── JSON / file merge helpers ─────────────────────────────────────────────

    private static readonly JsonSerializerOptions IndentedJsonOptions = new() { WriteIndented = true };

    private static readonly JsonDocumentOptions LenientJsonDocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    private static void MergeMcpJson(string mcpJsonPath, string serverName, string configRoot)
    {
        JsonObject root;
        if (File.Exists(mcpJsonPath))
        {
            var raw = File.ReadAllText(mcpJsonPath, Encoding.UTF8);
            root = JsonNode.Parse(raw, nodeOptions: null, documentOptions: LenientJsonDocumentOptions) as JsonObject
                ?? throw new InvalidOperationException(".mcp.json root is not a JSON object");
        }
        else
        {
            root = new JsonObject();
        }

        if (root["mcpServers"] is not JsonObject mcpServers)
        {
            mcpServers = new JsonObject();
            root["mcpServers"] = mcpServers;
        }

        mcpServers[serverName] = new JsonObject
        {
            ["command"] = "unifocl",
            ["args"] = new JsonArray { "--mcp-server" },
            ["env"] = new JsonObject
            {
                ["UNIFOCL_CONFIG_ROOT"] = configRoot,
            },
        };

        File.WriteAllText(mcpJsonPath, root.ToJsonString(IndentedJsonOptions), Encoding.UTF8);
    }

    private static void MergeClaudeSettingsLocal(string settingsLocalJsonPath, string serverName)
    {
        JsonObject root;
        if (File.Exists(settingsLocalJsonPath))
        {
            var raw = File.ReadAllText(settingsLocalJsonPath, Encoding.UTF8);
            root = JsonNode.Parse(raw, nodeOptions: null, documentOptions: LenientJsonDocumentOptions) as JsonObject
                ?? throw new InvalidOperationException("settings.local.json root is not a JSON object");
        }
        else
        {
            root = new JsonObject();
        }

        if (root["permissions"] is not JsonObject permissions)
        {
            permissions = new JsonObject();
            root["permissions"] = permissions;
        }

        if (permissions["allow"] is not JsonArray allow)
        {
            allow = new JsonArray();
            permissions["allow"] = allow;
        }

        var existing = allow
            .Select(n =>
            {
                try { return n?.GetValue<string>(); }
                catch { return null; }
            })
            .Where(s => s != null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase)!;

        string[] toolsToAdd =
        [
            $"mcp__{serverName}__exec",
            $"mcp__{serverName}__list_commands",
            $"mcp__{serverName}__lookup_command",
            $"mcp__{serverName}__get_agent_workflow_guide",
            $"mcp__{serverName}__get_categories",
            $"mcp__{serverName}__get_mutate_schema",
            $"mcp__{serverName}__load_category",
            $"mcp__{serverName}__reload_manifest",
            $"mcp__{serverName}__unload_category",
            $"mcp__{serverName}__use_category",
            $"mcp__{serverName}__validate_mutate_batch",
        ];

        foreach (var tool in toolsToAdd)
        {
            if (!existing.Contains(tool))
            {
                allow.Add(tool);
            }
        }

        File.WriteAllText(settingsLocalJsonPath, root.ToJsonString(IndentedJsonOptions), Encoding.UTF8);
    }

    private const string MdBeginMarker = "<!-- unifocl:begin -->";
    private const string MdEndMarker = "<!-- unifocl:end -->";

    private static void MergeMarkdownFile(string filePath, string section)
    {
        var block = $"{MdBeginMarker}\n{section}\n{MdEndMarker}";

        if (!File.Exists(filePath))
        {
            File.WriteAllText(filePath, block + "\n", Encoding.UTF8);
            return;
        }

        var content = File.ReadAllText(filePath, Encoding.UTF8);
        var beginIdx = content.IndexOf(MdBeginMarker, StringComparison.Ordinal);
        var endIdx = content.IndexOf(MdEndMarker, StringComparison.Ordinal);

        if (beginIdx >= 0 && endIdx > beginIdx)
        {
            var endOfBlock = endIdx + MdEndMarker.Length;
            content = content[..beginIdx] + block + content[endOfBlock..];
            File.WriteAllText(filePath, content, Encoding.UTF8);
        }
        else
        {
            var needsNewlines = content.Length > 0 && content[^1] != '\n';
            File.AppendAllText(filePath, (needsNewlines ? "\n\n" : "\n") + block + "\n", Encoding.UTF8);
        }
    }

    private static void MergeClaudeMd(string claudeMdPath, string serverName) =>
        MergeMarkdownFile(claudeMdPath, BuildClaudeMdSection(serverName));

    private static void MergeAgentsMd(string agentsMdPath, string serverName) =>
        MergeMarkdownFile(agentsMdPath, BuildAgentsMdSection(serverName));

    private static string BuildClaudeMdSection(string serverName) =>
        $"""
        ## unifocl — Unity MCP Integration

        The `{serverName}` MCP server gives Claude Code live access to the Unity Editor:
        hierarchy, inspector, assets, build, tests, and more.

        ### Getting started

        1. Attach to the project: call `mcp__{serverName}__exec` with command `/open <path-to-unity-project>`
        2. Discover commands: `mcp__{serverName}__list_commands`
        3. Read the full agentic guide: `mcp__{serverName}__get_agent_workflow_guide`

        ### Key MCP tools

        | Tool | Purpose |
        |------|---------|
        | `mcp__{serverName}__exec` | Run any unifocl command against the live Unity Editor |
        | `mcp__{serverName}__list_commands` | List available commands by category |
        | `mcp__{serverName}__lookup_command` | Look up a command's signature and usage |
        | `mcp__{serverName}__get_agent_workflow_guide` | Full agentic workflow guide |
        | `mcp__{serverName}__get_mutate_schema` | JSON schema for `/mutate` batch ops |
        """;

    private static string BuildAgentsMdSection(string serverName) =>
        $"""
        ## unifocl — Unity MCP Integration

        The `{serverName}` MCP server gives Codex live access to the Unity Editor:
        hierarchy, inspector, assets, build, tests, and more.

        ### Getting started

        1. Attach to the project: call `mcp__{serverName}__exec` with command `/open <path-to-unity-project>`
        2. Discover commands: call `mcp__{serverName}__list_commands`
        3. Read the full agentic guide: call `mcp__{serverName}__get_agent_workflow_guide`

        ### Key MCP tools

        | Tool | Purpose |
        |------|---------|
        | `mcp__{serverName}__exec` | Run any unifocl command against the live Unity Editor |
        | `mcp__{serverName}__list_commands` | List available commands by category |
        | `mcp__{serverName}__lookup_command` | Look up a command's signature and usage |
        | `mcp__{serverName}__get_agent_workflow_guide` | Full agentic workflow guide |
        | `mcp__{serverName}__get_mutate_schema` | JSON schema for `/mutate` batch ops |
        """;

    private static void MergeGitignore(string gitignorePath)
    {
        const string Entry = ".claude/settings.local.json";

        if (File.Exists(gitignorePath))
        {
            var lines = File.ReadAllLines(gitignorePath, Encoding.UTF8);
            if (lines.Any(l => l.Trim().Equals(Entry, StringComparison.Ordinal)))
            {
                return;
            }

            var content = File.ReadAllText(gitignorePath, Encoding.UTF8);
            var needsNewline = content.Length > 0 && content[^1] != '\n';
            File.AppendAllText(gitignorePath, (needsNewline ? "\n" : "") + Entry + "\n", Encoding.UTF8);
        }
        else
        {
            File.WriteAllText(gitignorePath, Entry + "\n", Encoding.UTF8);
        }
    }

    private static string BuildProcessArgumentString(IReadOnlyList<string> tokens)
    {
        return string.Join(' ', tokens.Select(QuoteProcessArgument));
    }

    private static string QuoteProcessArgument(string token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return "\"\"";
        }

        if (!token.Any(char.IsWhiteSpace) && !token.Contains('"', StringComparison.Ordinal))
        {
            return token;
        }

        return $"\"{token.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
    }
}

internal enum AgentSetupTarget
{
    Both,
    Claude,
    Codex,
}
