using Spectre.Console;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

internal sealed class ProjectLifecycleService
{
    private static readonly HttpClient UnityRegistryHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(12)
    };
    private static readonly HttpClient GitHubReleasesHttpClient = CreateGitHubReleasesHttpClient();
    private static readonly TimeSpan GitVersionProbeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan GitCloneTimeout = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan CompileRecoveryRetryDelay = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan DefaultOpenDaemonStartupTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ProjectOpenLockTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CliUpdateDownloadTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ExternalDependencyProbeTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan ExternalDependencyInstallTimeout = TimeSpan.FromMinutes(5);
    private const int DefaultRecentPruneStaleDays = 14;
    private const int DefaultCompileRecoveryRetryCount = 3;
    private const string ExternalDependencyAutoInstallEnv = "UNIFOCL_AUTO_INSTALL_EXTERNAL_DEPS";
    private const string GitHubReleaseOwner = "Kiankinakomochi";
    private const string GitHubReleaseRepository = "unifocl";
    private const string RequiredMcpPackageId = "com.coplaydev.unity-mcp";
    private const string RequiredMcpPackageTarget = "https://github.com/CoplayDev/unity-mcp.git?path=/MCPForUnity#main";
    private const string DefaultSampleSceneAssetPath = "Assets/SampleScene.unity";
    private const string LastOpenedSceneMarkerRelativePath = ".unifocl/last-opened-scene.txt";
    private static readonly Dictionary<string, string> RequiredMcpDependencyFloor = new(StringComparer.Ordinal)
    {
        // Required for Texture2D.EncodeToPNG used by MCP runtime screenshot helpers.
        ["com.unity.modules.imageconversion"] = "1.0.0"
    };

    private readonly EditorDependencyInitializerService _editorDependencyInitializerService = new();
    private readonly ProjectViewService _projectViewService = new();
    private readonly RecentProjectHistoryService _recentProjectHistoryService = new();
    private readonly HierarchyDaemonClient _daemonClient = new();

    public async Task<bool> TryHandleLifecycleCommandAsync(
        string input,
        CommandSpec matched,
        CliSessionState session,
        DaemonControlService daemonControlService,
        DaemonRuntime daemonRuntime,
        Action<string> log)
    {
        return matched.Trigger switch
        {
            "/open" => await HandleOpenAsync(input, matched, session, daemonControlService, daemonRuntime, log),
            "/new" => await HandleNewAsync(input, matched, session, daemonControlService, daemonRuntime, log),
            "/clone" => await HandleCloneAsync(input, matched, session, daemonControlService, daemonRuntime, log),
            "/recent" => await HandleRecentAsync(input, matched, session, daemonControlService, daemonRuntime, log),
            "/help" => await HandleHelpAsync(input, matched, log),
            "/status" => await HandleStatusAsync(session, daemonRuntime, log),
            "/doctor" => await HandleDoctorAsync(session, daemonRuntime, log),
            "/scan" => await HandleScanAsync(input, matched, log),
            "/info" => await HandleInfoAsync(input, matched, session, log),
            "/logs" => await HandleLogsAsync(input, matched, session, daemonRuntime, log),
            "/examples" => await HandleExamplesAsync(log),
            "/update" => await HandleUpdateAsync(log),
            "/install-hook" => await HandleInstallHookAsync(session, daemonControlService, daemonRuntime, log),
            "/agent install" => await HandleAgentInstallAsync(input, matched, log),
            "/unity detect" => await HandleUnityDetectAsync(log),
            "/unity set" => await HandleUnitySetAsync(input, matched, log),
            "/close" => await HandleCloseAsync(session, daemonControlService, daemonRuntime, log),
            "/init" => await HandleInitAsync(input, matched, session, daemonControlService, daemonRuntime, log),
            "/config" => await HandleConfigAsync(input, matched, log),
            _ => false
        };
    }

    public async Task<bool> TryHandleRecentSelectionToggleAsync(
        CliSessionState session,
        DaemonControlService daemonControlService,
        DaemonRuntime daemonRuntime,
        Action<string> log)
    {
        if (Console.IsInputRedirected)
        {
            log("[yellow]recent[/]: selection mode requires a TTY terminal");
            return true;
        }

        if (session.RecentProjectEntries.Count == 0)
        {
            log("[yellow]recent[/]: no cached recent list; run /recent first");
            return true;
        }

        await RunRecentSelectionModeAsync(session, daemonControlService, daemonRuntime, log);
        return true;
    }

    public async Task<bool> EnsureProjectOpenForAgenticEndpointAsync(
        string projectPath,
        CliSessionState session,
        DaemonControlService daemonControlService,
        DaemonRuntime daemonRuntime,
        Action<string> log)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            return false;
        }

        var resolvedProjectPath = ResolveAbsolutePath(projectPath, Directory.GetCurrentDirectory());
        return await TryOpenProjectAsync(
            resolvedProjectPath,
            session,
            daemonControlService,
            daemonRuntime,
            _editorDependencyInitializerService,
            promptForInitialization: true,
            ensureMcpHostDependencyCheck: true,
            allowUnsafe: false,
            daemonStartupTimeout: DefaultOpenDaemonStartupTimeout,
            log: log);
    }

    public async Task PerformSafeExitCleanupAsync(
        CliSessionState session,
        DaemonControlService daemonControlService,
        DaemonRuntime daemonRuntime,
        Action<string> log)
    {
        await HandleCloseAsync(session, daemonControlService, daemonRuntime, log);

        daemonRuntime.CleanStaleEntries();
        var remainingPorts = daemonRuntime.GetAll()
            .Select(instance => instance.Port)
            .Distinct()
            .OrderBy(port => port)
            .ToList();

        if (remainingPorts.Count == 0)
        {
            log("[grey]exit[/]: teardown complete");
            return;
        }

        var stoppedCount = 0;
        foreach (var port in remainingPorts)
        {
            if (await daemonControlService.StopDaemonByPortAsync(port, daemonRuntime, session, log))
            {
                stoppedCount++;
            }
        }

        if (stoppedCount == remainingPorts.Count)
        {
            log($"[green]exit[/]: teardown complete; stopped {stoppedCount} daemon(s)");
        }
        else
        {
            log($"[yellow]exit[/]: teardown partial; stopped {stoppedCount}/{remainingPorts.Count} daemon(s)");
        }

        daemonControlService.StopUnityLicensingClients(log);
    }

    private async Task<bool> HandleOpenAsync(
        string input,
        CommandSpec matched,
        CliSessionState session,
        DaemonControlService daemonControlService,
        DaemonRuntime daemonRuntime,
        Action<string> log)
    {
        var args = ParseCommandArgs(input, matched.Trigger);
        if (!TryParseOpenArgs(args, out var openPath, out var allowUnsafe, out var daemonStartupTimeout, out var openParseError))
        {
            log($"[red]error[/]: {Markup.Escape(openParseError)}");
            return true;
        }

        var projectPath = ResolveAbsolutePath(openPath, Directory.GetCurrentDirectory());

        await TryOpenProjectAsync(
            projectPath,
            session,
            daemonControlService,
            daemonRuntime,
            _editorDependencyInitializerService,
            promptForInitialization: true,
            ensureMcpHostDependencyCheck: true,
            allowUnsafe: allowUnsafe,
            daemonStartupTimeout: daemonStartupTimeout,
            log: log);
        return true;
    }

    private async Task<bool> HandleNewAsync(
        string input,
        CommandSpec matched,
        CliSessionState session,
        DaemonControlService daemonControlService,
        DaemonRuntime daemonRuntime,
        Action<string> log)
    {
        var args = ParseCommandArgs(input, matched.Trigger);
        if (!TryParseNewArgs(args, out var projectName, out var requestedVersion, out var allowUnsafe, out var parseError))
        {
            log($"[red]error[/]: {Markup.Escape(parseError)}");
            return true;
        }

        if (string.IsNullOrWhiteSpace(requestedVersion) && CliRuntimeState.SuppressConsoleOutput)
        {
            log("[red]error[/]: /new in agentic mode requires an explicit Unity version");
            log("[red]error[/]: usage /new <project-name> <unity-version> [--allow-unsafe]");
            return true;
        }

        var availableEditors = UnityEditorPathService.DetectInstalledEditors(out var detectedHubRoot);
        if (availableEditors.Count == 0)
        {
            log("[red]error[/]: no Unity editors were detected from Unity Hub/editor environment");
            return true;
        }

        UnityEditorPathService.UnityEditorInstallation selectedEditor;
        if (!string.IsNullOrWhiteSpace(requestedVersion))
        {
            selectedEditor = availableEditors.FirstOrDefault(x => x.Version.Equals(requestedVersion, StringComparison.OrdinalIgnoreCase))
                ?? new UnityEditorPathService.UnityEditorInstallation(string.Empty, string.Empty);
            if (string.IsNullOrWhiteSpace(selectedEditor.Version))
            {
                log($"[red]error[/]: requested Unity version is not installed: {Markup.Escape(requestedVersion)}");
                return true;
            }
        }
        else if (Console.IsInputRedirected)
        {
            selectedEditor = availableEditors[0];
            log($"[yellow]new[/]: non-interactive input; defaulting to newest detected editor [white]{Markup.Escape(selectedEditor.Version)}[/]");
        }
        else
        {
            var hubLabel = string.IsNullOrWhiteSpace(detectedHubRoot) ? "unknown location" : detectedHubRoot;
            log($"[grey]new[/]: detected Unity Hub editors from [white]{Markup.Escape(hubLabel)}[/]");
            selectedEditor = CliTheme.PromptWithDividers(() =>
                AnsiConsole.Prompt(
                    new SelectionPrompt<UnityEditorPathService.UnityEditorInstallation>()
                        .Title("Choose Unity editor version")
                        .PageSize(ResolvePromptPageSize(availableEditors.Count, 12))
                        .UseConverter(editor => $"{editor.Version} ({editor.EditorPath})")
                        .AddChoices(availableEditors)));
        }

        var unityVersion = selectedEditor.Version;

        var projectPath = ResolveAbsolutePath(projectName, Directory.GetCurrentDirectory());
        log($"[grey]new[/]: step 1/6 create project directory -> [white]{Markup.Escape(projectPath)}[/]");

        if (Directory.Exists(projectPath) && Directory.EnumerateFileSystemEntries(projectPath).Any())
        {
            log("[red]error[/]: target directory already exists and is not empty");
            return true;
        }

        try
        {
            Directory.CreateDirectory(projectPath);
            Directory.CreateDirectory(Path.Combine(projectPath, "Assets"));
            Directory.CreateDirectory(Path.Combine(projectPath, "Packages"));
            Directory.CreateDirectory(Path.Combine(projectPath, "ProjectSettings"));
        }
        catch (Exception ex)
        {
            log($"[red]error[/]: failed to create Unity folder structure ({Markup.Escape(ex.Message)})");
            return true;
        }

        log("[grey]new[/]: step 2/6 write Unity package manifest");
        var manifestResult = WriteDefaultUnityManifest(projectPath);
        if (!manifestResult.Ok)
        {
            log($"[red]error[/]: {Markup.Escape(manifestResult.Error)}");
            return true;
        }

        log($"[grey]new[/]: step 3/6 set Unity editor version [white]{Markup.Escape(unityVersion)}[/]");
        var versionResult = WriteProjectVersion(projectPath, unityVersion);
        if (!versionResult.Ok)
        {
            log($"[red]error[/]: {Markup.Escape(versionResult.Error)}");
            return true;
        }

        if (!UnityEditorPathService.TrySaveProjectEditorPath(projectPath, selectedEditor.EditorPath, out var saveEditorError))
        {
            log($"[yellow]new[/]: unable to persist project-editor pair ({Markup.Escape(saveEditorError ?? "unknown error")})");
        }

        log("[grey]new[/]: step 4/6 generate local templates and bridge config");
        var configResult = EnsureProjectLocalConfig(projectPath);
        if (!configResult.Ok)
        {
            log($"[red]error[/]: {Markup.Escape(configResult.Error)}");
            return true;
        }

        log("[grey]new[/]: step 5/6 initialize project bridge packages");
        var initializeResult = await InitializeProjectForLifecycleAsync(
            projectPath,
            log);
        if (!initializeResult.Ok)
        {
            log($"[red]error[/]: {Markup.Escape(initializeResult.Error)}");
            return true;
        }

        if (CliRuntimeState.SuppressConsoleOutput)
        {
            log("[green]new[/]: Unity project scaffold ready");
            log($"[grey]new[/]: agentic mode skips auto-open; run [white]/open {Markup.Escape(projectPath)}[/] when ready");
            return true;
        }

        log("[grey]new[/]: step 6/6 open initialized project");
        if (await TryOpenProjectAsync(
                projectPath,
                session,
                daemonControlService,
                daemonRuntime,
                _editorDependencyInitializerService,
                promptForInitialization: false,
                ensureMcpHostDependencyCheck: false,
                allowUnsafe: allowUnsafe,
                daemonStartupTimeout: DefaultOpenDaemonStartupTimeout,
                log: log))
        {
            log("[green]new[/]: Unity project scaffold ready");
        }
        else
        {
            log("[yellow]new[/]: project scaffolded, but auto-open failed");
        }

        return true;
    }

    private async Task<bool> HandleCloneAsync(
        string input,
        CommandSpec matched,
        CliSessionState session,
        DaemonControlService daemonControlService,
        DaemonRuntime daemonRuntime,
        Action<string> log)
    {
        var args = ParseCommandArgs(input, matched.Trigger);
        if (!TryParseCloneArgs(args, out var gitUrl, out var allowUnsafe, out var parseError))
        {
            log($"[red]error[/]: {Markup.Escape(parseError)}");
            return true;
        }

        log("[grey]clone[/]: step 1/4 validate git binary");
        var gitVersion = await RunProcessAsync("git", "--version", Directory.GetCurrentDirectory(), GitVersionProbeTimeout);
        if (gitVersion.ExitCode != 0)
        {
            log("[red]error[/]: git is not available on PATH");
            return true;
        }

        var targetFolderName = GuessRepoFolderName(gitUrl);
        var targetPath = Path.Combine(Directory.GetCurrentDirectory(), targetFolderName);
        log($"[grey]clone[/]: step 2/4 clone [white]{Markup.Escape(gitUrl)}[/] -> [white]{Markup.Escape(targetPath)}[/]");

        if (Directory.Exists(targetPath))
        {
            log("[red]error[/]: clone target already exists");
            return true;
        }

        var cloneResult = await RunProcessAsync(
            "git",
            $"clone \"{gitUrl}\" \"{targetPath}\"",
            Directory.GetCurrentDirectory(),
            GitCloneTimeout);
        if (cloneResult.ExitCode != 0)
        {
            log($"[red]error[/]: git clone failed ({Markup.Escape(SummarizeProcessError(cloneResult))})");
            return true;
        }

        log("[grey]clone[/]: step 3/4 write local templates and bridge config");
        var configResult = EnsureProjectLocalConfig(targetPath);
        if (!configResult.Ok)
        {
            log($"[red]error[/]: {Markup.Escape(configResult.Error)}");
            return true;
        }

        log("[grey]clone[/]: step 4/4 open cloned project");
        if (await TryOpenProjectAsync(
                targetPath,
                session,
                daemonControlService,
                daemonRuntime,
                _editorDependencyInitializerService,
                promptForInitialization: true,
                ensureMcpHostDependencyCheck: true,
                allowUnsafe: allowUnsafe,
                daemonStartupTimeout: DefaultOpenDaemonStartupTimeout,
                log: log))
        {
            log("[green]clone[/]: repository cloned and prepared");
        }
        else
        {
            log("[yellow]clone[/]: repository cloned; open skipped (not a Unity project yet)");
        }

        return true;
    }

    private async Task<bool> HandleInitAsync(
        string input,
        CommandSpec matched,
        CliSessionState session,
        DaemonControlService daemonControlService,
        DaemonRuntime daemonRuntime,
        Action<string> log)
    {
        var args = ParseCommandArgs(input, matched.Trigger);
        if (args.Count > 1)
        {
            log("[red]error[/]: usage /init <path-to-project?>");
            return true;
        }

        string? targetPath = null;
        if (args.Count == 1)
        {
            targetPath = ResolveAbsolutePath(args[0], Directory.GetCurrentDirectory());
        }
        else if (!string.IsNullOrWhiteSpace(session.CurrentProjectPath))
        {
            targetPath = session.CurrentProjectPath;
        }

        if (string.IsNullOrWhiteSpace(targetPath))
        {
            log("[red]error[/]: no attached project; use /init <path-to-project> or /open first");
            return true;
        }

        var initializeResult = await InitializeProjectForLifecycleAsync(
            targetPath,
            log);
        if (!initializeResult.Ok)
        {
            log($"[red]error[/]: {Markup.Escape(initializeResult.Error)}");
            return true;
        }

        log($"[green]init[/]: ready at [white]{Markup.Escape(targetPath)}[/]");
        return true;
    }

    private async Task<OperationResult> InitializeProjectForLifecycleAsync(
        string projectPath,
        Action<string> log)
    {
        var configResult = await RunWithStatusAsync(
            "Preparing local bridge config...",
            () => Task.FromResult(EnsureProjectLocalConfig(projectPath)));
        if (!configResult.Ok)
        {
            return configResult;
        }

        var packageFixResult = await RunWithStatusAsync(
            "Checking project package references...",
            () => EnsureRequiredUnityPackageReferencesAsync(projectPath));
        if (!packageFixResult.Ok)
        {
            return packageFixResult;
        }

        var initResult = await RunWithStatusAsync(
            "Installing editor bridge dependencies...",
            () => Task.FromResult(_editorDependencyInitializerService.InitializeProject(projectPath, log)));
        if (!initResult.Ok)
        {
            return initResult;
        }

        return OperationResult.Success();
    }

    private async Task<OperationResult> EnsureMcpPackageInstalledAsync(
        string projectPath,
        Action<string> log)
    {
        _ = log;
        var ensureResult = await EnsureRequiredUnityPackageReferencesAsync(projectPath);
        if (!ensureResult.Ok)
        {
            return ensureResult;
        }

        if (!ManifestContainsPackage(projectPath, RequiredMcpPackageId))
        {
            return OperationResult.Fail($"manifest install verification failed: missing {RequiredMcpPackageId}");
        }

        return OperationResult.Success();
    }

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

    private static async Task<bool> EnsureProjectDaemonWithCompileRecoveryAsync(
        DaemonControlService daemonControlService,
        string projectPath,
        DaemonRuntime daemonRuntime,
        CliSessionState session,
        Action<string> log,
        bool requireBridgeMode,
        bool preferHostMode,
        bool allowUnsafe,
        TimeSpan daemonStartupTimeout)
    {
        var started = await daemonControlService.EnsureProjectDaemonAsync(
            projectPath,
            daemonRuntime,
            session,
            log,
            requireBridgeMode: requireBridgeMode,
            preferHostMode: preferHostMode,
            allowUnsafe: allowUnsafe,
            startupTimeout: daemonStartupTimeout);
        if (started || allowUnsafe)
        {
            return started;
        }

        if (!ShouldRetryAfterCompileFailure(daemonControlService, out var summary))
        {
            return false;
        }

        for (var attempt = 1; attempt <= DefaultCompileRecoveryRetryCount; attempt++)
        {
            if (!string.IsNullOrWhiteSpace(summary))
            {
                log($"[yellow]daemon[/]: compile errors detected during startup ({Markup.Escape(summary)}). waiting before retry {attempt}/{DefaultCompileRecoveryRetryCount}...");
            }
            else
            {
                log($"[yellow]daemon[/]: compile errors detected during startup. waiting before retry {attempt}/{DefaultCompileRecoveryRetryCount}...");
            }

            await Task.Delay(CompileRecoveryRetryDelay);

            started = await daemonControlService.EnsureProjectDaemonAsync(
                projectPath,
                daemonRuntime,
                session,
                log,
                requireBridgeMode: requireBridgeMode,
                preferHostMode: preferHostMode,
                allowUnsafe: allowUnsafe,
                startupTimeout: daemonStartupTimeout);
            if (started)
            {
                log("[green]daemon[/]: daemon startup recovered after package/compile warmup retry");
                return true;
            }

            if (!ShouldRetryAfterCompileFailure(daemonControlService, out summary))
            {
                return false;
            }
        }

        return false;
    }

    private static bool ShouldRetryAfterCompileFailure(
        DaemonControlService daemonControlService,
        out string? summary)
    {
        summary = null;
        if (!daemonControlService.TryGetLastStartupFailure(out var failure) || failure is null || !failure.IsCompileError)
        {
            return false;
        }

        summary = failure.Summary;
        return true;
    }

    private async Task<bool> HandleRecentAsync(
        string input,
        CommandSpec matched,
        CliSessionState session,
        DaemonControlService daemonControlService,
        DaemonRuntime daemonRuntime,
        Action<string> log)
    {
        var args = ParseCommandArgs(input, matched.Trigger);
        if (!TryParseRecentArgs(args, out var indexRaw, out var allowUnsafe, out var pruneRequested, out var parseError))
        {
            log($"[red]error[/]: {Markup.Escape(parseError)}");
            return true;
        }

        if (pruneRequested)
        {
            if (!await HandleRecentPruneAsync(log))
            {
                return true;
            }
        }

        var recentResult = await RunWithStatusAsync("Loading recent projects...", () =>
        {
            var ok = _recentProjectHistoryService.TryGetRecentProjects(100, out var loadedEntries, out var loadError);
            return Task.FromResult((ok, loadedEntries, loadError));
        });

        if (!recentResult.ok)
        {
            log($"[red]error[/]: {Markup.Escape(recentResult.loadError ?? "failed to load recent projects")}");
            return true;
        }

        var entries = recentResult.loadedEntries;
        if (entries.Count == 0)
        {
            session.RecentProjectEntries.Clear();
            session.RecentSelectionAllowUnsafe = false;
            log("[grey]recent[/]: no recent projects found");
            return true;
        }

        LogRecentEntries(entries, log);
        session.RecentProjectEntries.Clear();
        session.RecentProjectEntries.AddRange(entries);
        session.RecentSelectionAllowUnsafe = allowUnsafe;

        if (!string.IsNullOrWhiteSpace(indexRaw))
        {
            if (!int.TryParse(indexRaw, out var idx) || idx <= 0)
            {
                log("[red]error[/]: idx must be a positive integer");
                return true;
            }

            if (idx > entries.Count)
            {
                log($"[red]error[/]: idx {idx} is out of range (1-{entries.Count})");
                return true;
            }

            var selectedEntry = entries[idx - 1];
            var confirmMessage = $"Open recent project [white]{idx}[/]: [white]{Markup.Escape(selectedEntry.ProjectPath)}[/]?";
            if (!CliTheme.ConfirmWithDividers(confirmMessage, defaultValue: true))
            {
                log("[grey]recent[/]: cancelled");
                return true;
            }

            return await OpenRecentSelectionAsync(selectedEntry, session, daemonControlService, daemonRuntime, allowUnsafe, log);
        }

        log("[grey]recent[/]: press [white]F7/F8[/] to enter selection mode ([white]↑/↓[/] move, [white]idx[/] jump, [white]Enter[/] open, [white]F7/F8[/] exit)");
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

    private static void LogRecentEntries(IReadOnlyList<RecentProjectEntry> entries, Action<string> log)
    {
        log("[grey]recent[/]: most recently opened projects");
        var visibleCount = ResolveVisibleRecentEntryCount(entries.Count, reservedRows: 2);
        for (var i = 0; i < visibleCount; i++)
        {
            var entry = entries[i];
            var opened = entry.LastOpenedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz");
            var plain = $"{entry.ProjectPath} ({opened})";
            var clamped = ClampToViewport(plain);
            log($"[grey]recent[/]: [white]{i + 1}[/]. [white]{Markup.Escape(clamped)}[/]");
        }

        var omittedCount = Math.Max(0, entries.Count - visibleCount);
        if (omittedCount > 0)
        {
            log($"[grey]recent[/]: showing [white]{visibleCount}[/] projects ([white]+{omittedCount}[/] more)");
        }
    }

    private async Task<bool> HandleRecentPruneAsync(Action<string> log)
    {
        if (!TryLoadCliConfig(out var config, out var configError))
        {
            log($"[red]error[/]: {Markup.Escape(configError ?? "failed to read config")}");
            return false;
        }

        var staleDays = ResolveRecentPruneStaleDays(config);
        var pruneResult = await RunWithStatusAsync("Pruning recent projects...", () =>
        {
            var ok = _recentProjectHistoryService.TryPruneRecentProjects(
                staleDays,
                DateTimeOffset.UtcNow,
                out var summary,
                out var error);
            return Task.FromResult((ok, summary, error));
        });

        if (!pruneResult.ok)
        {
            log($"[red]error[/]: {Markup.Escape(pruneResult.error ?? "failed to prune recent projects")}");
            return false;
        }

        var summary = pruneResult.summary;
        if (summary.RemovedTotal == 0)
        {
            log($"[grey]recent[/]: prune complete (no entries removed, stale threshold: {staleDays} days)");
            return true;
        }

        log(
            $"[green]recent[/]: pruned [white]{summary.RemovedTotal}[/] entries " +
            $"(missing: [white]{summary.RemovedMissing}[/], stale: [white]{summary.RemovedStale}[/], " +
            $"remaining: [white]{summary.RemainingCount}[/], stale threshold: [white]{staleDays}[/] days)");
        return true;
    }

    private async Task<bool> OpenRecentSelectionAsync(
        RecentProjectEntry selectedEntry,
        CliSessionState session,
        DaemonControlService daemonControlService,
        DaemonRuntime daemonRuntime,
        bool allowUnsafe,
        Action<string> log)
    {
        log($"[grey]recent[/]: opening [white]{Markup.Escape(selectedEntry.ProjectPath)}[/]");
        return await TryOpenProjectAsync(
            selectedEntry.ProjectPath,
            session,
            daemonControlService,
            daemonRuntime,
            _editorDependencyInitializerService,
            promptForInitialization: true,
            ensureMcpHostDependencyCheck: true,
            allowUnsafe: allowUnsafe,
            daemonStartupTimeout: DefaultOpenDaemonStartupTimeout,
            log: log);
    }

    private static async Task<T> RunWithStatusAsync<T>(string statusText, Func<Task<T>> action)
    {
        if (Console.IsInputRedirected)
        {
            return await action();
        }

        var result = default(T);
        await AnsiConsole.Status()
            .Spinner(TuiTrackableProgress.StatusSpinner)
            .StartAsync(statusText, async _ =>
            {
                result = await action();
            });
        return result!;
    }

    private async Task RunRecentSelectionModeAsync(
        CliSessionState session,
        DaemonControlService daemonControlService,
        DaemonRuntime daemonRuntime,
        Action<string> log)
    {
        var entries = session.RecentProjectEntries;
        if (entries.Count == 0)
        {
            log("[yellow]recent[/]: no recent entries are available");
            return;
        }

        var selectedIndex = 0;
        var lastRenderedSelectedIndex = -1;
        var typedIndexBuffer = string.Empty;
        long typedIndexLastInputTick = 0;
        var (knownViewportWidth, knownViewportHeight) = TuiConsoleViewport.GetWindowSizeOrDefault();
        RenderRecentSelectionIfChanged(entries, selectedIndex, ref lastRenderedSelectedIndex);

        while (true)
        {
            if (!TuiConsoleViewport.WaitForKeyOrResize(ref knownViewportWidth, ref knownViewportHeight, out var key))
            {
                RenderRecentSelectionFrame(entries, selectedIndex);
                continue;
            }

            var intent = KeyboardIntentReader.ReadIntentFromFirstKey(key);
            if (SelectionIndexJumpHelper.TryApply(
                    intent,
                    index =>
                    {
                        // Recent list displays 1-based indices.
                        var target = index - 1;
                        if ((uint)target >= entries.Count)
                        {
                            return false;
                        }

                        selectedIndex = target;
                        RenderRecentSelectionIfChanged(entries, selectedIndex, ref lastRenderedSelectedIndex);
                        return true;
                    },
                    ref typedIndexBuffer,
                    ref typedIndexLastInputTick))
            {
                continue;
            }

            if (intent == KeyboardIntent.Up)
            {
                var nextSelectedIndex = selectedIndex <= 0 ? entries.Count - 1 : selectedIndex - 1;
                RenderRecentSelectionIfChanged(entries, nextSelectedIndex, ref lastRenderedSelectedIndex);
                selectedIndex = nextSelectedIndex;
                continue;
            }

            if (intent == KeyboardIntent.Down)
            {
                var nextSelectedIndex = selectedIndex >= entries.Count - 1 ? 0 : selectedIndex + 1;
                RenderRecentSelectionIfChanged(entries, nextSelectedIndex, ref lastRenderedSelectedIndex);
                selectedIndex = nextSelectedIndex;
                continue;
            }

            if (intent is KeyboardIntent.FocusProject or KeyboardIntent.Escape)
            {
                AnsiConsole.Clear();
                log("[i] recent selection mode disabled");
                return;
            }

            if (intent != KeyboardIntent.Enter)
            {
                continue;
            }

            var selectedEntry = entries[selectedIndex];
            AnsiConsole.Clear();
            await OpenRecentSelectionAsync(
                selectedEntry,
                session,
                daemonControlService,
                daemonRuntime,
                session.RecentSelectionAllowUnsafe,
                log);
            return;
        }
    }

    private static void RenderRecentSelectionIfChanged(
        IReadOnlyList<RecentProjectEntry> entries,
        int selectedIndex,
        ref int lastRenderedSelectedIndex)
    {
        if (selectedIndex == lastRenderedSelectedIndex)
        {
            return;
        }

        lastRenderedSelectedIndex = selectedIndex;
        RenderRecentSelectionFrame(entries, selectedIndex);
    }

    private static void RenderRecentSelectionFrame(IReadOnlyList<RecentProjectEntry> entries, int selectedIndex)
    {
        AnsiConsole.Clear();
        CliTheme.MarkupLine(CliTheme.PromptDividerMarkup);
        CliTheme.MarkupLine("[grey]recent[/]: selection mode ([white]↑/↓[/] move, [white]idx[/] jump, [white]Enter[/] open, [white]Esc/F7/F8[/] exit)");
        var (windowStart, visibleCount, omittedCount) = ResolveVisibleRecentEntryWindow(entries.Count, selectedIndex);
        for (var i = windowStart; i < windowStart + visibleCount; i++)
        {
            var entry = entries[i];
            var opened = entry.LastOpenedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz");
            var plain = ClampToViewport($"recent: {i + 1}. {entry.ProjectPath} ({opened})");
            if (i == selectedIndex)
            {
                CliTheme.MarkupLine(CliTheme.CursorWrapEscaped(Markup.Escape($"> {plain}")));
                continue;
            }

            CliTheme.MarkupLine($"[grey]{Markup.Escape(plain)}[/]");
        }

        if (omittedCount > 0)
        {
            CliTheme.MarkupLine($"[grey]recent[/]: showing [white]{visibleCount}[/] projects ([white]+{omittedCount}[/] more)");
        }
        CliTheme.MarkupLine(CliTheme.PromptDividerMarkup);
    }

    private static int ResolveVisibleRecentEntryCount(int totalEntries, int reservedRows)
    {
        var intendedRows = reservedRows + totalEntries;
        var excessRows = TuiConsoleViewport.GetExcessRows(intendedRows);
        var visibleCount = Math.Max(1, totalEntries - excessRows);
        if (visibleCount < totalEntries)
        {
            // Reserve one line for a truncation summary footer.
            visibleCount = Math.Max(1, visibleCount - 1);
        }

        return Math.Min(totalEntries, visibleCount);
    }

    private static (int WindowStart, int VisibleCount, int OmittedCount) ResolveVisibleRecentEntryWindow(int totalEntries, int selectedIndex)
    {
        var visibleCount = ResolveVisibleRecentEntryCount(totalEntries, reservedRows: 4);
        if (visibleCount >= totalEntries)
        {
            return (0, totalEntries, 0);
        }

        var maxWindowStart = Math.Max(0, totalEntries - visibleCount);
        var centeredWindowStart = selectedIndex - (visibleCount / 2);
        var windowStart = Math.Clamp(centeredWindowStart, 0, maxWindowStart);
        var omittedCount = totalEntries - visibleCount;
        return (windowStart, visibleCount, omittedCount);
    }

    private static string ClampToViewport(string value)
    {
        var excessColumns = TuiConsoleViewport.GetExcessColumns(value.Length);
        if (excessColumns <= 0)
        {
            return value;
        }

        var keep = Math.Max(1, value.Length - excessColumns - 1);
        return $"{value[..keep]}…";
    }

    private Task<bool> HandleUnityDetectAsync(Action<string> log)
    {
        var editors = UnityEditorPathService.DetectInstalledEditors(out var hubRoot);
        if (editors.Count == 0)
        {
            log("[yellow]unity[/]: no installed editors detected");
            return Task.FromResult(true);
        }

        if (!string.IsNullOrWhiteSpace(hubRoot))
        {
            log($"[grey]unity[/]: Hub root [white]{Markup.Escape(hubRoot)}[/]");
        }

        log("[grey]unity[/]: detected installed editors");
        foreach (var editor in editors)
        {
            log($"[grey]unity[/]: [white]{Markup.Escape(editor.Version)}[/] -> [white]{Markup.Escape(editor.EditorPath)}[/]");
        }

        return Task.FromResult(true);
    }

    private Task<bool> HandleUnitySetAsync(string input, CommandSpec matched, Action<string> log)
    {
        var args = ParseCommandArgs(input, matched.Trigger);
        if (args.Count != 1)
        {
            log("[red]error[/]: usage /unity set <path>");
            return Task.FromResult(true);
        }

        var unityPath = ResolveAbsolutePath(args[0], Directory.GetCurrentDirectory());
        if (!UnityEditorPathService.TrySetDefaultEditorPath(unityPath, out var saveError))
        {
            log($"[red]error[/]: {Markup.Escape(saveError ?? "failed to save default Unity editor path")}");
            return Task.FromResult(true);
        }

        Environment.SetEnvironmentVariable("UNITY_PATH", unityPath);
        log($"[green]unity[/]: default editor set -> [white]{Markup.Escape(unityPath)}[/]");
        return Task.FromResult(true);
    }

    private static Task<bool> HandleHelpAsync(string input, CommandSpec matched, Action<string> log)
    {
        var args = ParseCommandArgs(input, matched.Trigger);
        var topic = args.Count == 0 ? "root" : args[0].Trim().ToLowerInvariant();

        switch (topic)
        {
            case "root":
            case "general":
                LogHelpSection("root commands", CliCommandCatalog.CreateRootCommands(), log);
                log("[grey]help[/]: topics -> project | inspector | build | upm | daemon");
                log("[grey]help[/]: usage  -> /help <topic>");
                return Task.FromResult(true);
            case "project":
                LogHelpSection("project mode commands", CliCommandCatalog.CreateProjectCommands(), log);
                return Task.FromResult(true);
            case "inspector":
                LogHelpSection("inspector mode commands", CliCommandCatalog.CreateInspectorCommands(), log);
                return Task.FromResult(true);
            case "build":
                LogHelpSection(
                    "build commands",
                    CliCommandCatalog.CreateRootCommands().Where(command => command.Trigger.StartsWith("/build", StringComparison.Ordinal)).ToList(),
                    log);
                return Task.FromResult(true);
            case "upm":
                LogHelpSection(
                    "upm commands",
                    CliCommandCatalog.CreateRootCommands().Where(command => command.Trigger.StartsWith("/upm", StringComparison.Ordinal)).ToList(),
                    log);
                return Task.FromResult(true);
            case "daemon":
                LogHelpSection(
                    "daemon commands",
                    CliCommandCatalog.CreateRootCommands().Where(command => command.Trigger.StartsWith("/daemon", StringComparison.Ordinal)).ToList(),
                    log);
                return Task.FromResult(true);
            default:
                log($"[yellow]help[/]: unknown topic [white]{Markup.Escape(topic)}[/]");
                log("[grey]help[/]: available topics -> root | project | inspector | build | upm | daemon");
                return Task.FromResult(true);
        }
    }

    private static Task<bool> HandleStatusAsync(
        CliSessionState session,
        DaemonRuntime daemonRuntime,
        Action<string> log)
    {
        log("[grey]status[/]: session");
        log($"[grey]status[/]: mode={session.Mode}, context={session.ContextMode}");
        log($"[grey]status[/]: attached-port={(DaemonControlService.GetPort(session)?.ToString() ?? "none")}");
        log($"[grey]status[/]: project={(string.IsNullOrWhiteSpace(session.CurrentProjectPath) ? "none" : session.CurrentProjectPath)}");
        log($"[grey]status[/]: safe-mode={(session.SafeModeEnabled ? "on" : "off")}");
        if (session.LastOpenedUtc is DateTimeOffset openedAtUtc)
        {
            log($"[grey]status[/]: last-opened={openedAtUtc.LocalDateTime:yyyy-MM-dd HH:mm:ss zzz}");
        }

        var daemons = daemonRuntime.GetAll()
            .OrderBy(instance => instance.Port)
            .ToList();
        if (daemons.Count == 0)
        {
            log("[grey]status[/]: daemon runtime -> no active daemon entries");
            return Task.FromResult(true);
        }

        log($"[grey]status[/]: daemon runtime -> {daemons.Count} active instance(s)");
        foreach (var daemon in daemons)
        {
            var uptime = DateTime.UtcNow - daemon.StartedAtUtc;
            var mode = daemon.Headless ? "host" : "bridge";
            var project = string.IsNullOrWhiteSpace(daemon.ProjectPath) ? "-" : daemon.ProjectPath;
            log($"[grey]status[/]: port={daemon.Port} pid={daemon.Pid} mode={mode} uptime={uptime.TotalMinutes:0}m project={Markup.Escape(project)}");
        }

        return Task.FromResult(true);
    }

    private static Task<bool> HandleExamplesAsync(Action<string> log)
    {
        log("[grey]examples[/]: common workflows");
        log("[grey]examples[/]: open project -> /open ./MyUnityProject");
        log("[grey]examples[/]: attach daemon -> /daemon attach 8080");
        log("[grey]examples[/]: view status -> /status");
        log("[grey]examples[/]: project commands -> mk script --name PlayerController");
        log("[grey]examples[/]: package management -> /upm list --outdated");
        log("[grey]examples[/]: build run -> /build run Android --dev");
        log("[grey]examples[/]: build logs -> /build logs");
        log("[grey]examples[/]: inspect object -> /inspect /Player");
        return Task.FromResult(true);
    }

    private static async Task<bool> HandleUpdateAsync(Action<string> log)
    {
        log($"[grey]update[/]: installed version -> [white]{Markup.Escape(CliVersion.SemVer)}[/]");

        if (!TryResolveCurrentPlatformUpdateSpec(out var platformSpec, out var platformError))
        {
            log($"[red]error[/]: {Markup.Escape(platformError)}");
            return true;
        }

        log($"[grey]update[/]: target platform -> [white]{Markup.Escape(platformSpec.Label)}[/]");
        var fetchReleaseResult = await TryFetchLatestGitHubReleaseAsync();
        if (!fetchReleaseResult.Ok || fetchReleaseResult.Release is null)
        {
            log($"[red]error[/]: update check failed ({Markup.Escape(fetchReleaseResult.Error)})");
            return true;
        }

        var release = fetchReleaseResult.Release;
        var releaseVersion = release.TagName.TrimStart('v');
        log($"[grey]update[/]: latest release -> [white]{Markup.Escape(release.TagName)}[/]");

        if (TryParseComparableSemVer(CliVersion.SemVer, out var installedVersion)
            && TryParseComparableSemVer(releaseVersion, out var latestVersion)
            && latestVersion <= installedVersion)
        {
            log("[green]update[/]: already on the latest release");
            return true;
        }

        var asset = release.Assets.FirstOrDefault(candidate =>
            candidate.Name.EndsWith(platformSpec.AssetSuffix, StringComparison.OrdinalIgnoreCase));
        if (asset is null)
        {
            log($"[red]error[/]: no release asset found for {Markup.Escape(platformSpec.Label)}");
            log("[grey]update[/]: available assets");
            foreach (var candidate in release.Assets)
            {
                log($"[grey]  -[/] {Markup.Escape(candidate.Name)}");
            }

            return true;
        }

        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            "unifocl-update",
            releaseVersion,
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var archivePath = Path.Combine(tempRoot, asset.Name);
        var extractDirectory = Path.Combine(tempRoot, "extracted");
        Directory.CreateDirectory(extractDirectory);

        log($"[grey]update[/]: downloading [white]{Markup.Escape(asset.Name)}[/]");
        var downloadResult = await DownloadReleaseAssetAsync(asset.DownloadUrl, archivePath);
        if (!downloadResult.Ok)
        {
            log($"[red]error[/]: failed to download release asset ({Markup.Escape(downloadResult.Error)})");
            return true;
        }

        var extractResult = await ExtractReleaseArchiveAsync(archivePath, extractDirectory, platformSpec.ArchiveType);
        if (!extractResult.Ok)
        {
            log($"[red]error[/]: failed to extract release asset ({Markup.Escape(extractResult.Error)})");
            return true;
        }

        var extractedExecutablePath = FindExtractedExecutablePath(extractDirectory, platformSpec.ExecutableName);
        if (string.IsNullOrWhiteSpace(extractedExecutablePath))
        {
            log("[red]error[/]: extracted archive did not include an executable payload");
            return true;
        }

        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath) || !File.Exists(processPath))
        {
            var stagedPath = StageDownloadedExecutableForManualInstall(
                extractedExecutablePath,
                releaseVersion,
                platformSpec.ExecutableName,
                Directory.GetCurrentDirectory());
            log("[yellow]update[/]: current executable path is unavailable (likely dotnet run/dev mode)");
            log($"[green]update[/]: downloaded latest binary -> [white]{Markup.Escape(stagedPath)}[/]");
            return true;
        }

        var processDirectory = Path.GetDirectoryName(processPath) ?? Directory.GetCurrentDirectory();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var stagedPath = StageDownloadedExecutableForManualInstall(
                extractedExecutablePath,
                releaseVersion,
                platformSpec.ExecutableName,
                processDirectory);
            log("[yellow]update[/]: Windows locks the running executable; staged update for next swap");
            log($"[green]update[/]: downloaded latest binary -> [white]{Markup.Escape(stagedPath)}[/]");
            log($"[grey]update[/]: replace [white]{Markup.Escape(processPath)}[/] with staged binary after quitting unifocl");
            return true;
        }

        try
        {
            File.Copy(extractedExecutablePath, processPath, overwrite: true);
            TryApplyUnixExecutableMode(processPath);
            log($"[green]update[/]: updated executable in place -> [white]{Markup.Escape(processPath)}[/]");
            log("[grey]update[/]: restart unifocl to use the new version");
            return true;
        }
        catch (Exception ex)
        {
            var stagedPath = StageDownloadedExecutableForManualInstall(
                extractedExecutablePath,
                releaseVersion,
                platformSpec.ExecutableName,
                processDirectory);
            log($"[yellow]update[/]: in-place replacement failed ({Markup.Escape(ex.Message)})");
            log($"[green]update[/]: downloaded latest binary -> [white]{Markup.Escape(stagedPath)}[/]");
            log($"[grey]update[/]: replace [white]{Markup.Escape(processPath)}[/] with staged binary after quitting unifocl");
            return true;
        }
    }

    private Task<bool> HandleInstallHookAsync(
        CliSessionState session,
        DaemonControlService daemonControlService,
        DaemonRuntime daemonRuntime,
        Action<string> log)
    {
        var targetPath = !string.IsNullOrWhiteSpace(session.CurrentProjectPath)
            ? session.CurrentProjectPath!
            : Directory.GetCurrentDirectory();
        if (!LooksLikeUnityProject(targetPath))
        {
            log("[red]error[/]: /install-hook requires an open Unity project or running from a Unity project root");
            return Task.FromResult(true);
        }

        log($"[grey]install-hook[/]: initializing bridge dependencies at [white]{Markup.Escape(targetPath)}[/]");
        return HandleInitAsync(
            $"/init \"{targetPath}\"",
            new CommandSpec("/init", "Initialize bridge", "/init"),
            session,
            daemonControlService,
            daemonRuntime,
            log);
    }

    private static async Task<bool> HandleDoctorAsync(
        CliSessionState session,
        DaemonRuntime daemonRuntime,
        Action<string> log)
    {
        var dotnet = await RunProcessAsync("dotnet", "--version", Directory.GetCurrentDirectory(), TimeSpan.FromSeconds(8));
        if (dotnet.ExitCode == 0)
        {
            var version = dotnet.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "unknown";
            log($"[green]doctor[/]: dotnet ok ({Markup.Escape(version)})");
        }
        else
        {
            log("[red]doctor[/]: dotnet not available on PATH");
        }

        var git = await RunProcessAsync("git", "--version", Directory.GetCurrentDirectory(), TimeSpan.FromSeconds(8));
        if (git.ExitCode == 0)
        {
            var version = git.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "unknown";
            log($"[green]doctor[/]: git ok ({Markup.Escape(version)})");
        }
        else
        {
            log("[red]doctor[/]: git not available on PATH");
        }

        var editors = UnityEditorPathService.DetectInstalledEditors(out var hubRoot);
        if (editors.Count == 0)
        {
            log("[yellow]doctor[/]: Unity editors were not detected");
        }
        else
        {
            var hubLabel = string.IsNullOrWhiteSpace(hubRoot) ? "unknown" : hubRoot;
            log($"[green]doctor[/]: Unity editor detection ok ({editors.Count} found; hub={Markup.Escape(hubLabel)})");
        }

        if (!string.IsNullOrWhiteSpace(session.CurrentProjectPath))
        {
            var projectValid = LooksLikeUnityProject(session.CurrentProjectPath);
            log(projectValid
                ? $"[green]doctor[/]: project layout ok ({Markup.Escape(session.CurrentProjectPath)})"
                : $"[red]doctor[/]: project layout invalid ({Markup.Escape(session.CurrentProjectPath)})");
        }

        var daemonCount = daemonRuntime.GetAll().Count();
        log($"[grey]doctor[/]: active daemon entries={daemonCount}");
        return true;
    }

    private static Task<bool> HandleScanAsync(
        string input,
        CommandSpec matched,
        Action<string> log)
    {
        var args = ParseCommandArgs(input, matched.Trigger);
        if (!TryParseScanArgs(args, out var rootPathRaw, out var depth, out var parseError))
        {
            log($"[red]error[/]: {Markup.Escape(parseError)}");
            return Task.FromResult(true);
        }

        var rootPath = ResolveAbsolutePath(rootPathRaw, Directory.GetCurrentDirectory());
        if (!Directory.Exists(rootPath))
        {
            log($"[red]error[/]: scan root not found -> {Markup.Escape(rootPath)}");
            return Task.FromResult(true);
        }

        var matches = ScanUnityProjects(rootPath, depth);
        log($"[grey]scan[/]: root={Markup.Escape(rootPath)} depth={depth} matches={matches.Count}");
        if (matches.Count == 0)
        {
            return Task.FromResult(true);
        }

        for (var i = 0; i < matches.Count; i++)
        {
            log($"[grey]scan[/]: [white]{i + 1}[/]. {Markup.Escape(matches[i])}");
        }

        return Task.FromResult(true);
    }

    private static Task<bool> HandleInfoAsync(
        string input,
        CommandSpec matched,
        CliSessionState session,
        Action<string> log)
    {
        var args = ParseCommandArgs(input, matched.Trigger);
        var targetPath = args.Count switch
        {
            0 when !string.IsNullOrWhiteSpace(session.CurrentProjectPath) => session.CurrentProjectPath!,
            0 => Directory.GetCurrentDirectory(),
            1 => ResolveAbsolutePath(args[0], Directory.GetCurrentDirectory()),
            _ => string.Empty
        };
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            log("[red]error[/]: usage /info <path>");
            return Task.FromResult(true);
        }

        if (!Directory.Exists(targetPath))
        {
            log($"[red]error[/]: path not found -> {Markup.Escape(targetPath)}");
            return Task.FromResult(true);
        }

        var unityProject = LooksLikeUnityProject(targetPath);
        log($"[grey]info[/]: path={Markup.Escape(targetPath)}");
        log($"[grey]info[/]: unity-project={(unityProject ? "yes" : "no")}");
        if (!unityProject)
        {
            return Task.FromResult(true);
        }

        var versionPath = Path.Combine(targetPath, "ProjectSettings", "ProjectVersion.txt");
        var editorVersion = TryReadEditorVersion(versionPath);
        if (!string.IsNullOrWhiteSpace(editorVersion))
        {
            log($"[grey]info[/]: unity-version={Markup.Escape(editorVersion)}");
        }
        else
        {
            log("[grey]info[/]: unity-version=unknown");
        }

        if (UnityEditorPathService.TryResolveEditorForProject(targetPath, out var resolvedEditorPath, out var resolvedEditorVersion, out _))
        {
            log($"[grey]info[/]: unity-editor-match={Markup.Escape(resolvedEditorVersion)}");
            log($"[grey]info[/]: unity-editor-path={Markup.Escape(resolvedEditorPath)}");
        }
        else
        {
            log("[yellow]info[/]: unity-editor-match=not found");
        }

        var daemonPort = DaemonControlService.ResolveProjectDaemonPort(targetPath);
        log($"[grey]info[/]: default-daemon-port={daemonPort}");

        if (TryGetProjectBridgeProtocol(targetPath, out var protocol))
        {
            log($"[grey]info[/]: bridge-protocol={Markup.Escape(protocol ?? "-")}");
        }
        else
        {
            log("[grey]info[/]: bridge-protocol=not configured");
        }

        var manifestPath = Path.Combine(targetPath, "Packages", "manifest.json");
        var dependencyCount = TryCountManifestDependencies(manifestPath);
        if (dependencyCount >= 0)
        {
            log($"[grey]info[/]: package-dependencies={dependencyCount}");
        }

        return Task.FromResult(true);
    }

    private static Task<bool> HandleLogsAsync(
        string input,
        CommandSpec matched,
        CliSessionState session,
        DaemonRuntime daemonRuntime,
        Action<string> log)
    {
        var args = ParseCommandArgs(input, matched.Trigger);
        var topic = args.FirstOrDefault()?.Trim().ToLowerInvariant() ?? "daemon";
        var follow = args.Any(arg => arg.Equals("-f", StringComparison.OrdinalIgnoreCase)
                                     || arg.Equals("--follow", StringComparison.OrdinalIgnoreCase));
        if (topic.Equals("daemon", StringComparison.OrdinalIgnoreCase))
        {
            var daemons = daemonRuntime.GetAll()
                .OrderBy(instance => instance.Port)
                .ToList();
            if (daemons.Count == 0)
            {
                log("[grey]logs[/]: no active daemon runtime entries");
                return Task.FromResult(true);
            }

            log($"[grey]logs[/]: daemon entries ({daemons.Count})");
            foreach (var daemon in daemons)
            {
                var heartbeatAge = DateTime.UtcNow - daemon.LastHeartbeatUtc;
                log($"[grey]logs[/]: port={daemon.Port} pid={daemon.Pid} heartbeat-age={heartbeatAge.TotalSeconds:0}s project={Markup.Escape(daemon.ProjectPath ?? "-")}");
            }

            if (follow)
            {
                log("[yellow]logs[/]: follow mode is not implemented for daemon process output in this runtime");
            }

            return Task.FromResult(true);
        }

        if (!topic.Equals("unity", StringComparison.OrdinalIgnoreCase))
        {
            log("[red]error[/]: usage /logs [daemon|unity] [-f]");
            return Task.FromResult(true);
        }

        if (session.UnityLogPane.Count == 0)
        {
            log("[grey]logs[/]: unity log pane is empty");
            return Task.FromResult(true);
        }

        var lines = session.UnityLogPane.TakeLast(60).ToList();
        log($"[grey]logs[/]: unity log pane tail ({lines.Count} lines)");
        foreach (var line in lines)
        {
            log(Markup.Escape(line));
        }

        if (follow)
        {
            log("[yellow]logs[/]: follow mode is not implemented for cached Unity log pane");
        }

        return Task.FromResult(true);
    }

    private static bool TryParseScanArgs(
        IReadOnlyList<string> args,
        out string rootPath,
        out int depth,
        out string error)
    {
        rootPath = ".";
        depth = 3;
        error = string.Empty;
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg.Equals("--root", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Count)
                {
                    error = "usage /scan [--root <dir>] [--depth <n>]";
                    return false;
                }

                rootPath = args[++i];
                continue;
            }

            if (arg.Equals("--depth", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Count || !int.TryParse(args[++i], out depth) || depth < 0 || depth > 12)
                {
                    error = "--depth must be an integer between 0 and 12";
                    return false;
                }

                continue;
            }

            error = $"unrecognized option {arg}; usage /scan [--root <dir>] [--depth <n>]";
            return false;
        }

        return true;
    }

    private static List<string> ScanUnityProjects(string rootPath, int maxDepth)
    {
        var matches = new List<string>();
        var pending = new Queue<(string Path, int Depth)>();
        pending.Enqueue((rootPath, 0));
        while (pending.Count > 0)
        {
            var (currentPath, depth) = pending.Dequeue();
            if (LooksLikeUnityProject(currentPath))
            {
                matches.Add(currentPath);
            }

            if (depth >= maxDepth)
            {
                continue;
            }

            string[] children;
            try
            {
                children = Directory.GetDirectories(currentPath);
            }
            catch
            {
                continue;
            }

            foreach (var child in children)
            {
                pending.Enqueue((child, depth + 1));
            }
        }

        return matches
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? TryReadEditorVersion(string projectVersionPath)
    {
        if (!File.Exists(projectVersionPath))
        {
            return null;
        }

        foreach (var line in File.ReadLines(projectVersionPath))
        {
            const string prefix = "m_EditorVersion:";
            if (!line.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            return line[prefix.Length..].Trim();
        }

        return null;
    }

    private static int TryCountManifestDependencies(string manifestPath)
    {
        if (!File.Exists(manifestPath))
        {
            return -1;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("dependencies", out var deps)
                || deps.ValueKind != JsonValueKind.Object)
            {
                return -1;
            }

            return deps.EnumerateObject().Count();
        }
        catch
        {
            return -1;
        }
    }

    private static void LogHelpSection(string title, IReadOnlyList<CommandSpec> commands, Action<string> log)
    {
        log($"[grey]help[/]: {title}");
        var deduped = commands
            .GroupBy(command => command.Signature, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(command => command.Signature, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var command in deduped)
        {
            var signature = command.Signature.Replace('[', '(').Replace(']', ')');
            log($"[grey]help[/]: [white]{Markup.Escape(signature)}[/] - {Markup.Escape(command.Description)}");
        }
    }

    private Task<bool> HandleConfigAsync(
        string input,
        CommandSpec matched,
        Action<string> log)
    {
        var args = ParseCommandArgs(input, matched.Trigger);
        if (args.Count == 0)
        {
            LogConfigUsage(log);
            return Task.FromResult(true);
        }

        var action = args[0].Trim().ToLowerInvariant();
        return action switch
        {
            "list" => HandleConfigList(log),
            "get" => HandleConfigGet(args.Skip(1).ToList(), log),
            "set" => HandleConfigSet(args.Skip(1).ToList(), log),
            "reset" => HandleConfigReset(args.Skip(1).ToList(), log),
            _ => Task.FromResult(LogConfigUsage(log))
        };
    }

    private static Task<bool> HandleConfigList(Action<string> log)
    {
        var loadResult = TryLoadCliConfig(out var config, out var error);
        if (!loadResult)
        {
            log($"[red]error[/]: {Markup.Escape(error ?? "failed to read config")}");
            return Task.FromResult(true);
        }

        var source = string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("UNIFOCL_THEME"))
            ? "file/default"
            : "env (UNIFOCL_THEME)";
        var theme = ResolveEffectiveTheme(config);
        var staleDays = ResolveRecentPruneStaleDays(config);
        log("[grey]config[/]: available settings");
        log($"[grey]config[/]: [white]theme[/] = [white]{theme}[/] [dim](dark|light, source: {source})[/]");
        log($"[grey]config[/]: [white]recent.staleDays[/] = [white]{staleDays}[/] [dim](days, default: {DefaultRecentPruneStaleDays})[/]");
        return Task.FromResult(true);
    }

    private static Task<bool> HandleConfigGet(IReadOnlyList<string> args, Action<string> log)
    {
        if (args.Count != 1)
        {
            log("[red]error[/]: usage /config get <theme|recent.staleDays>");
            return Task.FromResult(true);
        }

        var isThemeKey = IsThemeKey(args[0]);
        var isRecentStaleDaysKey = IsRecentStaleDaysKey(args[0]);
        if (!isThemeKey && !isRecentStaleDaysKey)
        {
            log("[red]error[/]: supported keys are 'theme' and 'recent.staleDays'");
            return Task.FromResult(true);
        }

        var loadResult = TryLoadCliConfig(out var config, out var error);
        if (!loadResult)
        {
            log($"[red]error[/]: {Markup.Escape(error ?? "failed to read config")}");
            return Task.FromResult(true);
        }

        if (isThemeKey)
        {
            var theme = ResolveEffectiveTheme(config);
            log($"[grey]config[/]: [white]theme[/] = [white]{theme}[/]");
            return Task.FromResult(true);
        }

        var staleDays = ResolveRecentPruneStaleDays(config);
        log($"[grey]config[/]: [white]recent.staleDays[/] = [white]{staleDays}[/]");
        return Task.FromResult(true);
    }

    private static Task<bool> HandleConfigSet(IReadOnlyList<string> args, Action<string> log)
    {
        if (args.Count != 2)
        {
            log("[red]error[/]: usage /config set <theme|recent.staleDays> <value>");
            return Task.FromResult(true);
        }

        var key = args[0];
        if (!TryLoadCliConfig(out var config, out var loadError))
        {
            log($"[red]error[/]: {Markup.Escape(loadError ?? "failed to read config")}");
            return Task.FromResult(true);
        }

        if (IsThemeKey(key))
        {
            var requestedTheme = args[1].Trim().ToLowerInvariant();
            if (requestedTheme is not ("dark" or "light"))
            {
                log("[red]error[/]: theme must be 'dark' or 'light'");
                return Task.FromResult(true);
            }

            config.Theme = requestedTheme;
            if (!TrySaveCliConfig(config, out var saveThemeError))
            {
                log($"[red]error[/]: {Markup.Escape(saveThemeError ?? "failed to write config")}");
                return Task.FromResult(true);
            }

            CliTheme.TrySetTheme(requestedTheme);
            log($"[green]config[/]: theme set to [white]{requestedTheme}[/]");
            return Task.FromResult(true);
        }

        if (IsRecentStaleDaysKey(key))
        {
            if (!TryParseRecentPruneStaleDays(args[1], out var staleDays))
            {
                log("[red]error[/]: recent.staleDays must be a positive integer");
                return Task.FromResult(true);
            }

            config.RecentPruneStaleDays = staleDays;
            if (!TrySaveCliConfig(config, out var saveStaleDaysError))
            {
                log($"[red]error[/]: {Markup.Escape(saveStaleDaysError ?? "failed to write config")}");
                return Task.FromResult(true);
            }

            log($"[green]config[/]: recent.staleDays set to [white]{staleDays}[/] days");
            return Task.FromResult(true);
        }

        log("[red]error[/]: supported keys are 'theme' and 'recent.staleDays'");
        return Task.FromResult(true);
    }

    private static Task<bool> HandleConfigReset(IReadOnlyList<string> args, Action<string> log)
    {
        if (args.Count > 1)
        {
            log("[red]error[/]: usage /config reset <theme|recent.staleDays?>");
            return Task.FromResult(true);
        }

        var resetTheme = args.Count == 0 || IsThemeKey(args[0]);
        var resetRecentStaleDays = args.Count == 0 || IsRecentStaleDaysKey(args[0]);
        if (!resetTheme && !resetRecentStaleDays)
        {
            log("[red]error[/]: supported keys are 'theme' and 'recent.staleDays'");
            return Task.FromResult(true);
        }

        if (!TryLoadCliConfig(out var config, out var loadError))
        {
            log($"[red]error[/]: {Markup.Escape(loadError ?? "failed to read config")}");
            return Task.FromResult(true);
        }

        if (resetTheme)
        {
            config.Theme = null;
        }

        if (resetRecentStaleDays)
        {
            config.RecentPruneStaleDays = null;
        }

        if (!TrySaveCliConfig(config, out var saveError))
        {
            log($"[red]error[/]: {Markup.Escape(saveError ?? "failed to write config")}");
            return Task.FromResult(true);
        }

        var effective = ResolveEffectiveTheme(config);
        CliTheme.TrySetTheme(effective);

        if (resetTheme && resetRecentStaleDays)
        {
            log(
                $"[green]config[/]: reset to defaults " +
                $"([white]theme[/]={effective}, [white]recent.staleDays[/]={DefaultRecentPruneStaleDays})");
            return Task.FromResult(true);
        }

        if (resetTheme)
        {
            log($"[green]config[/]: theme reset to default [white]{effective}[/]");
            return Task.FromResult(true);
        }

        log($"[green]config[/]: recent.staleDays reset to default [white]{DefaultRecentPruneStaleDays}[/]");
        return Task.FromResult(true);
    }

    private static bool LogConfigUsage(Action<string> log)
    {
        log("[red]error[/]: usage /config <get|set|list|reset> <theme|recent.staleDays?> <value?>");
        return true;
    }

    private async Task<bool> HandleCloseAsync(
        CliSessionState session,
        DaemonControlService daemonControlService,
        DaemonRuntime daemonRuntime,
        Action<string> log)
    {
        if (DaemonControlService.GetPort(session) is int attachedBuildPort)
        {
            var status = await _daemonClient.GetBuildStatusAsync(attachedBuildPort);
            if (status?.Running == true)
            {
                var label = string.IsNullOrWhiteSpace(status.Kind) ? "build" : status.Kind;
                session.ResetToBoot();
                log($"[yellow]close[/]: detached only; active {Markup.Escape(label)} build continues on daemon port {attachedBuildPort}");
                return true;
            }
        }

        var candidatePorts = new HashSet<int>();
        if (DaemonControlService.GetPort(session) is int attachedPort)
        {
            candidatePorts.Add(attachedPort);
        }

        if (!string.IsNullOrWhiteSpace(session.CurrentProjectPath))
        {
            candidatePorts.Add(DaemonControlService.ResolveProjectDaemonPort(session.CurrentProjectPath));
        }

        var hadDaemonTarget = candidatePorts.Count > 0;
        var stopFailed = false;
        foreach (var port in candidatePorts)
        {
            if (!await daemonControlService.StopDaemonByPortAsync(port, daemonRuntime, session, log))
            {
                stopFailed = true;
            }
        }

        daemonControlService.StopUnityLicensingClients(log);

        session.ResetToBoot();
        if (stopFailed)
        {
            log("[yellow]close[/]: session detached, but one or more daemons could not be stopped");
        }
        else if (hadDaemonTarget)
        {
            log("[green]close[/]: session detached and daemon stopped");
        }
        else
        {
            log("[green]close[/]: session detached");
        }

        return true;
    }

    private async Task<bool> TryOpenProjectAsync(
        string projectPath,
        CliSessionState session,
        DaemonControlService daemonControlService,
        DaemonRuntime daemonRuntime,
        EditorDependencyInitializerService editorDependencyInitializerService,
        bool promptForInitialization,
        bool ensureMcpHostDependencyCheck,
        bool allowUnsafe,
        TimeSpan daemonStartupTimeout,
        Action<string> log)
    {
        log($"[grey]open[/]: step 1/5 resolve project path -> [white]{Markup.Escape(projectPath)}[/]");

        if (!Directory.Exists(projectPath))
        {
            log("[red]error[/]: project directory does not exist");
            return false;
        }

        if (!LooksLikeUnityProject(projectPath))
        {
            log("[red]error[/]: path is not a Unity project (missing Assets/ or ProjectSettings/)");
            return false;
        }

        // Acquire cross-process file lock to prevent concurrent opens on the same project.
        // Agents working concurrently should clone the project (with Library/) to their own worktree.
        var lockDir = Path.Combine(projectPath, ".unifocl");
        Directory.CreateDirectory(lockDir);
        var lockPath = Path.Combine(lockDir, "open.lock");
        FileStream? projectLock = null;
        try
        {
            projectLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            log($"[red]error[/]: another process is already opening this project");
            log($"[yellow]hint[/]: concurrent agents must clone the project to an isolated worktree before opening");
            log($"[yellow]hint[/]: use [white]agent-worktree.sh provision --seed-library[/] to clone with pre-compiled Library cache");
            return false;
        }

        try
        {
            return await TryOpenProjectLockedAsync(
                projectPath, session, daemonControlService, daemonRuntime,
                editorDependencyInitializerService, promptForInitialization,
                ensureMcpHostDependencyCheck, allowUnsafe, daemonStartupTimeout, log);
        }
        finally
        {
            projectLock.Dispose();
            TryDeleteLockFile(lockPath);
        }
    }

    private async Task<bool> TryOpenProjectLockedAsync(
        string projectPath,
        CliSessionState session,
        DaemonControlService daemonControlService,
        DaemonRuntime daemonRuntime,
        EditorDependencyInitializerService editorDependencyInitializerService,
        bool promptForInitialization,
        bool ensureMcpHostDependencyCheck,
        bool allowUnsafe,
        TimeSpan daemonStartupTimeout,
        Action<string> log)
    {
        var hasProtocolMismatch = TryGetProjectBridgeProtocol(projectPath, out var configuredProtocol)
                                  && !string.Equals(configuredProtocol, CliVersion.Protocol, StringComparison.Ordinal);

        log("[grey]open[/]: step 2/5 validate Unity project layout");
        var bridgeResult = EnsureProjectLocalConfig(projectPath);
        if (!bridgeResult.Ok)
        {
            log($"[red]error[/]: {Markup.Escape(bridgeResult.Error)}");
            return false;
        }

        var packageFixResult = await EnsureRequiredUnityPackageReferencesAsync(projectPath);
        if (!packageFixResult.Ok)
        {
            log($"[red]error[/]: {Markup.Escape(packageFixResult.Error)}");
            return false;
        }

        if (promptForInitialization)
        {
            if (hasProtocolMismatch)
            {
                log($"[yellow]init[/]: bridge protocol mismatch detected (project: [white]{Markup.Escape(configuredProtocol!)}[/], cli: [white]{Markup.Escape(CliVersion.Protocol)}[/]); reinitializing editor dependencies");
                var initResult = editorDependencyInitializerService.InitializeProject(projectPath, log);
                if (!initResult.Ok)
                {
                    log($"[red]error[/]: {Markup.Escape(initResult.Error)}");
                    return false;
                }
            }

            var needsInitialization = editorDependencyInitializerService.NeedsInitialization(projectPath, out var initReason);
            if (needsInitialization)
            {
                log($"[yellow]init[/]: editor bridge dependency is missing or invalid ({Markup.Escape(initReason)}).");
            }
            else
            {
                log("[grey]init[/]: editor bridge dependency already installed");
            }

            // Always refresh embedded package on open to keep project-side bridge in sync with current CLI payload.
            log("[grey]init[/]: syncing embedded editor bridge package");
            var syncResult = editorDependencyInitializerService.InitializeProject(projectPath, log);
            if (!syncResult.Ok)
            {
                log($"[red]error[/]: {Markup.Escape(syncResult.Error)}");
                return false;
            }
        }

        if (!UnityEditorPathService.TryResolveEditorForProject(projectPath, out var resolvedEditorPath, out var resolvedEditorVersion, out var editorResolveError))
        {
            log($"[red]error[/]: {Markup.Escape(editorResolveError ?? "failed to resolve Unity editor for project")}");
            return false;
        }

        if (!UnityEditorPathService.TrySetDefaultEditorPath(resolvedEditorPath, out var defaultEditorSaveError))
        {
            log($"[yellow]config[/]: unable to persist default editor path ({Markup.Escape(defaultEditorSaveError ?? "unknown error")})");
        }

        Environment.SetEnvironmentVariable("UNITY_PATH", resolvedEditorPath);
        log($"[grey]open[/]: step 3/5 resolved Unity editor [white]{Markup.Escape(resolvedEditorVersion)}[/] -> [white]{Markup.Escape(resolvedEditorPath)}[/]");

        log("[grey]open[/]: step 4/5 ensure managed daemon bridge");
        var daemonPort = DaemonControlService.ResolveProjectDaemonPort(projectPath);
        if (allowUnsafe)
        {
            log("[yellow]open[/]: --allow-unsafe enabled (using -noUpm and -ignoreCompileErrors for faster Host mode boot)");
        }

        var started = await EnsureProjectDaemonWithCompileRecoveryAsync(
            daemonControlService,
            projectPath,
            daemonRuntime,
            session,
            log,
            requireBridgeMode: true,
            // Prefer attaching to live editor Bridge mode when available; Host mode remains fallback.
            preferHostMode: false,
            allowUnsafe: allowUnsafe,
            daemonStartupTimeout: daemonStartupTimeout);
        if (!started)
        {
            HandleDaemonStartupFailure(projectPath, session, daemonControlService, log);
            log("[red]open[/]: daemon is not stable; open aborted before entering project UI");
            return false;
        }

        var stableReservation = await daemonControlService.HasStableProjectDaemonAsync(projectPath, daemonRuntime, session);
        if (!stableReservation)
        {
            DaemonControlService.ClearAttachedPort(session);
            log("[red]daemon[/]: daemon reservation check failed (missing attachment or project endpoint responsiveness)");
            log("[red]open[/]: open aborted before entering project UI");
            return false;
        }

        var hierarchyReady = await WaitForHierarchySnapshotReadyAsync(DaemonControlService.GetPort(session)!.Value, log);
        if (!hierarchyReady)
        {
            DaemonControlService.ClearAttachedPort(session);
            log("[red]daemon[/]: hierarchy snapshot endpoint is not ready after /open");
            log("[red]open[/]: open aborted (hierarchy mode would be unavailable)");
            return false;
        }

        SaveDaemonSession(projectPath, new DaemonSessionInfo(daemonPort, DateTimeOffset.UtcNow, true));
        log($"[grey]daemon[/]: managed daemon ready on [white]127.0.0.1:{daemonPort}[/]");
        session.SafeModeEnabled = false;
        session.LastCompileError = null;

        session.CurrentProjectPath = projectPath;
        session.Mode = CliMode.Project;
        session.ContextMode = CliContextMode.Project;
        session.LastOpenedUtc = DateTimeOffset.UtcNow;
        Environment.SetEnvironmentVariable("UNIFOCL_UNITY_PROJECT_PATH", projectPath);
        if (!TrySaveLastUnityProjectPath(projectPath, out var configError))
        {
            log($"[yellow]config[/]: unable to persist unity project path ({Markup.Escape(configError ?? "unknown error")})");
        }
        if (!_recentProjectHistoryService.TryRecordProjectOpen(projectPath, session.LastOpenedUtc.Value, out var historyError))
        {
            log($"[yellow]recent[/]: unable to update history ({Markup.Escape(historyError ?? "unknown error")})");
        }

        log("[grey]open[/]: step 5/5 load project context");
        if (!await EnsureStartupSceneLoadedAsync(projectPath, session, log))
        {
            DaemonControlService.ClearAttachedPort(session);
            log("[red]open[/]: open aborted (no startup scene could be loaded)");
            return false;
        }

        _projectViewService.OpenInitialView(session);
        _ = _projectViewService.SyncMkTypeCacheAsync(session);
        _ = _projectViewService.SyncComponentTypeCacheAsync(session);
        return true;
    }

    private async Task<bool> EnsureStartupSceneLoadedAsync(
        string projectPath,
        CliSessionState session,
        Action<string> log)
    {
        if (DaemonControlService.GetPort(session) is not int scenePort)
        {
            log("[red]open[/]: no daemon port attached for startup scene load");
            return false;
        }

        var shouldCreateSampleScene = false;
        var startupScenePath = ResolvePreferredStartupScenePath(projectPath, out shouldCreateSampleScene);
        if (shouldCreateSampleScene)
        {
            log($"[grey]open[/]: creating fallback startup scene [white]{Markup.Escape(DefaultSampleSceneAssetPath)}[/]");
            var createScenePayload = JsonSerializer.Serialize(new
            {
                type = "Scene",
                count = 1,
                name = "SampleScene"
            });
            var createSceneResponse = await _daemonClient.ExecuteProjectCommandAsync(
                scenePort,
                new ProjectCommandRequestDto("mk-asset", "Assets", null, createScenePayload));
            if (!createSceneResponse.Ok)
            {
                log($"[red]open[/]: failed to create fallback scene ({Markup.Escape(createSceneResponse.Message ?? "unknown error")})");
                return false;
            }

            startupScenePath = DefaultSampleSceneAssetPath;
        }

        if (string.IsNullOrWhiteSpace(startupScenePath))
        {
            log("[red]open[/]: no scene assets available");
            return false;
        }

        var loadResponse = await _daemonClient.ExecuteProjectCommandAsync(
            scenePort,
            new ProjectCommandRequestDto("load-asset", startupScenePath, null, null));
        if (!loadResponse.Ok)
        {
            log($"[red]open[/]: failed to load startup scene [white]{Markup.Escape(startupScenePath)}[/] ({Markup.Escape(loadResponse.Message ?? "unknown error")})");
            return false;
        }

        log($"[grey]open[/]: startup scene loaded -> [white]{Markup.Escape(startupScenePath)}[/]");
        return true;
    }

    private static string ResolvePreferredStartupScenePath(string projectPath, out bool shouldCreateSampleScene)
    {
        shouldCreateSampleScene = false;

        var scenes = EnumerateProjectSceneAssets(projectPath);
        var sampleSceneExists = scenes.Contains(DefaultSampleSceneAssetPath, StringComparer.OrdinalIgnoreCase);
        var hasPersistedMarker = TryReadLastOpenedSceneMarker(projectPath, out var persistedScenePath);
        if (hasPersistedMarker
            && !string.IsNullOrWhiteSpace(persistedScenePath)
            && scenes.Contains(persistedScenePath, StringComparer.OrdinalIgnoreCase))
        {
            return persistedScenePath;
        }

        if (sampleSceneExists)
        {
            return DefaultSampleSceneAssetPath;
        }

        if (hasPersistedMarker)
        {
            shouldCreateSampleScene = true;
            return DefaultSampleSceneAssetPath;
        }

        if (scenes.Count > 0)
        {
            return scenes[0];
        }

        shouldCreateSampleScene = true;
        return DefaultSampleSceneAssetPath;
    }

    private static List<string> EnumerateProjectSceneAssets(string projectPath)
    {
        var assetsRoot = Path.Combine(projectPath, "Assets");
        if (!Directory.Exists(assetsRoot))
        {
            return [];
        }

        return Directory
            .EnumerateFiles(assetsRoot, "*.unity", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(projectPath, path).Replace('\\', '/'))
            .Where(path => path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool TryReadLastOpenedSceneMarker(string projectPath, out string sceneAssetPath)
    {
        sceneAssetPath = string.Empty;
        var markerPath = Path.Combine(projectPath, LastOpenedSceneMarkerRelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(markerPath))
        {
            return false;
        }

        var raw = (File.ReadAllText(markerPath) ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        var normalized = raw.Replace('\\', '/');
        if (!normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
            || !normalized.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        sceneAssetPath = normalized;
        return true;
    }

    private static async Task<bool> WaitForHierarchySnapshotReadyAsync(int port, Action<string> log)
    {
        var client = new HierarchyDaemonClient();
        for (var attempt = 1; attempt <= 6; attempt++)
        {
            var snapshot = await client.GetSnapshotAsync(port);
            if (snapshot is not null)
            {
                return true;
            }

            if (attempt < 6)
            {
                await Task.Delay(250);
            }
        }

        log($"[yellow]daemon[/]: hierarchy snapshot probe failed on port {port}");
        return false;
    }

    private static bool HandleDaemonStartupFailure(
        string projectPath,
        CliSessionState session,
        DaemonControlService daemonControlService,
        Action<string> log)
    {
        if (!daemonControlService.TryGetLastStartupFailure(out var failure) || failure is null)
        {
            log("[red]daemon[/]: failed to start or attach Host mode daemon");
            return false;
        }

        var allText = string.Join("\n", new[] { failure.Summary }.Concat(failure.Lines));
        if (allText.Contains("another Unity instance", StringComparison.OrdinalIgnoreCase)
            || allText.Contains("Multiple Unity instances cannot open", StringComparison.OrdinalIgnoreCase))
        {
            log($"[red]daemon[/]: startup failed — another Unity instance is already running for this project");
            log("[yellow]hint[/]: concurrent agents must clone the project to an isolated worktree before opening");
            log("[yellow]hint[/]: use [white]agent-worktree.sh provision --seed-library[/] to clone with pre-compiled Library cache");
            return false;
        }

        if (!failure.IsCompileError)
        {
            log($"[red]daemon[/]: startup failed ({Markup.Escape(failure.Summary)})");
            return false;
        }

        session.LastCompileError = new CompileErrorState(
            projectPath,
            DateTimeOffset.UtcNow,
            failure.Summary,
            failure.Lines.ToList());
        log("[yellow]daemon[/]: Unity script compilation errors detected during daemon startup");
        if (!string.IsNullOrWhiteSpace(failure.Summary))
        {
            log($"[yellow]daemon[/]: {Markup.Escape(failure.Summary)}");
        }
        DaemonControlService.ClearAttachedPort(session);
        log("[yellow]open[/]: compile errors blocked daemon startup; fix errors and retry /open");
        return false;
    }

    private static void TryDeleteLockFile(string lockPath)
    {
        try { File.Delete(lockPath); } catch { /* best-effort cleanup */ }
    }

    private static void SaveDaemonSession(string projectPath, DaemonSessionInfo session)
    {
        var daemonDir = Path.Combine(projectPath, ".unifocl");
        Directory.CreateDirectory(daemonDir);
        var daemonPath = Path.Combine(daemonDir, "daemon.session.json");
        File.WriteAllText(
            daemonPath,
            JsonSerializer.Serialize(session, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
    }

    private static HttpClient CreateGitHubReleasesHttpClient()
    {
        var httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("unifocl-cli-updater");
        httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return httpClient;
    }

    private static bool TryResolveCurrentPlatformUpdateSpec(out PlatformUpdateSpec spec, out string error)
    {
        var architecture = RuntimeInformation.ProcessArchitecture;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && architecture == Architecture.Arm64)
        {
            spec = new PlatformUpdateSpec(
                "macOS arm64",
                "-macos-arm64.tar.gz",
                "unifocl",
                ReleaseArchiveType.TarGz);
            error = string.Empty;
            return true;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && architecture == Architecture.X64)
        {
            spec = new PlatformUpdateSpec(
                "Windows x64",
                "-win-x64.zip",
                "unifocl.exe",
                ReleaseArchiveType.Zip);
            error = string.Empty;
            return true;
        }

        spec = new PlatformUpdateSpec(string.Empty, string.Empty, string.Empty, ReleaseArchiveType.Zip);
        error = $"unsupported update target: os={RuntimeInformation.OSDescription}, arch={architecture}";
        return false;
    }

    private static async Task<ReleaseFetchResult> TryFetchLatestGitHubReleaseAsync()
    {
        try
        {
            var endpoint = $"https://api.github.com/repos/{GitHubReleaseOwner}/{GitHubReleaseRepository}/releases/latest";
            using var response = await GitHubReleasesHttpClient.GetAsync(endpoint);
            if (!response.IsSuccessStatusCode)
            {
                return ReleaseFetchResult.Fail($"{(int)response.StatusCode} {response.ReasonPhrase}");
            }

            var payload = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return ReleaseFetchResult.Fail("invalid release payload");
            }

            if (!root.TryGetProperty("tag_name", out var tagNameElement)
                || tagNameElement.ValueKind != JsonValueKind.String)
            {
                return ReleaseFetchResult.Fail("release payload missing tag_name");
            }

            var tagName = tagNameElement.GetString();
            if (string.IsNullOrWhiteSpace(tagName))
            {
                return ReleaseFetchResult.Fail("release tag_name is empty");
            }

            var assets = new List<ReleaseAsset>();
            if (root.TryGetProperty("assets", out var assetsElement)
                && assetsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var assetElement in assetsElement.EnumerateArray())
                {
                    if (assetElement.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    if (!assetElement.TryGetProperty("name", out var nameElement)
                        || nameElement.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    if (!assetElement.TryGetProperty("browser_download_url", out var urlElement)
                        || urlElement.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    var name = nameElement.GetString();
                    var url = urlElement.GetString();
                    if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(url))
                    {
                        assets.Add(new ReleaseAsset(name, url));
                    }
                }
            }

            return ReleaseFetchResult.Success(new ReleaseInfo(tagName!, assets));
        }
        catch (Exception ex)
        {
            return ReleaseFetchResult.Fail(ex.Message);
        }
    }

    private static bool TryParseComparableSemVer(string value, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim();
        if (normalized.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[1..];
        }

        var match = Regex.Match(normalized, @"^(?<core>\d+\.\d+\.\d+)", RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return false;
        }

        if (!Version.TryParse(match.Groups["core"].Value, out var parsed) || parsed is null)
        {
            return false;
        }

        version = parsed;
        return true;
    }

    private static async Task<OperationResult> DownloadReleaseAssetAsync(string downloadUrl, string destinationPath)
    {
        try
        {
            using var timeoutCts = new CancellationTokenSource(CliUpdateDownloadTimeout);
            using var response = await GitHubReleasesHttpClient.GetAsync(
                downloadUrl,
                HttpCompletionOption.ResponseHeadersRead,
                timeoutCts.Token);
            response.EnsureSuccessStatusCode();
            await using var sourceStream = await response.Content.ReadAsStreamAsync(timeoutCts.Token);
            await using var destinationStream = File.Create(destinationPath);
            await sourceStream.CopyToAsync(destinationStream, timeoutCts.Token);
            return OperationResult.Success();
        }
        catch (Exception ex)
        {
            return OperationResult.Fail(ex.Message);
        }
    }

    private static async Task<OperationResult> ExtractReleaseArchiveAsync(
        string archivePath,
        string extractDirectory,
        ReleaseArchiveType archiveType)
    {
        try
        {
            if (archiveType == ReleaseArchiveType.Zip)
            {
                ZipFile.ExtractToDirectory(archivePath, extractDirectory, overwriteFiles: true);
                return OperationResult.Success();
            }

            await ExtractTarGzArchiveAsync(archivePath, extractDirectory);
            return OperationResult.Success();
        }
        catch (Exception ex)
        {
            return OperationResult.Fail(ex.Message);
        }
    }

    private static async Task ExtractTarGzArchiveAsync(string archivePath, string extractDirectory)
    {
        var extractRoot = Path.GetFullPath(extractDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        await using var archiveStream = File.OpenRead(archivePath);
        await using var gzipStream = new GZipStream(archiveStream, CompressionMode.Decompress);
        using var tarReader = new TarReader(gzipStream);

        TarEntry? entry;
        while ((entry = tarReader.GetNextEntry()) is not null)
        {
            if (string.IsNullOrWhiteSpace(entry.Name) || entry.DataStream is null)
            {
                continue;
            }

            var destinationPath = Path.GetFullPath(Path.Combine(extractDirectory, entry.Name));
            if (!destinationPath.StartsWith(extractRoot, StringComparison.Ordinal))
            {
                continue;
            }

            var directory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await using var output = File.Create(destinationPath);
            await entry.DataStream.CopyToAsync(output);
        }
    }

    private static string? FindExtractedExecutablePath(string extractDirectory, string executableName)
    {
        if (!Directory.Exists(extractDirectory))
        {
            return null;
        }

        return Directory.EnumerateFiles(extractDirectory, executableName, SearchOption.AllDirectories).FirstOrDefault();
    }

    private static string StageDownloadedExecutableForManualInstall(
        string sourcePath,
        string releaseVersion,
        string executableName,
        string targetDirectory)
    {
        var extension = Path.GetExtension(executableName);
        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(executableName);
        var stagedFileName = $"{fileNameWithoutExtension}-{releaseVersion}{extension}";
        var stagedPath = Path.Combine(targetDirectory, stagedFileName);
        File.Copy(sourcePath, stagedPath, overwrite: true);
        TryApplyUnixExecutableMode(stagedPath);
        return stagedPath;
    }

    private static void TryApplyUnixExecutableMode(string executablePath)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        try
        {
            var mode =
                UnixFileMode.UserRead
                | UnixFileMode.UserWrite
                | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead
                | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead
                | UnixFileMode.OtherExecute;
            File.SetUnixFileMode(executablePath, mode);
        }
        catch
        {
            // best effort only
        }
    }

    private static List<string> ParseCommandArgs(string input, string trigger)
    {
        var raw = input.Length > trigger.Length ? input[trigger.Length..].Trim() : string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        var tokens = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];

            if (c == '\\' && i + 1 < raw.Length && raw[i + 1] == '"')
            {
                current.Append('"');
                i++;
                continue;
            }

            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (!inQuotes && char.IsWhiteSpace(c))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    private static bool TryParseOpenArgs(
        IReadOnlyList<string> args,
        out string projectPath,
        out bool allowUnsafe,
        out TimeSpan daemonStartupTimeout,
        out string error)
    {
        projectPath = string.Empty;
        allowUnsafe = false;
        daemonStartupTimeout = DefaultOpenDaemonStartupTimeout;
        error = string.Empty;

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg.Equals("--allow-unsafe", StringComparison.Ordinal))
            {
                allowUnsafe = true;
                continue;
            }

            if (arg.Equals("--timeout", StringComparison.Ordinal))
            {
                if (i + 1 >= args.Count)
                {
                    error = "missing timeout value; usage /open <path> [--allow-unsafe] [--timeout <seconds>]";
                    return false;
                }

                var timeoutRaw = args[++i];
                if (!int.TryParse(timeoutRaw, out var timeoutSeconds) || timeoutSeconds < 1)
                {
                    error = $"invalid timeout value '{timeoutRaw}'; usage /open <path> [--allow-unsafe] [--timeout <seconds>]";
                    return false;
                }

                daemonStartupTimeout = TimeSpan.FromSeconds(timeoutSeconds);
                continue;
            }

            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                error = $"unrecognized option {arg}; usage /open <path> [--allow-unsafe] [--timeout <seconds>]";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(projectPath))
            {
                error = "too many positional arguments; usage /open <path> [--allow-unsafe] [--timeout <seconds>]";
                return false;
            }

            projectPath = arg;
        }

        if (string.IsNullOrWhiteSpace(projectPath))
        {
            error = "usage /open <path> [--allow-unsafe] [--timeout <seconds>]";
            return false;
        }

        return true;
    }

    private static bool TryParseNewArgs(
        IReadOnlyList<string> args,
        out string projectName,
        out string? unityVersion,
        out bool allowUnsafe,
        out string error)
    {
        projectName = string.Empty;
        unityVersion = null;
        allowUnsafe = false;
        error = string.Empty;

        foreach (var arg in args)
        {
            if (IsAllowUnsafeFlag(arg))
            {
                allowUnsafe = true;
                continue;
            }

            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                error = $"unrecognized option {arg}; usage /new <project-name> [unity-version] [--allow-unsafe]";
                return false;
            }

            if (string.IsNullOrWhiteSpace(projectName))
            {
                projectName = arg.Trim();
                continue;
            }

            if (string.IsNullOrWhiteSpace(unityVersion))
            {
                unityVersion = arg.Trim();
                continue;
            }

            error = "too many positional arguments; usage /new <project-name> [unity-version] [--allow-unsafe]";
            return false;
        }

        if (string.IsNullOrWhiteSpace(projectName))
        {
            error = "usage /new <project-name> [unity-version] [--allow-unsafe]";
            return false;
        }

        return true;
    }

    private static bool TryParseCloneArgs(
        IReadOnlyList<string> args,
        out string gitUrl,
        out bool allowUnsafe,
        out string error)
    {
        gitUrl = string.Empty;
        allowUnsafe = false;
        error = string.Empty;

        foreach (var arg in args)
        {
            if (IsAllowUnsafeFlag(arg))
            {
                allowUnsafe = true;
                continue;
            }

            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                error = $"unrecognized option {arg}; usage /clone <git-url> [--allow-unsafe]";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(gitUrl))
            {
                error = "too many positional arguments; usage /clone <git-url> [--allow-unsafe]";
                return false;
            }

            gitUrl = arg.Trim();
        }

        if (string.IsNullOrWhiteSpace(gitUrl))
        {
            error = "usage /clone <git-url> [--allow-unsafe]";
            return false;
        }

        return true;
    }

    private static bool TryParseRecentArgs(
        IReadOnlyList<string> args,
        out string? indexRaw,
        out bool allowUnsafe,
        out bool pruneRequested,
        out string error)
    {
        indexRaw = null;
        allowUnsafe = false;
        pruneRequested = false;
        error = string.Empty;

        foreach (var arg in args)
        {
            if (IsAllowUnsafeFlag(arg))
            {
                allowUnsafe = true;
                continue;
            }

            if (arg.Equals("--prune", StringComparison.OrdinalIgnoreCase)
                || arg.Equals("prune", StringComparison.OrdinalIgnoreCase))
            {
                pruneRequested = true;
                continue;
            }

            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                error = $"unrecognized option {arg}; usage /recent [idx] [--allow-unsafe] [--prune]";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(indexRaw))
            {
                error = "too many positional arguments; usage /recent [idx] [--allow-unsafe] [--prune]";
                return false;
            }

            indexRaw = arg.Trim();
        }

        if (pruneRequested && !string.IsNullOrWhiteSpace(indexRaw))
        {
            error = "cannot combine idx with prune; usage /recent [idx] [--allow-unsafe] [--prune]";
            return false;
        }

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

    private static bool IsAllowUnsafeFlag(string arg)
    {
        return arg.Equals("--allow-unsafe", StringComparison.Ordinal)
               || arg.Equals("--alow-unsafe", StringComparison.Ordinal);
    }

    private static int ResolvePromptPageSize(int itemCount, int maxPageSize)
    {
        if (itemCount <= 0)
        {
            return 3;
        }

        return Math.Max(3, Math.Min(itemCount, maxPageSize));
    }

    private static string ResolveAbsolutePath(string path, string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return baseDirectory;
        }

        if (path.StartsWith("~"))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var suffix = path[1..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return Path.GetFullPath(Path.Combine(home, suffix));
        }

        return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(baseDirectory, path));
    }

    private static bool LooksLikeUnityProject(string projectPath)
    {
        return Directory.Exists(Path.Combine(projectPath, "Assets"))
               && Directory.Exists(Path.Combine(projectPath, "ProjectSettings"));
    }

    private static bool TryGetProjectBridgeProtocol(string projectPath, out string? protocol)
    {
        protocol = null;
        var bridgePath = Path.Combine(projectPath, ".unifocl", "bridge.json");
        if (!File.Exists(bridgePath))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(bridgePath));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!document.RootElement.TryGetProperty("protocol", out var protocolElement))
            {
                return false;
            }

            if (protocolElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            protocol = protocolElement.GetString();
            return !string.IsNullOrWhiteSpace(protocol);
        }
        catch
        {
            return false;
        }
    }

    private static OperationResult EnsureProjectLocalConfig(string projectPath)
    {
        try
        {
            var templatesPath = Path.Combine(projectPath, "templates.json");
            if (!File.Exists(templatesPath))
            {
                var templates = JsonSerializer.Serialize(new
                {
                    templates = new Dictionary<string, string>
                    {
                        ["script"] = "Assets/Scripts/NewScript.cs",
                        ["shader"] = "Assets/Shaders/NewShader.shader",
                        ["material"] = "Assets/Materials/NewMaterial.mat"
                    }
                }, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(templatesPath, templates + Environment.NewLine);
            }

            var bridgeDir = Path.Combine(projectPath, ".unifocl");
            Directory.CreateDirectory(bridgeDir);

            var bridgePath = Path.Combine(bridgeDir, "bridge.json");
            var bridge = JsonSerializer.Serialize(new
            {
                projectPath,
                daemon = new { host = "127.0.0.1", port = DaemonControlService.ResolveProjectDaemonPort(projectPath) },
                protocol = CliVersion.Protocol,
                updatedAtUtc = DateTimeOffset.UtcNow
            }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(bridgePath, bridge + Environment.NewLine);

            return OperationResult.Success();
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"failed to write local config ({ex.Message})");
        }
    }

    private static OperationResult WriteDefaultUnityManifest(string projectPath)
    {
        try
        {
            var manifestPath = Path.Combine(projectPath, "Packages", "manifest.json");
            if (!File.Exists(manifestPath))
            {
                var manifest = JsonSerializer.Serialize(new
                {
                    dependencies = new Dictionary<string, string>
                    {
                        ["com.unity.ide.rider"] = "3.0.35",
                        ["com.unity.test-framework"] = "1.4.5",
                        ["com.unity.textmeshpro"] = "3.0.6",
                        ["com.unity.timeline"] = "1.8.9",
                        ["com.unity.ugui"] = "1.0.0"
                    }
                }, new JsonSerializerOptions { WriteIndented = true });

                File.WriteAllText(manifestPath, manifest + Environment.NewLine);
            }

            return OperationResult.Success();
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"failed to write Packages/manifest.json ({ex.Message})");
        }
    }

    private static OperationResult WriteProjectVersion(string projectPath, string unityVersion)
    {
        try
        {
            var projectVersionPath = Path.Combine(projectPath, "ProjectSettings", "ProjectVersion.txt");
            var content =
                $"m_EditorVersion: {unityVersion}{Environment.NewLine}m_EditorVersionWithRevision: {unityVersion} (placeholder){Environment.NewLine}";
            File.WriteAllText(projectVersionPath, content);
            return OperationResult.Success();
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"failed to write ProjectVersion.txt ({ex.Message})");
        }
    }

    private static async Task<OperationResult> EnsureRequiredUnityPackageReferencesAsync(string projectPath)
    {
        try
        {
            var manifestPath = Path.Combine(projectPath, "Packages", "manifest.json");
            if (!File.Exists(manifestPath))
            {
                return OperationResult.Fail("Packages/manifest.json is missing");
            }

            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return OperationResult.Fail("manifest.json has invalid format");
            }

            var root = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                root[property.Name] = JsonSerializer.Deserialize<object>(property.Value.GetRawText());
            }

            var dependencies = new Dictionary<string, string>(StringComparer.Ordinal);
            if (document.RootElement.TryGetProperty("dependencies", out var depsElement)
                && depsElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var dep in depsElement.EnumerateObject())
                {
                    if (dep.Value.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    var version = dep.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(version))
                    {
                        dependencies[dep.Name] = version;
                    }
                }
            }

            var requiredPackages = InferRequiredUnityPackages(projectPath);

            var scopedRegistries = LoadScopedRegistries(projectPath);
            var resolvedRequiredPackages = await ResolveRequiredPackageGraphAsync(requiredPackages, scopedRegistries);

            var changed = false;

            foreach (var required in resolvedRequiredPackages)
            {
                if (dependencies.ContainsKey(required.Key))
                {
                    continue;
                }

                dependencies[required.Key] = required.Value;
                changed = true;
            }

            if (!changed)
            {
                return OperationResult.Success();
            }

            root["dependencies"] = dependencies
                .OrderBy(x => x.Key, StringComparer.Ordinal)
                .ToDictionary(x => x.Key, x => (object?)x.Value, StringComparer.Ordinal);
            var updated = JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(manifestPath, updated + Environment.NewLine);
            return OperationResult.Success();
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"failed to update required Unity package references ({ex.Message})");
        }
    }

    private static async Task<(bool Ok, Dictionary<string, string> Dependencies, string Error)> ProbeRequiredMcpTransitiveDependenciesAsync(
        IReadOnlyList<ScopedRegistryConfig> scopedRegistries)
    {
        var dependencies = await TryFetchPackageDependenciesAsync(
            RequiredMcpPackageId,
            RequiredMcpPackageTarget,
            scopedRegistries);
        if (dependencies.Count > 0)
        {
            return (true, dependencies, string.Empty);
        }

        var diagnostics = await DiagnoseMcpDependencyResolutionFailureAsync(scopedRegistries);
        if (!string.IsNullOrWhiteSpace(diagnostics))
        {
            return (false, new Dictionary<string, string>(StringComparer.Ordinal), diagnostics);
        }

        return (false, new Dictionary<string, string>(StringComparer.Ordinal),
            "failed to resolve transitive dependencies for required package com.coplaydev.unity-mcp; package metadata returned no dependencies");
    }

    private static async Task<string?> DiagnoseMcpDependencyResolutionFailureAsync(IReadOnlyList<ScopedRegistryConfig> scopedRegistries)
    {
        try
        {
            if (TryBuildGitHubRawPackageJsonUrl(RequiredMcpPackageTarget, out var packageJsonUrl))
            {
                using var gitResponse = await UnityRegistryHttpClient.GetAsync(packageJsonUrl);
                if (!gitResponse.IsSuccessStatusCode)
                {
                    return $"failed to resolve transitive dependencies for required package {RequiredMcpPackageId} (git package metadata lookup returned {(int)gitResponse.StatusCode})";
                }
            }
        }
        catch (Exception ex) when (LooksLikePermissionOrNetworkRestriction(ex.Message))
        {
            return $"failed to resolve transitive dependencies for required package {RequiredMcpPackageId} ({ex.Message}); rerun with elevated permissions";
        }
        catch (Exception ex)
        {
            return $"failed to resolve transitive dependencies for required package {RequiredMcpPackageId} ({ex.Message})";
        }

        try
        {
            var registryUrl = ResolveRegistryUrlForPackage(RequiredMcpPackageId, scopedRegistries);
            var endpoint = $"{registryUrl.TrimEnd('/')}/{Uri.EscapeDataString(RequiredMcpPackageId)}";
            using var registryResponse = await UnityRegistryHttpClient.GetAsync(endpoint);
            if (!registryResponse.IsSuccessStatusCode)
            {
                return $"failed to resolve transitive dependencies for required package {RequiredMcpPackageId} (registry lookup returned {(int)registryResponse.StatusCode})";
            }
        }
        catch (Exception ex) when (LooksLikePermissionOrNetworkRestriction(ex.Message))
        {
            return $"failed to resolve transitive dependencies for required package {RequiredMcpPackageId} ({ex.Message}); rerun with elevated permissions";
        }
        catch (Exception ex)
        {
            return $"failed to resolve transitive dependencies for required package {RequiredMcpPackageId} ({ex.Message})";
        }

        return null;
    }

    private static bool LooksLikePermissionOrNetworkRestriction(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        var normalized = message.ToLowerInvariant();
        return normalized.Contains("access to the path")
               || normalized.Contains("operation not permitted")
               || normalized.Contains("permission denied")
               || normalized.Contains("could not resolve host")
               || normalized.Contains("nodename nor servname provided")
               || normalized.Contains("name or service not known")
               || normalized.Contains("temporary failure in name resolution")
               || normalized.Contains("network is unreachable")
               || normalized.Contains("sandbox");
    }

    private static OperationResult SyncInstalledMcpPackageJsonDependencies(
        string projectPath,
        IReadOnlyDictionary<string, string> transitiveDependencies)
    {
        if (transitiveDependencies.Count == 0)
        {
            return OperationResult.Success();
        }

        try
        {
            var packageJsonPaths = new List<string>();
            var directPackageJson = Path.Combine(projectPath, "Packages", RequiredMcpPackageId, "package.json");
            if (File.Exists(directPackageJson))
            {
                packageJsonPaths.Add(directPackageJson);
            }

            var packageCacheRoot = Path.Combine(projectPath, "Library", "PackageCache");
            if (Directory.Exists(packageCacheRoot))
            {
                foreach (var cacheDir in Directory.EnumerateDirectories(packageCacheRoot, $"{RequiredMcpPackageId}@*"))
                {
                    var packageJsonPath = Path.Combine(cacheDir, "package.json");
                    if (File.Exists(packageJsonPath))
                    {
                        packageJsonPaths.Add(packageJsonPath);
                    }
                }
            }

            foreach (var packageJsonPath in packageJsonPaths.Distinct(StringComparer.Ordinal))
            {
                var syncResult = MergeDependenciesIntoPackageJson(packageJsonPath, transitiveDependencies);
                if (!syncResult.Ok)
                {
                    return syncResult;
                }
            }

            return OperationResult.Success();
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"failed to sync local MCP package.json dependencies ({ex.Message})");
        }
    }

    private static OperationResult MergeDependenciesIntoPackageJson(
        string packageJsonPath,
        IReadOnlyDictionary<string, string> dependenciesToMerge)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(packageJsonPath));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return OperationResult.Fail($"failed to sync local MCP package.json dependencies (invalid JSON root: {packageJsonPath})");
            }

            var root = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                root[property.Name] = JsonSerializer.Deserialize<object>(property.Value.GetRawText());
            }

            var dependencies = new Dictionary<string, string>(StringComparer.Ordinal);
            if (document.RootElement.TryGetProperty("dependencies", out var depsElement)
                && depsElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var dep in depsElement.EnumerateObject())
                {
                    if (dep.Value.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    var value = dep.Value.GetString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        dependencies[dep.Name] = value!;
                    }
                }
            }

            var changed = false;
            foreach (var dependency in dependenciesToMerge)
            {
                if (dependencies.ContainsKey(dependency.Key))
                {
                    continue;
                }

                dependencies[dependency.Key] = dependency.Value;
                changed = true;
            }

            if (!changed)
            {
                return OperationResult.Success();
            }

            root["dependencies"] = dependencies
                .OrderBy(item => item.Key, StringComparer.Ordinal)
                .ToDictionary(item => item.Key, item => (object?)item.Value, StringComparer.Ordinal);
            var updated = JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(packageJsonPath, updated + Environment.NewLine);
            return OperationResult.Success();
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"failed to sync local MCP package.json dependencies ({ex.Message})");
        }
    }

    private static async Task<Dictionary<string, string>> ResolveRequiredPackageGraphAsync(
        Dictionary<string, string> seedPackages,
        IReadOnlyList<ScopedRegistryConfig> scopedRegistries)
    {
        var resolved = new Dictionary<string, string>(seedPackages, StringComparer.Ordinal);
        var queue = new Queue<KeyValuePair<string, string>>(seedPackages);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!visited.Add(current.Key))
            {
                continue;
            }

            var dependencies = await TryFetchPackageDependenciesAsync(current.Key, current.Value, scopedRegistries);
            foreach (var dependency in dependencies)
            {
                if (!IsRegistryPackageId(dependency.Key))
                {
                    continue;
                }

                var value = dependency.Value?.Trim() ?? string.Empty;
                if (!IsLikelyRegistryVersionSpec(value))
                {
                    continue;
                }

                if (!resolved.TryGetValue(dependency.Key, out var existing))
                {
                    resolved[dependency.Key] = value;
                    queue.Enqueue(new KeyValuePair<string, string>(dependency.Key, value));
                    continue;
                }

                if (string.IsNullOrWhiteSpace(existing) && !string.IsNullOrWhiteSpace(value))
                {
                    resolved[dependency.Key] = value;
                }
            }
        }

        return resolved;
    }

    private static async Task<Dictionary<string, string>> TryFetchPackageDependenciesAsync(
        string packageId,
        string packageTarget,
        IReadOnlyList<ScopedRegistryConfig> scopedRegistries)
    {
        if (string.IsNullOrWhiteSpace(packageId) || string.IsNullOrWhiteSpace(packageTarget))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        if (packageTarget.Contains(".git", StringComparison.OrdinalIgnoreCase)
            || packageTarget.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || packageTarget.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            var gitDependencies = await TryFetchGitPackageDependenciesAsync(packageTarget);
            var registryDependencies = await TryFetchRegistryPackageDependenciesAsync(
                packageId,
                versionSpec: "latest",
                scopedRegistries);
            if (gitDependencies.Count == 0)
            {
                return registryDependencies;
            }

            foreach (var dependency in registryDependencies)
            {
                if (!gitDependencies.ContainsKey(dependency.Key))
                {
                    gitDependencies[dependency.Key] = dependency.Value;
                }
            }

            return gitDependencies;
        }

        return await TryFetchRegistryPackageDependenciesAsync(packageId, packageTarget, scopedRegistries);
    }

    private static async Task<Dictionary<string, string>> TryFetchRegistryPackageDependenciesAsync(
        string packageId,
        string versionSpec,
        IReadOnlyList<ScopedRegistryConfig> scopedRegistries)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var registryUrl = ResolveRegistryUrlForPackage(packageId, scopedRegistries);
            var endpoint = $"{registryUrl.TrimEnd('/')}/{Uri.EscapeDataString(packageId)}";
            using var response = await UnityRegistryHttpClient.GetAsync(endpoint);
            if (!response.IsSuccessStatusCode)
            {
                return map;
            }

            var raw = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(raw);
            if (!document.RootElement.TryGetProperty("versions", out var versionsElement)
                || versionsElement.ValueKind != JsonValueKind.Object)
            {
                return map;
            }

            var selectedVersion = versionSpec.Trim();
            if (!versionsElement.TryGetProperty(selectedVersion, out var packageNode)
                || packageNode.ValueKind != JsonValueKind.Object)
            {
                if (document.RootElement.TryGetProperty("dist-tags", out var tagsElement)
                    && tagsElement.ValueKind == JsonValueKind.Object
                    && tagsElement.TryGetProperty("latest", out var latestElement)
                    && latestElement.ValueKind == JsonValueKind.String)
                {
                    var latest = latestElement.GetString();
                    if (!string.IsNullOrWhiteSpace(latest)
                        && versionsElement.TryGetProperty(latest, out var latestNode)
                        && latestNode.ValueKind == JsonValueKind.Object)
                    {
                        packageNode = latestNode;
                    }
                }
            }

            if (packageNode.ValueKind != JsonValueKind.Object
                || !packageNode.TryGetProperty("dependencies", out var dependenciesElement)
                || dependenciesElement.ValueKind != JsonValueKind.Object)
            {
                return map;
            }

            foreach (var dependency in dependenciesElement.EnumerateObject())
            {
                if (dependency.Value.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var value = dependency.Value.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    map[dependency.Name] = value!;
                }
            }

            return map;
        }
        catch
        {
            return map;
        }
    }

    private static async Task<Dictionary<string, string>> TryFetchGitPackageDependenciesAsync(string gitTarget)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!TryBuildGitHubRawPackageJsonUrl(gitTarget, out var packageJsonUrl))
        {
            return map;
        }

        try
        {
            using var response = await UnityRegistryHttpClient.GetAsync(packageJsonUrl);
            if (!response.IsSuccessStatusCode)
            {
                return map;
            }

            var raw = await response.Content.ReadAsStringAsync();
            return TryReadDependenciesFromPackageJson(raw);
        }
        catch
        {
            return map;
        }
    }

    private static bool TryBuildGitHubRawPackageJsonUrl(string gitTarget, out string packageJsonUrl)
    {
        packageJsonUrl = string.Empty;
        if (string.IsNullOrWhiteSpace(gitTarget))
        {
            return false;
        }

        var normalized = gitTarget.Trim();
        if (normalized.StartsWith("git+", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized["git+".Length..];
        }

        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri)
            || !uri.Host.Contains("github.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var fragment = uri.Fragment.TrimStart('#');
        var reference = string.IsNullOrWhiteSpace(fragment) ? "main" : fragment;

        var path = uri.AbsolutePath.Trim('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^4];
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
        {
            return false;
        }

        var owner = segments[0];
        var repository = segments[1];

        var packagePath = "package.json";
        var query = uri.Query.TrimStart('?');
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2, StringSplitOptions.TrimEntries);
            if (kv.Length != 2 || !kv[0].Equals("path", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var decodedPath = Uri.UnescapeDataString(kv[1]).Trim('/');
            packagePath = string.IsNullOrWhiteSpace(decodedPath)
                ? "package.json"
                : $"{decodedPath}/package.json";
            break;
        }

        packageJsonUrl = $"https://raw.githubusercontent.com/{owner}/{repository}/{reference}/{packagePath}";
        return true;
    }

    private static Dictionary<string, string> TryReadDependenciesFromPackageJson(string rawPackageJson)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(rawPackageJson))
        {
            return map;
        }

        try
        {
            using var document = JsonDocument.Parse(rawPackageJson);
            if (!document.RootElement.TryGetProperty("dependencies", out var dependenciesElement)
                || dependenciesElement.ValueKind != JsonValueKind.Object)
            {
                return map;
            }

            foreach (var dependency in dependenciesElement.EnumerateObject())
            {
                if (dependency.Value.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var value = dependency.Value.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    map[dependency.Name] = value!;
                }
            }

            return map;
        }
        catch
        {
            return map;
        }
    }

    private static bool IsLikelyRegistryVersionSpec(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return !value.StartsWith("git+", StringComparison.OrdinalIgnoreCase)
               && !value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
               && !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
               && !value.StartsWith("file:", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRegistryPackageId(string packageId)
    {
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return false;
        }

        var segments = packageId.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
        {
            return false;
        }

        return segments.All(segment => segment.All(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '_'));
    }

    private static Dictionary<string, string> InferRequiredUnityPackages(string projectPath)
    {
        var namespaceToPackage = new Dictionary<string, (string PackageId, string Version)>(StringComparer.Ordinal)
        {
            ["UnityEngine.UI"] = ("com.unity.ugui", "1.0.0"),
            ["UnityEngine.EventSystems"] = ("com.unity.ugui", "1.0.0"),
            ["TMPro"] = ("com.unity.textmeshpro", "3.0.6")
        };
        var required = new Dictionary<string, string>(StringComparer.Ordinal);
        var usingPattern = new Regex(@"^\s*using\s+([A-Za-z0-9_.]+)\s*;", RegexOptions.Compiled);
        var assetsPath = Path.Combine(projectPath, "Assets");
        if (!Directory.Exists(assetsPath))
        {
            return required;
        }

        foreach (var scriptPath in Directory.EnumerateFiles(assetsPath, "*.cs", SearchOption.AllDirectories))
        {
            foreach (var line in File.ReadLines(scriptPath))
            {
                var match = usingPattern.Match(line);
                if (!match.Success)
                {
                    continue;
                }

                var ns = match.Groups[1].Value;
                if (!namespaceToPackage.TryGetValue(ns, out var package))
                {
                    continue;
                }

                required[package.PackageId] = package.Version;
            }
        }

        return required;
    }

    private static bool ManifestContainsPackage(string projectPath, string packageId)
    {
        try
        {
            var manifestPath = Path.Combine(projectPath, "Packages", "manifest.json");
            if (!File.Exists(manifestPath))
            {
                return false;
            }

            var manifest = File.ReadAllText(manifestPath);
            return manifest.Contains($"\"{packageId}\"", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static List<ScopedRegistryConfig> LoadScopedRegistries(string projectPath)
    {
        var manifestPath = Path.Combine(projectPath, "Packages", "manifest.json");
        if (!File.Exists(manifestPath))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (!document.RootElement.TryGetProperty("scopedRegistries", out var scopedRegistriesElement)
                || scopedRegistriesElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var resolved = new List<ScopedRegistryConfig>();
            foreach (var registryElement in scopedRegistriesElement.EnumerateArray())
            {
                if (registryElement.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!registryElement.TryGetProperty("url", out var urlElement)
                    || urlElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var url = urlElement.GetString()?.Trim();
                if (string.IsNullOrWhiteSpace(url))
                {
                    continue;
                }

                var scopes = new List<string>();
                if (registryElement.TryGetProperty("scopes", out var scopesElement)
                    && scopesElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var scopeElement in scopesElement.EnumerateArray())
                    {
                        if (scopeElement.ValueKind != JsonValueKind.String)
                        {
                            continue;
                        }

                        var scope = scopeElement.GetString()?.Trim();
                        if (!string.IsNullOrWhiteSpace(scope))
                        {
                            scopes.Add(scope);
                        }
                    }
                }

                resolved.Add(new ScopedRegistryConfig(url, scopes));
            }

            return resolved;
        }
        catch
        {
            return [];
        }
    }

    private static string ResolveRegistryUrlForPackage(string packageId, IReadOnlyList<ScopedRegistryConfig> scopedRegistries)
    {
        var resolvedUrl = "https://packages.unity.com";
        var bestScopeLength = -1;
        foreach (var scopedRegistry in scopedRegistries)
        {
            foreach (var scope in scopedRegistry.Scopes)
            {
                if (!packageId.StartsWith($"{scope}.", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (scope.Length <= bestScopeLength)
                {
                    continue;
                }

                bestScopeLength = scope.Length;
                resolvedUrl = scopedRegistry.Url;
            }
        }

        return resolvedUrl;
    }

    private static async Task<ProcessResult> RunProcessAsync(
        string fileName,
        string arguments,
        string workingDirectory,
        TimeSpan timeout)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return new ProcessResult(-1, string.Empty, "failed to start process");
            }

            using var timeoutCts = new CancellationTokenSource(timeout);
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                }

                return new ProcessResult(
                    -1,
                    string.Empty,
                    $"process timed out after {(int)timeout.TotalSeconds}s ({fileName} {arguments})");
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            return new ProcessResult(process.ExitCode, stdout, stderr);
        }
        catch (Exception ex)
        {
            return new ProcessResult(-1, string.Empty, ex.Message);
        }
    }

    private static string GuessRepoFolderName(string gitUrl)
    {
        var trimmed = gitUrl.TrimEnd('/');
        var lastSegment = trimmed[(trimmed.LastIndexOf('/') + 1)..];
        if (lastSegment.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            lastSegment = lastSegment[..^4];
        }

        return string.IsNullOrWhiteSpace(lastSegment) ? "cloned-project" : lastSegment;
    }

    private static string SummarizeProcessError(ProcessResult result)
    {
        var text = string.IsNullOrWhiteSpace(result.Stderr) ? result.Stdout : result.Stderr;
        if (string.IsNullOrWhiteSpace(text))
        {
            return $"exit code {result.ExitCode}";
        }

        var firstLine = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return firstLine is null ? $"exit code {result.ExitCode}" : firstLine;
    }

    private static bool TryLoadCliConfig(out CliConfig config, out string? error)
    {
        config = new CliConfig();
        error = null;

        try
        {
            var path = GetCliConfigPath();
            if (!File.Exists(path))
            {
                return true;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "invalid config format";
                return false;
            }

            if (document.RootElement.TryGetProperty("theme", out var themeProperty)
                && themeProperty.ValueKind == JsonValueKind.String)
            {
                config.Theme = themeProperty.GetString();
            }

            if (document.RootElement.TryGetProperty("unityProjectPath", out var unityProjectPathProperty)
                && unityProjectPathProperty.ValueKind == JsonValueKind.String)
            {
                var configuredPath = unityProjectPathProperty.GetString();
                if (!string.IsNullOrWhiteSpace(configuredPath))
                {
                    config.UnityProjectPath = Path.GetFullPath(configuredPath);
                }
            }

            if (document.RootElement.TryGetProperty("recentStaleDays", out var recentStaleDaysProperty))
            {
                if (recentStaleDaysProperty.ValueKind == JsonValueKind.Number
                    && recentStaleDaysProperty.TryGetInt32(out var staleDaysFromNumber))
                {
                    config.RecentPruneStaleDays = staleDaysFromNumber;
                }
                else if (recentStaleDaysProperty.ValueKind == JsonValueKind.String
                         && TryParseRecentPruneStaleDays(recentStaleDaysProperty.GetString() ?? string.Empty, out var staleDaysFromString))
                {
                    config.RecentPruneStaleDays = staleDaysFromString;
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            error = $"failed to read config ({ex.Message})";
            return false;
        }
    }

    private static bool TrySaveCliConfig(CliConfig config, out string? error)
    {
        error = null;
        try
        {
            var path = GetCliConfigPath();
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory))
            {
                error = "failed to resolve config directory";
                return false;
            }

            Directory.CreateDirectory(directory);
            var payload = JsonSerializer.Serialize(
                new Dictionary<string, object?>
                {
                    ["theme"] = NormalizeTheme(config.Theme),
                    ["unityProjectPath"] = NormalizeProjectPath(config.UnityProjectPath),
                    ["recentStaleDays"] = config.RecentPruneStaleDays
                },
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, payload + Environment.NewLine);
            return true;
        }
        catch (Exception ex)
        {
            error = $"failed to write config ({ex.Message})";
            return false;
        }
    }

    private static string ResolveEffectiveTheme(CliConfig config)
    {
        var fromEnv = Environment.GetEnvironmentVariable("UNIFOCL_THEME");
        if (NormalizeTheme(fromEnv) is string envTheme)
        {
            return envTheme;
        }

        return NormalizeTheme(config.Theme) ?? "dark";
    }

    private static int ResolveRecentPruneStaleDays(CliConfig config)
    {
        if (config.RecentPruneStaleDays is int configured && configured > 0)
        {
            return configured;
        }

        return DefaultRecentPruneStaleDays;
    }

    private static bool IsThemeKey(string key)
    {
        return key.Equals("theme", StringComparison.OrdinalIgnoreCase)
               || key.Equals("ui.theme", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRecentStaleDaysKey(string key)
    {
        return key.Equals("recent.staledays", StringComparison.OrdinalIgnoreCase)
               || key.Equals("recent.stale-days", StringComparison.OrdinalIgnoreCase)
               || key.Equals("recent.prunedays", StringComparison.OrdinalIgnoreCase)
               || key.Equals("recent.prune-days", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseRecentPruneStaleDays(string raw, out int staleDays)
    {
        staleDays = 0;
        if (!int.TryParse(raw.Trim(), out var parsed))
        {
            return false;
        }

        if (parsed <= 0)
        {
            return false;
        }

        staleDays = parsed;
        return true;
    }

    private static string? NormalizeTheme(string? theme)
    {
        if (string.Equals(theme, "dark", StringComparison.OrdinalIgnoreCase))
        {
            return "dark";
        }

        if (string.Equals(theme, "light", StringComparison.OrdinalIgnoreCase))
        {
            return "light";
        }

        return null;
    }

    private static string? NormalizeProjectPath(string? projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            return null;
        }

        return Path.GetFullPath(projectPath);
    }

    private static bool TrySaveLastUnityProjectPath(string projectPath, out string? error)
    {
        error = null;
        if (!TryLoadCliConfig(out var config, out var loadError))
        {
            error = loadError ?? "failed to read config";
            return false;
        }

        config.UnityProjectPath = NormalizeProjectPath(projectPath);
        if (!TrySaveCliConfig(config, out var saveError))
        {
            error = saveError ?? "failed to write config";
            return false;
        }

        return true;
    }

    private static string GetCliConfigPath()
    {
        var explicitPath = Environment.GetEnvironmentVariable("UNIFOCL_CONFIG_PATH");
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return Path.GetFullPath(explicitPath);
        }

        var configRoot = Environment.GetEnvironmentVariable("UNIFOCL_CONFIG_ROOT");
        if (!string.IsNullOrWhiteSpace(configRoot))
        {
            return Path.Combine(Path.GetFullPath(configRoot), "config.json");
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".unifocl", "config.json");
    }

    private sealed record ExternalDependencyRequirement(
        string Id,
        string DisplayName,
        string ProbeCommand,
        string ProbeArgs,
        string? MinimumVersion);

    private sealed record ExternalDependencyProbeResult(
        ExternalDependencyRequirement Requirement,
        bool Satisfied,
        string? InstalledVersion,
        string? Error);

    private sealed record DependencyInstallOption(
        string Label,
        string Command,
        string Source);

    private sealed class CliConfig
    {
        public string? Theme { get; set; }
        public string? UnityProjectPath { get; set; }
        public int? RecentPruneStaleDays { get; set; }
    }

    private sealed record PlatformUpdateSpec(
        string Label,
        string AssetSuffix,
        string ExecutableName,
        ReleaseArchiveType ArchiveType);

    private sealed record ReleaseInfo(string TagName, List<ReleaseAsset> Assets);

    private sealed record ReleaseAsset(string Name, string DownloadUrl);

    private sealed record ReleaseFetchResult(bool Ok, string Error, ReleaseInfo? Release)
    {
        public static ReleaseFetchResult Success(ReleaseInfo release)
            => new(true, string.Empty, release);

        public static ReleaseFetchResult Fail(string error)
            => new(false, string.IsNullOrWhiteSpace(error) ? "unknown error" : error, null);
    }

    private enum ReleaseArchiveType
    {
        Zip,
        TarGz
    }

    private sealed record ScopedRegistryConfig(string Url, List<string> Scopes);

}
