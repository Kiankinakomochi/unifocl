using Spectre.Console;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

internal sealed class ProjectLifecycleService
{
    private static readonly TimeSpan GitVersionProbeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan GitCloneTimeout = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan McpBatchInstallTimeout = TimeSpan.FromMinutes(8);
    private const int DefaultRecentPruneStaleDays = 14;
    private const string RequiredMcpPackageId = "com.coplaydev.unity-mcp";
    private const string RequiredMcpPackageTarget = "https://github.com/CoplayDev/unity-mcp.git?path=/MCPForUnity#main";

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
        if (!TryParseOpenArgs(args, out var openPath, out var allowUnsafe, out var openParseError))
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
            allowUnsafe: allowUnsafe,
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
        log($"[grey]new[/]: step 1/5 create project directory -> [white]{Markup.Escape(projectPath)}[/]");

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

        log("[grey]new[/]: step 2/5 write Unity package manifest");
        var manifestResult = WriteDefaultUnityManifest(projectPath);
        if (!manifestResult.Ok)
        {
            log($"[red]error[/]: {Markup.Escape(manifestResult.Error)}");
            return true;
        }

        log($"[grey]new[/]: step 3/5 set Unity editor version [white]{Markup.Escape(unityVersion)}[/]");
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

        log("[grey]new[/]: step 4/5 generate local templates and bridge config");
        var configResult = EnsureProjectLocalConfig(projectPath);
        if (!configResult.Ok)
        {
            log($"[red]error[/]: {Markup.Escape(configResult.Error)}");
            return true;
        }

        log("[grey]new[/]: step 5/5 open bootstrapped project");
        if (await TryOpenProjectAsync(
                projectPath,
                session,
                daemonControlService,
                daemonRuntime,
                _editorDependencyInitializerService,
                promptForInitialization: true,
                allowUnsafe: allowUnsafe,
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
                allowUnsafe: allowUnsafe,
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

        var configResult = await RunWithStatusAsync(
            "Preparing local bridge config...",
            () => Task.FromResult(EnsureProjectLocalConfig(targetPath)));
        if (!configResult.Ok)
        {
            log($"[red]error[/]: {Markup.Escape(configResult.Error)}");
            return true;
        }

        var packageFixResult = await RunWithStatusAsync(
            "Checking project package references...",
            () => Task.FromResult(EnsureRequiredUnityPackageReferences(targetPath)));
        if (!packageFixResult.Ok)
        {
            log($"[red]error[/]: {Markup.Escape(packageFixResult.Error)}");
            return true;
        }

        var initResult = await RunWithStatusAsync(
            "Installing editor bridge dependencies...",
            () => Task.FromResult(_editorDependencyInitializerService.InitializeProject(targetPath, log)));
        if (!initResult.Ok)
        {
            log($"[red]error[/]: {Markup.Escape(initResult.Error)}");
            return true;
        }

        var mcpInstallResult = await RunWithStatusAsync(
            "Installing required MCP package...",
            () => EnsureMcpPackageInstalledAsync(targetPath, log));
        if (!mcpInstallResult.Ok)
        {
            log($"[red]error[/]: {Markup.Escape(mcpInstallResult.Error)}");
            return true;
        }

        log($"[green]init[/]: ready at [white]{Markup.Escape(targetPath)}[/]");
        return true;
    }

    private async Task<OperationResult> EnsureMcpPackageInstalledAsync(
        string projectPath,
        Action<string> log)
    {
        if (!UnityEditorPathService.TryResolveEditorForProject(projectPath, out var unityPath, out var unityVersion, out var editorResolveError))
        {
            return OperationResult.Fail(editorResolveError ?? "failed to resolve Unity editor for MCP package installation");
        }

        if (!UnityEditorPathService.TrySetDefaultEditorPath(unityPath, out var defaultEditorSaveError))
        {
            log($"[yellow]config[/]: unable to persist default editor path ({Markup.Escape(defaultEditorSaveError ?? "unknown error")})");
        }

        Environment.SetEnvironmentVariable("UNITY_PATH", unityPath);

        var runtimeDir = Path.Combine(projectPath, ".unifocl", "runtime");
        Directory.CreateDirectory(runtimeDir);
        var statusPath = Path.Combine(runtimeDir, "mcp-install-status.json");
        var unityLogPath = Path.Combine(runtimeDir, "mcp-install-unity.log");
        TryDeleteFile(statusPath);
        TryDeleteFile(unityLogPath);

        var processStart = new ProcessStartInfo(unityPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        processStart.ArgumentList.Add("-projectPath");
        processStart.ArgumentList.Add(projectPath);
        processStart.ArgumentList.Add("-batchmode");
        processStart.ArgumentList.Add("-nographics");
        processStart.ArgumentList.Add("-vcsMode");
        processStart.ArgumentList.Add("None");
        processStart.ArgumentList.Add("-logFile");
        processStart.ArgumentList.Add(unityLogPath);
        processStart.ArgumentList.Add("-executeMethod");
        processStart.ArgumentList.Add("UniFocl.EditorBridge.CLIDaemon.InstallRequiredMcpPackageBatch");
        processStart.ArgumentList.Add("--upm-install-package");
        processStart.ArgumentList.Add(RequiredMcpPackageId);
        processStart.ArgumentList.Add("--upm-install-status-file");
        processStart.ArgumentList.Add(statusPath);

        var startedAt = DateTime.UtcNow;
        var lastStageSignature = string.Empty;
        try
        {
            using var process = Process.Start(processStart);
            if (process is null)
            {
                return OperationResult.Fail("failed to start Unity batch process for MCP package installation");
            }

            log($"[grey]init[/]: launched Unity batch installer pid=[white]{process.Id}[/] version=[white]{Markup.Escape(unityVersion)}[/]");

            while (!process.HasExited)
            {
                if (DateTime.UtcNow - startedAt > McpBatchInstallTimeout)
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch
                    {
                    }

                    var timeoutDetail = TryReadUpmBatchInstallStatus(statusPath, out var timeoutStatus)
                        ? $"lastStage={timeoutStatus!.Stage} message={timeoutStatus.Message}"
                        : "status file unavailable";
                    return OperationResult.Fail(
                        $"Unity batch MCP install timed out after {(int)McpBatchInstallTimeout.TotalSeconds}s ({timeoutDetail})");
                }

                if (TryReadUpmBatchInstallStatus(statusPath, out var status))
                {
                    var signature = $"{status!.Stage}|{status.Message}";
                    if (!signature.Equals(lastStageSignature, StringComparison.Ordinal))
                    {
                        lastStageSignature = signature;
                        log($"[grey]init[/]: MCP install status stage=[white]{Markup.Escape(status.Stage)}[/] detail=[white]{Markup.Escape(status.Message)}[/]");
                    }
                }

                await Task.Delay(500);
            }

            var exitCode = process.ExitCode;
            var elapsedSeconds = (DateTime.UtcNow - startedAt).TotalSeconds;
            log($"[grey]init[/]: Unity batch installer exited pid=[white]{process.Id}[/] code=[white]{exitCode}[/] elapsed=[white]{elapsedSeconds:0.0}s[/]");

            if (!TryReadUpmBatchInstallStatus(statusPath, out var finalStatus))
            {
                var unityLogTail = ReadTailLines(unityLogPath, 20);
                return OperationResult.Fail(
                    $"MCP install status file was not produced by Unity batch process (exit={exitCode}, logTail={unityLogTail})");
            }

            var final = finalStatus!;
            var manifestHasPackage = ManifestContainsPackage(projectPath, RequiredMcpPackageId);
            if (exitCode != 0 || !final.Success || !manifestHasPackage)
            {
                var unityLogTail = ReadTailLines(unityLogPath, 20);
                return OperationResult.Fail(
                    $"MCP install failed (exit={exitCode}, stage={final.Stage}, message={final.Message}, manifestHasPackage={manifestHasPackage}, logTail={unityLogTail})");
            }

            log($"[green]init[/]: installed required package [white]{Markup.Escape(RequiredMcpPackageId)}[/]");
            return OperationResult.Success();
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"failed to run Unity batch MCP install ({ex.Message})");
        }
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
        return await RunWithStatusAsync(
            "Opening recent project...",
            () => TryOpenProjectAsync(
                selectedEntry.ProjectPath,
                session,
                daemonControlService,
                daemonRuntime,
                _editorDependencyInitializerService,
                promptForInitialization: true,
                allowUnsafe: allowUnsafe,
                log: log));
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
        log($"[grey]status[/]: attached-port={(session.AttachedPort is int port ? port : "none")}");
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

    private static Task<bool> HandleUpdateAsync(Action<string> log)
    {
        log($"[grey]update[/]: installed version -> [white]{Markup.Escape(CliVersion.SemVer)}[/]");
        log("[grey]update[/]: automatic updater is not wired in this CLI build");
        log("[grey]update[/]: update by pulling latest source and rebuilding the binary");
        return Task.FromResult(true);
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
        if (session.AttachedPort is int attachedBuildPort)
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
        if (session.AttachedPort is int attachedPort)
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
        bool allowUnsafe,
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

        var hasProtocolMismatch = TryGetProjectBridgeProtocol(projectPath, out var configuredProtocol)
                                  && !string.Equals(configuredProtocol, CliVersion.Protocol, StringComparison.Ordinal);

        log("[grey]open[/]: step 2/5 validate Unity project layout");
        var bridgeResult = EnsureProjectLocalConfig(projectPath);
        if (!bridgeResult.Ok)
        {
            log($"[red]error[/]: {Markup.Escape(bridgeResult.Error)}");
            return false;
        }

        var packageFixResult = EnsureRequiredUnityPackageReferences(projectPath);
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

        var started = await daemonControlService.EnsureProjectDaemonAsync(
            projectPath,
            daemonRuntime,
            session,
            log,
            requireBridgeMode: true,
            // Prefer attaching to live editor Bridge mode when available; Host mode remains fallback.
            preferHostMode: false,
            allowUnsafe: allowUnsafe);
        if (!started)
        {
            HandleDaemonStartupFailure(projectPath, session, daemonControlService, log);
            log("[red]open[/]: daemon is not stable; open aborted before entering project UI");
            return false;
        }

        var stableReservation = await daemonControlService.HasStableProjectDaemonAsync(projectPath, daemonRuntime, session);
        if (!stableReservation)
        {
            session.AttachedPort = null;
            log("[red]daemon[/]: daemon reservation check failed (missing attachment or project endpoint responsiveness)");
            log("[red]open[/]: open aborted before entering project UI");
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
        _projectViewService.OpenInitialView(session);
        return true;
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
        session.AttachedPort = null;
        log("[yellow]open[/]: compile errors blocked daemon startup; fix errors and retry /open");
        return false;
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
        out string error)
    {
        projectPath = string.Empty;
        allowUnsafe = false;
        error = string.Empty;

        foreach (var arg in args)
        {
            if (arg.Equals("--allow-unsafe", StringComparison.Ordinal))
            {
                allowUnsafe = true;
                continue;
            }

            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                error = $"unrecognized option {arg}; usage /open <path> [--allow-unsafe]";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(projectPath))
            {
                error = "too many positional arguments; usage /open <path> [--allow-unsafe]";
                return false;
            }

            projectPath = arg;
        }

        if (string.IsNullOrWhiteSpace(projectPath))
        {
            error = "usage /open <path> [--allow-unsafe]";
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
                        ["com.unity.collab-proxy"] = "2.7.2",
                        ["com.unity.ide.rider"] = "3.0.35",
                        ["com.unity.ide.visualstudio"] = "2.0.24",
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

    private static OperationResult EnsureRequiredUnityPackageReferences(string projectPath)
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
                    if (dep.Value.ValueKind == JsonValueKind.String)
                    {
                        var version = dep.Value.GetString();
                        if (!string.IsNullOrWhiteSpace(version))
                        {
                            dependencies[dep.Name] = version;
                        }
                    }
                }
            }

            var requiredPackages = InferRequiredUnityPackages(projectPath);
            requiredPackages[RequiredMcpPackageId] = RequiredMcpPackageTarget;
            var changed = false;
            foreach (var required in requiredPackages)
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

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static bool TryReadUpmBatchInstallStatus(string statusPath, out UpmBatchInstallStatusRecord? status)
    {
        status = null;
        if (!File.Exists(statusPath))
        {
            return false;
        }

        try
        {
            var raw = File.ReadAllText(statusPath);
            status = JsonSerializer.Deserialize<UpmBatchInstallStatusRecord>(
                raw,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return status is not null;
        }
        catch
        {
            return false;
        }
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

    private static string ReadTailLines(string path, int maxLines)
    {
        try
        {
            if (!File.Exists(path))
            {
                return "log unavailable";
            }

            var lines = File.ReadLines(path)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .TakeLast(Math.Max(1, maxLines))
                .ToArray();
            if (lines.Length == 0)
            {
                return "log empty";
            }

            var joined = string.Join(" | ", lines);
            if (joined.Length > 1000)
            {
                return joined[..1000] + "...";
            }

            return joined;
        }
        catch (Exception ex)
        {
            return $"log read failed: {ex.Message}";
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

    private sealed class CliConfig
    {
        public string? Theme { get; set; }
        public string? UnityProjectPath { get; set; }
        public int? RecentPruneStaleDays { get; set; }
    }

    private sealed record UpmBatchInstallStatusRecord(
        int Pid,
        string PackageId,
        string Stage,
        bool Success,
        string Message,
        string Detail);
}
