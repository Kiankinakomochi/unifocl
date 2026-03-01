using Spectre.Console;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

internal sealed class ProjectLifecycleService
{
    private readonly EditorDependencyInitializerService _editorDependencyInitializerService = new();
    private readonly ProjectViewService _projectViewService = new();
    private readonly RecentProjectHistoryService _recentProjectHistoryService = new();

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
            "/unity detect" => await HandleUnityDetectAsync(log),
            "/unity set" => await HandleUnitySetAsync(input, matched, log),
            "/close" => await HandleCloseAsync(session, daemonControlService, daemonRuntime, log),
            "/init" => await HandleInitAsync(input, matched, session, log),
            "/config" => await HandleConfigAsync(input, matched, log),
            _ => false
        };
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
            selectedEditor = AnsiConsole.Prompt(
                new SelectionPrompt<UnityEditorPathService.UnityEditorInstallation>()
                    .Title("Choose Unity editor version")
                    .PageSize(Math.Min(availableEditors.Count, 12))
                    .UseConverter(editor => $"{editor.Version} ({editor.EditorPath})")
                    .AddChoices(availableEditors));
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
        var gitVersion = RunProcess("git", "--version", Directory.GetCurrentDirectory());
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

        var cloneResult = RunProcess("git", $"clone \"{gitUrl}\" \"{targetPath}\"", Directory.GetCurrentDirectory());
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

    private Task<bool> HandleInitAsync(
        string input,
        CommandSpec matched,
        CliSessionState session,
        Action<string> log)
    {
        var args = ParseCommandArgs(input, matched.Trigger);
        if (args.Count > 1)
        {
            log("[red]error[/]: usage /init <path-to-project?>");
            return Task.FromResult(true);
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
            return Task.FromResult(true);
        }

        var configResult = EnsureProjectLocalConfig(targetPath);
        if (!configResult.Ok)
        {
            log($"[red]error[/]: {Markup.Escape(configResult.Error)}");
            return Task.FromResult(true);
        }

        var packageFixResult = EnsureRequiredUnityPackageReferences(targetPath);
        if (!packageFixResult.Ok)
        {
            log($"[red]error[/]: {Markup.Escape(packageFixResult.Error)}");
            return Task.FromResult(true);
        }

        var initResult = _editorDependencyInitializerService.InitializeProject(targetPath, log);
        if (!initResult.Ok)
        {
            log($"[red]error[/]: {Markup.Escape(initResult.Error)}");
            return Task.FromResult(true);
        }

        log($"[green]init[/]: ready at [white]{Markup.Escape(targetPath)}[/]");
        return Task.FromResult(true);
    }

    private Task<bool> HandleRecentAsync(
        string input,
        CommandSpec matched,
        CliSessionState session,
        DaemonControlService daemonControlService,
        DaemonRuntime daemonRuntime,
        Action<string> log)
    {
        var args = ParseCommandArgs(input, matched.Trigger);
        if (!TryParseRecentArgs(args, out var indexRaw, out var allowUnsafe, out var parseError))
        {
            log($"[red]error[/]: {Markup.Escape(parseError)}");
            return Task.FromResult(true);
        }

        if (!_recentProjectHistoryService.TryGetRecentProjects(100, out var entries, out var historyError))
        {
            log($"[red]error[/]: {Markup.Escape(historyError ?? "failed to load recent projects")}");
            return Task.FromResult(true);
        }

        if (entries.Count == 0)
        {
            log("[grey]recent[/]: no recent projects found");
            return Task.FromResult(true);
        }

        LogRecentEntries(entries, log);

        if (!string.IsNullOrWhiteSpace(indexRaw))
        {
            if (!int.TryParse(indexRaw, out var idx) || idx <= 0)
            {
                log("[red]error[/]: idx must be a positive integer");
                return Task.FromResult(true);
            }

            if (idx > entries.Count)
            {
                log($"[red]error[/]: idx {idx} is out of range (1-{entries.Count})");
                return Task.FromResult(true);
            }

            var selectedEntry = entries[idx - 1];
            var confirmMessage = $"Open recent project [white]{idx}[/]: [white]{Markup.Escape(selectedEntry.ProjectPath)}[/]?";
            if (!AnsiConsole.Confirm(confirmMessage, defaultValue: true))
            {
                log("[grey]recent[/]: cancelled");
                return Task.FromResult(true);
            }

            return OpenRecentSelectionAsync(selectedEntry, session, daemonControlService, daemonRuntime, allowUnsafe, log);
        }

        if (Console.IsInputRedirected)
        {
            log("[yellow]recent[/]: interactive selection requires a TTY; use /recent <idx> to open a project");
            return Task.FromResult(true);
        }

        var selected = AnsiConsole.Prompt(
            new SelectionPrompt<RecentProjectEntry>()
                .Title("Select a recent project to open")
                .PageSize(Math.Min(entries.Count, 10))
                .UseConverter(entry =>
                {
                    var index = entries.IndexOf(entry) + 1;
                    var opened = entry.LastOpenedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz");
                    return $"{index}. {entry.ProjectPath} ({opened})";
                })
                .AddChoices(entries));

        return OpenRecentSelectionAsync(selected, session, daemonControlService, daemonRuntime, allowUnsafe, log);
    }

    private static void LogRecentEntries(IReadOnlyList<RecentProjectEntry> entries, Action<string> log)
    {
        log("[grey]recent[/]: most recently opened projects");
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var opened = entry.LastOpenedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz");
            log($"[grey]recent[/]: [white]{i + 1}[/]. [white]{Markup.Escape(entry.ProjectPath)}[/] [dim]({opened})[/]");
        }
    }

    private Task<bool> OpenRecentSelectionAsync(
        RecentProjectEntry selectedEntry,
        CliSessionState session,
        DaemonControlService daemonControlService,
        DaemonRuntime daemonRuntime,
        bool allowUnsafe,
        Action<string> log)
    {
        log($"[grey]recent[/]: opening [white]{Markup.Escape(selectedEntry.ProjectPath)}[/]");
        return TryOpenProjectAsync(
            selectedEntry.ProjectPath,
            session,
            daemonControlService,
            daemonRuntime,
            _editorDependencyInitializerService,
            promptForInitialization: true,
            allowUnsafe: allowUnsafe,
            log: log);
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

    private Task<bool> HandleConfigAsync(
        string input,
        CommandSpec matched,
        Action<string> log)
    {
        var args = ParseCommandArgs(input, matched.Trigger);
        if (args.Count == 0)
        {
            log("[red]error[/]: usage /config <get|set|list|reset> <theme?> <value?>");
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
        log("[grey]config[/]: available settings");
        log($"[grey]config[/]: [white]theme[/] = [white]{theme}[/] [dim](dark|light, source: {source})[/]");
        return Task.FromResult(true);
    }

    private static Task<bool> HandleConfigGet(IReadOnlyList<string> args, Action<string> log)
    {
        if (args.Count != 1)
        {
            log("[red]error[/]: usage /config get <theme>");
            return Task.FromResult(true);
        }

        if (!IsThemeKey(args[0]))
        {
            log("[red]error[/]: only 'theme' is supported");
            return Task.FromResult(true);
        }

        var loadResult = TryLoadCliConfig(out var config, out var error);
        if (!loadResult)
        {
            log($"[red]error[/]: {Markup.Escape(error ?? "failed to read config")}");
            return Task.FromResult(true);
        }

        var theme = ResolveEffectiveTheme(config);
        log($"[grey]config[/]: [white]theme[/] = [white]{theme}[/]");
        return Task.FromResult(true);
    }

    private static Task<bool> HandleConfigSet(IReadOnlyList<string> args, Action<string> log)
    {
        if (args.Count != 2)
        {
            log("[red]error[/]: usage /config set <theme> <dark|light>");
            return Task.FromResult(true);
        }

        if (!IsThemeKey(args[0]))
        {
            log("[red]error[/]: only 'theme' is supported");
            return Task.FromResult(true);
        }

        var requestedTheme = args[1].Trim().ToLowerInvariant();
        if (requestedTheme is not ("dark" or "light"))
        {
            log("[red]error[/]: theme must be 'dark' or 'light'");
            return Task.FromResult(true);
        }

        if (!TryLoadCliConfig(out var config, out var loadError))
        {
            log($"[red]error[/]: {Markup.Escape(loadError ?? "failed to read config")}");
            return Task.FromResult(true);
        }

        config.Theme = requestedTheme;
        if (!TrySaveCliConfig(config, out var saveError))
        {
            log($"[red]error[/]: {Markup.Escape(saveError ?? "failed to write config")}");
            return Task.FromResult(true);
        }

        CliTheme.TrySetTheme(requestedTheme);
        log($"[green]config[/]: theme set to [white]{requestedTheme}[/]");
        return Task.FromResult(true);
    }

    private static Task<bool> HandleConfigReset(IReadOnlyList<string> args, Action<string> log)
    {
        if (args.Count > 1)
        {
            log("[red]error[/]: usage /config reset <theme?>");
            return Task.FromResult(true);
        }

        if (args.Count == 1 && !IsThemeKey(args[0]))
        {
            log("[red]error[/]: only 'theme' is supported");
            return Task.FromResult(true);
        }

        if (!TryLoadCliConfig(out var config, out var loadError))
        {
            log($"[red]error[/]: {Markup.Escape(loadError ?? "failed to read config")}");
            return Task.FromResult(true);
        }

        config.Theme = null;
        if (!TrySaveCliConfig(config, out var saveError))
        {
            log($"[red]error[/]: {Markup.Escape(saveError ?? "failed to write config")}");
            return Task.FromResult(true);
        }

        var effective = ResolveEffectiveTheme(config);
        CliTheme.TrySetTheme(effective);
        log($"[green]config[/]: theme reset to default [white]{effective}[/]");
        return Task.FromResult(true);
    }

    private static bool LogConfigUsage(Action<string> log)
    {
        log("[red]error[/]: usage /config <get|set|list|reset> <theme?> <value?>");
        return true;
    }

    private async Task<bool> HandleCloseAsync(
        CliSessionState session,
        DaemonControlService daemonControlService,
        DaemonRuntime daemonRuntime,
        Action<string> log)
    {
        var candidatePorts = new HashSet<int>();
        if (session.AttachedPort is int attachedPort)
        {
            candidatePorts.Add(attachedPort);
        }

        if (!string.IsNullOrWhiteSpace(session.CurrentProjectPath))
        {
            candidatePorts.Add(DaemonControlService.ComputeProjectDaemonPort(session.CurrentProjectPath));
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
            if (editorDependencyInitializerService.NeedsInitialization(projectPath, out var initReason))
            {
                log($"[yellow]init[/]: editor bridge dependency is missing or invalid ({Markup.Escape(initReason)}).");
                if (editorDependencyInitializerService.PromptForInitialization(log))
                {
                    var initResult = editorDependencyInitializerService.InitializeProject(projectPath, log);
                    if (!initResult.Ok)
                    {
                        log($"[red]error[/]: {Markup.Escape(initResult.Error)}");
                        return false;
                    }
                }
                else
                {
                    log("[yellow]init[/]: skipped; run /init to enable editor-side bridge package");
                }
            }
            else
            {
                log("[grey]init[/]: editor bridge dependency already installed");
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

        log("[grey]open[/]: step 4/5 route runtime by active Unity client lock");
        var daemonPort = DaemonControlService.ComputeProjectDaemonPort(projectPath);
        if (DaemonControlService.IsUnityClientActiveForProject(projectPath))
        {
            var attached = await daemonControlService.TryAttachProjectDaemonAsync(
                projectPath,
                session,
                log: null,
                attemptCount: 32,
                attemptDelayMs: 250);
            if (attached && session.AttachedPort == daemonPort)
            {
                SaveDaemonSession(projectPath, new DaemonSessionInfo(daemonPort, DateTimeOffset.UtcNow, false));
                log($"[grey]daemon[/]: Unity editor lock detected; attached bridge on [white]127.0.0.1:{daemonPort}[/]");
            }
            else
            {
                log($"[red]daemon[/]: Unity editor lock detected, but bridge endpoint [white]127.0.0.1:{daemonPort}[/] is not responding");
                log("[yellow]daemon[/]: wait for Unity editor script compilation/domain reload, then retry /open");
                return false;
            }
        }
        else
        {
            if (allowUnsafe)
            {
                log("[yellow]open[/]: --allow-unsafe enabled (using -noUpm and -ignoreCompileErrors for faster headless boot)");
            }

            var started = await daemonControlService.EnsureProjectDaemonAsync(
                projectPath,
                daemonRuntime,
                session,
                log,
                requireUnityBridge: true,
                preferHeadless: true,
                allowUnsafe: allowUnsafe);
            if (!started)
            {
                if (!HandleDaemonStartupFailure(projectPath, session, daemonControlService, log))
                {
                    return false;
                }
            }
            else
            {
                SaveDaemonSession(projectPath, new DaemonSessionInfo(daemonPort, DateTimeOffset.UtcNow, true));
                log($"[grey]daemon[/]: started headless daemon on [white]127.0.0.1:{daemonPort}[/]");
                session.SafeModeEnabled = false;
                session.LastCompileError = null;
            }
        }

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
            log("[red]daemon[/]: failed to start or attach headless daemon");
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

        var canEnterSafeMode = CanEnterSafeMode(projectPath);
        if (Console.IsInputRedirected)
        {
            log("[red]daemon[/]: redirected input cannot answer compile-error prompt; aborting open");
            return false;
        }

        var options = new List<string>
        {
            "Ignore and continue",
            "Quit open"
        };
        if (canEnterSafeMode)
        {
            options.Insert(0, "Enter Safe mode");
        }

        var selected = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Daemon startup failed due to compile errors. Choose action:")
                .PageSize(options.Count)
                .AddChoices(options));

        if (selected.Equals("Quit open", StringComparison.Ordinal))
        {
            log("[yellow]open[/]: cancelled due to compile errors");
            return false;
        }

        if (selected.Equals("Enter Safe mode", StringComparison.Ordinal))
        {
            session.SafeModeEnabled = true;
            session.AttachedPort = null;
            log("[yellow]open[/]: entered safe mode (daemon unavailable; Unity bridge commands are limited)");
            return true;
        }

        session.SafeModeEnabled = false;
        session.AttachedPort = null;
        log("[yellow]open[/]: continuing without daemon attachment; Unity bridge commands may fail until compile errors are fixed");
        return true;
    }

    private static bool CanEnterSafeMode(string projectPath)
    {
        return Directory.Exists(projectPath)
               && Directory.Exists(Path.Combine(projectPath, "Assets"))
               && Directory.Exists(Path.Combine(projectPath, "ProjectSettings"));
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
        out string error)
    {
        indexRaw = null;
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
                error = $"unrecognized option {arg}; usage /recent [idx] [--allow-unsafe]";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(indexRaw))
            {
                error = "too many positional arguments; usage /recent [idx] [--allow-unsafe]";
                return false;
            }

            indexRaw = arg.Trim();
        }

        return true;
    }

    private static bool IsAllowUnsafeFlag(string arg)
    {
        return arg.Equals("--allow-unsafe", StringComparison.Ordinal)
               || arg.Equals("--alow-unsafe", StringComparison.Ordinal);
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
                daemon = new { host = "127.0.0.1", port = DaemonControlService.ComputeProjectDaemonPort(projectPath) },
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

    private static ProcessResult RunProcess(string fileName, string arguments, string workingDirectory)
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

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
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
                new Dictionary<string, string?>
                {
                    ["theme"] = NormalizeTheme(config.Theme),
                    ["unityProjectPath"] = NormalizeProjectPath(config.UnityProjectPath)
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

    private static bool IsThemeKey(string key)
    {
        return key.Equals("theme", StringComparison.OrdinalIgnoreCase)
               || key.Equals("ui.theme", StringComparison.OrdinalIgnoreCase);
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
    }
}
