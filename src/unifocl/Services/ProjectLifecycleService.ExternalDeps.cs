using System.Runtime.InteropServices;
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

        return await HandleAgentInstallClaudeAsync(dryRun, log);
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
            return true;
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

        if (dryRun)
        {
            log("[grey]agent[/]: dry-run (no changes applied)");
            log($"[grey]agent[/]: workspace [white]{Markup.Escape(workspacePath)}[/]");
            log($"[grey]agent[/]: config-root [white]{Markup.Escape(configRoot)}[/]");
            log($"[grey]agent[/]: would run [white]codex {Markup.Escape(removeArgs)}[/]");
            log($"[grey]agent[/]: would run [white]codex {Markup.Escape(addArgs)}[/]");
            return true;
        }

        if (!await IsCommandAvailableAsync("codex"))
        {
            log("[red]agent[/]: codex is not installed or not available on PATH");
            return true;
        }

        Directory.CreateDirectory(configRoot);
        _ = await RunProcessAsync("codex", removeArgs, workspacePath, ExternalDependencyProbeTimeout);
        var addResult = await RunProcessAsync("codex", addArgs, workspacePath, ExternalDependencyProbeTimeout);
        if (addResult.ExitCode != 0)
        {
            var reason = SummarizeProcessError(addResult);
            log($"[red]agent[/]: failed to install codex integration ({Markup.Escape(reason)})");
            return true;
        }

        log("[green]agent[/]: codex integration installed");
        log($"[grey]agent[/]: server [white]{Markup.Escape(serverName)}[/] -> [white]unifocl --mcp-server[/]");
        log($"[grey]agent[/]: config-root [white]{Markup.Escape(configRoot)}[/]");
        log("[grey]agent[/]: restart Codex session to load the MCP tools");
        return true;
    }

    private async Task<bool> HandleAgentInstallClaudeAsync(
        bool dryRun,
        Action<string> log)
    {
        const string ClaudeInstallArgs = "mcp add @unifocl/claude-plugin";
        if (dryRun)
        {
            log("[grey]agent[/]: dry-run (no changes applied)");
            log($"[grey]agent[/]: would run [white]claude {Markup.Escape(ClaudeInstallArgs)}[/]");
            return true;
        }

        if (!await IsCommandAvailableAsync("claude"))
        {
            log("[red]agent[/]: claude CLI is not installed or not available on PATH");
            return true;
        }

        var installResult = await RunProcessAsync("claude", ClaudeInstallArgs, Directory.GetCurrentDirectory(), ExternalDependencyProbeTimeout);
        if (installResult.ExitCode != 0)
        {
            var reason = SummarizeProcessError(installResult);
            log($"[red]agent[/]: failed to install claude integration ({Markup.Escape(reason)})");
            return true;
        }

        log("[green]agent[/]: claude integration installed");
        log("[grey]agent[/]: plugin [white]@unifocl/claude-plugin[/] was registered");
        log("[grey]agent[/]: restart Claude Code to ensure MCP/tooling refresh");
        return true;
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
