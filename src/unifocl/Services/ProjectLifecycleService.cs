using Spectre.Console;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;

internal sealed partial class ProjectLifecycleService
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
    private const string WingetPackageId = "KinichiAnjuMakino.unifocl";
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
            "/agent setup" => await HandleAgentSetupFromInputAsync(input, matched, log),
            "/unity detect" => await HandleUnityDetectAsync(log),
            "/unity set" => await HandleUnitySetAsync(input, matched, log),
            "/close" => await HandleCloseAsync(session, daemonControlService, daemonRuntime, log),
            "/init" => await HandleInitAsync(input, matched, session, daemonControlService, daemonRuntime, log),
            "/config" => await HandleConfigAsync(input, matched, log),
            _ => false
        };
    }

    public async Task<bool> RunQuickUpdateAsync(Action<string> log)
    {
        var hadError = false;
        await HandleUpdateAsync(line =>
        {
            log(line);
            if (line.Contains("[red]error[/]", StringComparison.OrdinalIgnoreCase))
            {
                hadError = true;
            }
        });

        return !hadError;
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
                        .HighlightStyle(CliTheme.SelectionHighlightStyle)
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

        // Fast path: if a Bridge mode daemon is already responsive, attach without acquiring
        // the project open lock. No Unity launch is needed so concurrent attaches are safe.
        // However, if a different agentic session owns this daemon, enforce isolation:
        // each agent must have its own Unity instance to prevent mutation cross-contamination.
        var candidatePort = DaemonControlService.ResolveProjectDaemonPort(projectPath);
        if (await IsDaemonEndpointAliveAsync(candidatePort))
        {
            if (session.SessionSeed is not null)
            {
                var ownerSeed = AgenticStatePersistenceService.FindSessionSeedByPort(candidatePort);
                if (ownerSeed is not null
                    && !string.Equals(ownerSeed, session.SessionSeed, StringComparison.Ordinal))
                {
                    log($"[red]error[/]: project is already open in another agent session ({Markup.Escape(ownerSeed)})");
                    log($"[yellow]hint[/]: concurrent agents must clone the project to an isolated path before opening");
                    log($"[yellow]hint[/]: use the [white]project.clone[/] ExecV2 operation to create an isolated copy with a seeded Library cache, then open the cloned path");
                    log($"[yellow]example[/]: {{\"operation\":\"project.clone\",\"args\":{{\"sourcePath\":\"{Markup.Escape(projectPath)}\",\"destPath\":\"<new-path>\"}}}}");
                    return false;
                }
            }

            return await TryOpenProjectLockedAsync(
                projectPath, session, daemonControlService, daemonRuntime,
                editorDependencyInitializerService, promptForInitialization,
                ensureMcpHostDependencyCheck, allowUnsafe, daemonStartupTimeout, log);
        }

        // Acquire cross-process file lock to prevent concurrent Unity launches on the same project.
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
            log($"[yellow]hint[/]: concurrent agents must clone the project to an isolated path before opening");
            log($"[yellow]hint[/]: use the [white]project.clone[/] ExecV2 operation to create an isolated copy with a seeded Library cache, then open the cloned path");
            log($"[yellow]example[/]: {{\"operation\":\"project.clone\",\"args\":{{\"sourcePath\":\"{Markup.Escape(projectPath)}\",\"destPath\":\"<new-path>\"}}}}");
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

        if (!Console.IsOutputRedirected)
        {
            Console.Write("\u001b[H\u001b[0J");
        }

        log($"[green]open[/]: project mode active -> [white]{Markup.Escape(projectPath)}[/]");
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

    private static async Task<bool> IsDaemonEndpointAliveAsync(int port)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var response = await UnityRegistryHttpClient.GetAsync(
                $"http://127.0.0.1:{port}/ping", cts.Token);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
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

    private sealed record TextFetchResult(bool Ok, string Error, string? Content)
    {
        public static TextFetchResult Success(string content)
            => new(true, string.Empty, content);

        public static TextFetchResult Fail(string error)
            => new(false, string.IsNullOrWhiteSpace(error) ? "unknown error" : error, null);
    }

    private sealed record AssetIntegrityResult(bool Ok, string Error, string? Sha256)
    {
        public static AssetIntegrityResult Success(string sha256)
            => new(true, string.Empty, sha256);

        public static AssetIntegrityResult Fail(string error)
            => new(false, string.IsNullOrWhiteSpace(error) ? "unknown error" : error, null);
    }

    private sealed record AttestationVerificationResult(bool Ok, bool Skipped, string Message)
    {
        public static AttestationVerificationResult Success()
            => new(true, false, "verified");

        public static AttestationVerificationResult Skip(string message)
            => new(true, true, string.IsNullOrWhiteSpace(message) ? "skipped" : message);

        public static AttestationVerificationResult Fail(string message)
            => new(false, false, string.IsNullOrWhiteSpace(message) ? "verification failed" : message);
    }

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
