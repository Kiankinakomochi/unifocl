using Spectre.Console;
using System.Diagnostics;

var appCancellation = new CancellationTokenSource();
var cancellationRefCount = 0;
var cancellationDisposeRequested = 0;

bool TryAcquireCancellationReference()
{
    while (true)
    {
        if (Volatile.Read(ref cancellationDisposeRequested) != 0)
        {
            return false;
        }

        Interlocked.Increment(ref cancellationRefCount);
        if (Volatile.Read(ref cancellationDisposeRequested) == 0)
        {
            return true;
        }

        Interlocked.Decrement(ref cancellationRefCount);
    }
}

void ReleaseCancellationReference()
{
    Interlocked.Decrement(ref cancellationRefCount);
}

static string ResolveRuntimePath()
{
    // Prefer a runtime dir beside the CWD, but fall back to the user home directory
    // when CWD is read-only (e.g. / in container/MCP environments).
    var cwdCandidate = Path.Combine(Environment.CurrentDirectory, ".unifocl-runtime");
    try
    {
        Directory.CreateDirectory(cwdCandidate);
        return cwdCandidate;
    }
    catch (IOException) { }
    catch (UnauthorizedAccessException) { }

    return Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".unifocl-runtime");
}

void TryCancelApplication()
{
    if (!TryAcquireCancellationReference())
    {
        return;
    }

    try
    {
        if (!appCancellation.IsCancellationRequested)
        {
            appCancellation.Cancel();
        }
    }
    finally
    {
        ReleaseCancellationReference();
    }
}

ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    TryCancelApplication();
};
EventHandler processExitHandler = (_, _) => TryCancelApplication();
Console.CancelKeyPress += cancelHandler;
AppDomain.CurrentDomain.ProcessExit += processExitHandler;

static async Task AwaitWithCancellationAsync(Func<Task> operation, CancellationToken cancellationToken)
{
    await operation().WaitAsync(cancellationToken);
}

try
{
    var launchArgs = Environment.GetCommandLineArgs().Skip(1).ToArray();
    if (launchArgs.Length == 1
        && (launchArgs[0].Equals("--version", StringComparison.OrdinalIgnoreCase)
            || launchArgs[0].Equals("-v", StringComparison.OrdinalIgnoreCase)))
    {
        Console.WriteLine(CliVersion.SemVer);
        return;
    }

    if (UnifoclMcpServerMode.IsRequested(launchArgs))
    {
        await UnifoclMcpServerMode.RunAsync(launchArgs, appCancellation.Token);
        return;
    }

    if (BuildLogTailService.TryRunFromArgs(launchArgs))
    {
        return;
    }

    if (DaemonControlService.TryParseDaemonServiceArgs(launchArgs, out var serviceOptions, out var daemonParseError))
    {
        if (daemonParseError is not null)
        {
            CliTheme.MarkupLine($"[red]{Markup.Escape(daemonParseError)}[/]");
            Environment.ExitCode = 2;
            return;
        }

        try
        {
            await DaemonControlService.RunDaemonServiceAsync(serviceOptions!, appCancellation.Token);
        }
        catch (OperationCanceledException) when (appCancellation.IsCancellationRequested)
        {
        }
        return;
    }

    var commands = CliCommandCatalog.CreateRootCommands();
    var projectCommands = CliCommandCatalog.CreateProjectCommands();
    var inspectorCommands = CliCommandCatalog.CreateInspectorCommands();

    var streamLog = new List<string>();
    var runtimePath = ResolveRuntimePath();
    var daemonRuntime = new DaemonRuntime(runtimePath);
    var session = new CliSessionState();
    var daemonControlService = new DaemonControlService();
    var projectLifecycleService = new ProjectLifecycleService();
    var projectCommandRouterService = new ProjectCommandRouterService();
    var hierarchyTui = new HierarchyTui();
    var buildCommandService = new BuildCommandService();
    if (launchArgs.Length > 0 && launchArgs[0].Equals("agent", StringComparison.OrdinalIgnoreCase))
    {
        if (CliCommandParsingService.TryParseAgentInstallCommandText(launchArgs, out var agentInstallCommandText, out var agentInstallError))
        {
            if (!string.IsNullOrWhiteSpace(agentInstallError))
            {
                CliTheme.MarkupLine($"[red]{Markup.Escape(agentInstallError)}[/]");
                Environment.ExitCode = 2;
                return;
            }

            var matched = CliCommandParsingService.MatchCommand(agentInstallCommandText!, commands);
            if (matched is null)
            {
                CliTheme.MarkupLine("[red]error[/]: internal command routing failed for /agent install");
                Environment.ExitCode = 2;
                return;
            }

            var handled = await projectLifecycleService.TryHandleLifecycleCommandAsync(
                agentInstallCommandText!,
                matched,
                session,
                daemonControlService,
                daemonRuntime,
                line => CliTheme.MarkupLine(line));
            Environment.ExitCode = handled ? 0 : 2;
            return;
        }

        if (CliCommandParsingService.TryParseAgentSetupArgs(launchArgs, out var setupPath, out var setupDryRun, out var setupError))
        {
            if (!string.IsNullOrWhiteSpace(setupError))
            {
                CliTheme.MarkupLine($"[red]{Markup.Escape(setupError!)}[/]");
                Environment.ExitCode = 2;
                return;
            }

            var setupOk = await projectLifecycleService.HandleAgentSetupAsync(setupPath, setupDryRun, AgentSetupTarget.Both, line => CliTheme.MarkupLine(line));
            Environment.ExitCode = setupOk ? 0 : 1;
            return;
        }

        CliTheme.MarkupLine("[red]error[/]: unknown agent subcommand");
        CliTheme.MarkupLine("[grey]      [/]: unifocl agent install <codex|claude> [--workspace <path>] [--server-name <name>] [--config-root <path>] [--dry-run]");
        CliTheme.MarkupLine("[grey]      [/]: unifocl agent setup [path-to-unity-project] [--dry-run]");
        Environment.ExitCode = 2;
        return;
    }

    if (CliCommandParsingService.TryParseQuickLifecycleCommandText(launchArgs, out var quickLifecycleCommandText, out var quickLifecycleError))
    {
        if (!string.IsNullOrWhiteSpace(quickLifecycleError))
        {
            CliTheme.MarkupLine($"[red]{Markup.Escape(quickLifecycleError)}[/]");
            Environment.ExitCode = 2;
            return;
        }

        if (!string.Equals(quickLifecycleCommandText, "/update", StringComparison.Ordinal))
        {
            CliTheme.MarkupLine("[red]error[/]: unsupported quick lifecycle command");
            Environment.ExitCode = 2;
            return;
        }

        var succeeded = await projectLifecycleService.RunQuickUpdateAsync(line => CliTheme.MarkupLine(line));
        Environment.ExitCode = succeeded ? 0 : 1;
        return;
    }

    var validateCommandService = new ValidateCommandService();
    var diagCommandService = new DiagCommandService();
    var testCommandService = new TestCommandService();
    var tagLayerCommandService = new TagLayerCommandService();
    var runtimeCommandService = new RuntimeCommandService();
    if (CliCommandParsingService.TryParseExecLaunchOptions(launchArgs, out var execOptions, out var execError))
    {
        if (!string.IsNullOrWhiteSpace(execError))
        {
            CliTheme.MarkupLine($"[red]{Markup.Escape(execError)}[/]");
            Environment.ExitCode = 2;
            return;
        }

        CliRuntimeState.SuppressConsoleOutput = execOptions!.Agentic;
        AgenticResponseEnvelope envelope;
        try
        {
            envelope = await CliOneShotExecutionService.ExecuteOneShotCommandAsync(
                execOptions,
                commands,
                streamLog,
                session,
                daemonControlService,
                daemonRuntime,
                projectLifecycleService,
                projectCommandRouterService,
                hierarchyTui,
                buildCommandService,
                validateCommandService,
                diagCommandService,
                testCommandService,
                tagLayerCommandService,
                runtimeCommandService,
                appCancellation.Token).WaitAsync(appCancellation.Token);
        }
        catch (OperationCanceledException) when (appCancellation.IsCancellationRequested)
        {
            try
            {
                await projectLifecycleService.PerformSafeExitCleanupAsync(
                    session,
                    daemonControlService,
                    daemonRuntime,
                    _ => { });
            }
            catch
            {
            }

            var canceled = new AgenticResponseEnvelope(
                "error",
                string.IsNullOrWhiteSpace(execOptions!.RequestId) ? Guid.NewGuid().ToString("N") : execOptions.RequestId!,
                "none",
                "exec",
                null,
                [new AgenticError("E_CANCELED", "execution canceled")],
                [],
                new AgenticMeta("agentic.v1", CliVersion.Protocol, 130, DateTime.UtcNow.ToString("O")));
            var canceledPayload = AgenticFormatter.SerializeEnvelope(canceled, execOptions.Format);
            Console.Out.WriteLine(canceledPayload);
            Environment.ExitCode = 130;
            return;
        }
        var payload = AgenticFormatter.SerializeEnvelope(envelope, execOptions.Format);
        Console.Out.WriteLine(payload);
        Environment.ExitCode = envelope.Meta.ExitCode;
        return;
    }

    CliBootLogo.SeedBootLog(streamLog);
    CliLogService.RenderInitialLog(streamLog);

    if (ProjectLifecycleService.FindAgentSetupRoot(Environment.CurrentDirectory) is null
        && !appCancellation.IsCancellationRequested)
    {
        try
        {
            const string ChoiceYesCwd = "Yes \u2014 set up for this directory";
            const string ChoiceYesPath = "Yes \u2014 specify a path";
            const string ChoiceNo = "No \u2014 skip for now";

            var choice = CliTheme.PromptWithDividers(() =>
                AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("[yellow]setup[/]: no agent MCP integration found in this directory tree. Configure now?")
                        .HighlightStyle(CliTheme.SelectionHighlightStyle)
                        .AddChoices(ChoiceYesCwd, ChoiceYesPath, ChoiceNo)));

            if (choice != ChoiceNo)
            {
                string? setupPath = null;
                if (choice == ChoiceYesPath)
                {
                    setupPath = CliTheme.PromptWithDividers(() =>
                        AnsiConsole.Prompt(
                            new TextPrompt<string>("[grey]setup[/]: path to Unity project:")
                                .PromptStyle(CliTheme.SelectionHighlightStyle)));
                }

                const string TargetBoth = "Both (Claude Code + Codex)";
                const string TargetClaude = "Claude Code only";
                const string TargetCodex = "Codex only";

                var agentChoice = CliTheme.PromptWithDividers(() =>
                    AnsiConsole.Prompt(
                        new SelectionPrompt<string>()
                            .Title("[grey]setup[/]: which agent integration to configure?")
                            .HighlightStyle(CliTheme.SelectionHighlightStyle)
                            .AddChoices(TargetBoth, TargetClaude, TargetCodex)));

                var target = agentChoice switch
                {
                    TargetClaude => AgentSetupTarget.Claude,
                    TargetCodex => AgentSetupTarget.Codex,
                    _ => AgentSetupTarget.Both,
                };

                await projectLifecycleService.HandleAgentSetupAsync(
                    setupPath,
                    false,
                    target,
                    line => CliLogService.AppendLog(streamLog, line));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Non-interactive terminal or prompt interrupted — continue to main loop
        }
    }

    while (true)
    {
        if (appCancellation.IsCancellationRequested)
        {
            break;
        }

        string? rawInput;
        try
        {
            rawInput = CliComposerService.ReadInput(commands, projectCommands, inspectorCommands, streamLog, session);
        }
        catch (Exception ex)
        {
            CliLogService.LogUnhandledException(streamLog, ex, "input");
            continue;
        }

        if (rawInput is null)
        {
            try
            {
                await AwaitWithCancellationAsync(
                    () => projectLifecycleService.PerformSafeExitCleanupAsync(
                        session,
                        daemonControlService,
                        daemonRuntime,
                        line => CliLogService.AppendLog(streamLog, line)),
                    appCancellation.Token);
            }
            catch (OperationCanceledException) when (appCancellation.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                CliLogService.LogUnhandledException(streamLog, ex, "shutdown");
            }

            CliTheme.MarkupLine("[grey]Input stream closed. Session ended.[/]");
            return;
        }

        try
        {
            var input = rawInput.Trim();
            if (string.IsNullOrWhiteSpace(input))
            {
                continue;
            }

        if (input.Equals(":focus-recent", StringComparison.OrdinalIgnoreCase))
        {
            await AwaitWithCancellationAsync(
                () => projectLifecycleService.TryHandleRecentSelectionToggleAsync(
                    session,
                    daemonControlService,
                    daemonRuntime,
                    line => CliLogService.AppendLog(streamLog, line)),
                appCancellation.Token);
            continue;
        }

        if (!input.StartsWith('/'))
        {
            if (CliCommandParsingService.TryNormalizeProjectBuildCommand(input, out var normalizedBuildInput))
            {
                await AwaitWithCancellationAsync(
                    () => buildCommandService.HandleBuildCommandAsync(
                        normalizedBuildInput,
                        session,
                        daemonControlService,
                        daemonRuntime,
                        line => CliLogService.AppendLog(streamLog, line)),
                    appCancellation.Token);
                continue;
            }

            if (CliCommandParsingService.IsTestCommand(input))
            {
                await AwaitWithCancellationAsync(
                    () => testCommandService.HandleTestCommandAsync(
                        input,
                        session,
                        line => CliLogService.AppendLog(streamLog, line),
                        appCancellation.Token),
                    appCancellation.Token);
                continue;
            }

            var handledProjectCommand = await projectCommandRouterService.TryHandleProjectCommandAsync(
                    input,
                    session,
                    daemonControlService,
                    daemonRuntime,
                    line => CliLogService.AppendLog(streamLog, line))
                .WaitAsync(appCancellation.Token);
            if (!handledProjectCommand)
            {
                CliLogService.AppendLog(streamLog, "[grey]system[/]: unknown project command; use / for command palette");
            }

            if (handledProjectCommand && session.AutoEnterHierarchyRequested)
            {
                session.AutoEnterHierarchyRequested = false;
                session.ContextMode = CliContextMode.Hierarchy;
                await AwaitWithCancellationAsync(
                    () => hierarchyTui.RunAsync(
                        session,
                        daemonControlService,
                        daemonRuntime,
                        line => CliLogService.AppendLog(streamLog, line),
                        async targetPath =>
                        {
                            var inspectInput = targetPath.Contains(' ', StringComparison.Ordinal)
                                ? $"inspect \"{targetPath}\""
                                : $"inspect {targetPath}";
                            await projectCommandRouterService.TryHandleProjectCommandAsync(
                                inspectInput,
                                session,
                                daemonControlService,
                                daemonRuntime,
                                line => CliLogService.AppendLog(streamLog, line));
                            if (session.Inspector is null)
                            {
                                return false;
                            }

                            session.ContextMode = CliContextMode.Inspector;
                            return true;
                        }),
                    appCancellation.Token);
                if (session.Mode == CliMode.Project)
                {
                    session.ContextMode = session.Inspector is null ? CliContextMode.Project : CliContextMode.Inspector;
                    if (session.ContextMode == CliContextMode.Project
                        && !string.IsNullOrWhiteSpace(session.CurrentProjectPath))
                    {
                        await projectCommandRouterService.TryHandleProjectCommandAsync(
                                string.Empty,
                                session,
                                daemonControlService,
                                daemonRuntime,
                                line => CliLogService.AppendLog(streamLog, line))
                            .WaitAsync(appCancellation.Token);
                    }
                }
            }

            continue;
        }

        if (input == "/")
        {
            continue;
        }

        input = CliCommandParsingService.NormalizeSlashCommand(input);
        var matched = CliCommandParsingService.MatchCommand(input, commands);
        if (matched is null)
        {
            CliLogService.AppendLog(streamLog, $"[grey]system[/]: unknown command [white]{Markup.Escape(input)}[/]");
            continue;
        }

        if (matched.Trigger == "/quit")
        {
            try
            {
                await projectLifecycleService.PerformSafeExitCleanupAsync(
                    session,
                    daemonControlService,
                    daemonRuntime,
                    line => CliLogService.AppendLog(streamLog, line));
            }
            catch (Exception ex)
            {
                CliLogService.LogUnhandledException(streamLog, ex, "shutdown");
            }

            CliTheme.MarkupLine("[grey]Session closed.[/]");
            return;
        }

        if (matched.Trigger == "/clear")
        {
            streamLog.Clear();
            CliComposerService.ResetBootLogoCollapsed();
            CliBootLogo.SeedBootLog(streamLog);
            AnsiConsole.Clear();
            CliLogService.RenderInitialLog(streamLog);
            continue;
        }

        if (matched.Trigger == "/hierarchy")
        {
            session.ContextMode = CliContextMode.Hierarchy;
            await AwaitWithCancellationAsync(
                () => hierarchyTui.RunAsync(
                    session,
                    daemonControlService,
                    daemonRuntime,
                    line => CliLogService.AppendLog(streamLog, line),
                    async targetPath =>
                    {
                        var inspectInput = targetPath.Contains(' ', StringComparison.Ordinal)
                            ? $"inspect \"{targetPath}\""
                            : $"inspect {targetPath}";
                        await projectCommandRouterService.TryHandleProjectCommandAsync(
                            inspectInput,
                            session,
                            daemonControlService,
                            daemonRuntime,
                            line => CliLogService.AppendLog(streamLog, line));
                        if (session.Inspector is null)
                        {
                            return false;
                        }

                        session.ContextMode = CliContextMode.Inspector;
                        return true;
                    }),
                appCancellation.Token);
            if (session.Mode == CliMode.Project)
            {
                session.ContextMode = session.Inspector is null ? CliContextMode.Project : CliContextMode.Inspector;
                if (session.ContextMode == CliContextMode.Project
                    && !string.IsNullOrWhiteSpace(session.CurrentProjectPath))
                {
                    await projectCommandRouterService.TryHandleProjectCommandAsync(
                            string.Empty,
                            session,
                            daemonControlService,
                            daemonRuntime,
                            line => CliLogService.AppendLog(streamLog, line))
                        .WaitAsync(appCancellation.Token);
                }
            }
            continue;
        }

        if (matched.Trigger == "/project")
        {
            if (session.Mode != CliMode.Project || string.IsNullOrWhiteSpace(session.CurrentProjectPath))
            {
                CliLogService.AppendLog(streamLog, "[yellow]mode[/]: open a project first with /open");
                continue;
            }

            session.ContextMode = CliContextMode.Project;
            session.Inspector = null;
            await projectCommandRouterService.TryHandleProjectCommandAsync(
                    string.Empty,
                    session,
                    daemonControlService,
                    daemonRuntime,
                    line => CliLogService.AppendLog(streamLog, line))
                .WaitAsync(appCancellation.Token);
            continue;
        }

        if (matched.Trigger == "/inspect")
        {
            if (session.Mode != CliMode.Project || string.IsNullOrWhiteSpace(session.CurrentProjectPath))
            {
                CliLogService.AppendLog(streamLog, "[yellow]mode[/]: open a project first with /open");
                continue;
            }

            var inspectInput = input.Length > "/inspect".Length
                ? $"inspect {input["/inspect".Length..].Trim()}"
                : "inspect";
            await projectCommandRouterService.TryHandleProjectCommandAsync(
                    inspectInput,
                    session,
                    daemonControlService,
                    daemonRuntime,
                    line => CliLogService.AppendLog(streamLog, line))
                .WaitAsync(appCancellation.Token);
            if (session.Inspector is not null)
            {
                session.ContextMode = CliContextMode.Inspector;
            }
            continue;
        }

        if (matched.Trigger.StartsWith("/upm", StringComparison.Ordinal))
        {
            if (session.Mode != CliMode.Project || string.IsNullOrWhiteSpace(session.CurrentProjectPath))
            {
                CliLogService.AppendLog(streamLog, "[yellow]mode[/]: open a project first with /open");
                continue;
            }

            var upmInput = input.Length > "/upm".Length
                ? $"upm {input["/upm".Length..].Trim()}"
                : "upm";
            var handled = await projectCommandRouterService.TryHandleProjectCommandAsync(
                    upmInput,
                    session,
                    daemonControlService,
                    daemonRuntime,
                    line => CliLogService.AppendLog(streamLog, line))
                .WaitAsync(appCancellation.Token);
            if (!handled)
            {
                CliLogService.AppendLog(streamLog, "[yellow]upm[/]: unsupported /upm command");
            }

            continue;
        }

        if (matched.Trigger.StartsWith("/build", StringComparison.Ordinal))
        {
            await AwaitWithCancellationAsync(
                () => buildCommandService.HandleBuildCommandAsync(
                    input,
                    session,
                    daemonControlService,
                    daemonRuntime,
                    line => CliLogService.AppendLog(streamLog, line)),
                appCancellation.Token);
            continue;
        }

        if (matched.Trigger.StartsWith("/validate", StringComparison.Ordinal) || matched.Trigger == "/val")
        {
            await AwaitWithCancellationAsync(
                () => validateCommandService.HandleValidateCommandAsync(
                    input,
                    session,
                    daemonControlService,
                    daemonRuntime,
                    line => CliLogService.AppendLog(streamLog, line)),
                appCancellation.Token);
            continue;
        }

        if (matched.Trigger.StartsWith("/diag", StringComparison.Ordinal))
        {
            await AwaitWithCancellationAsync(
                () => diagCommandService.HandleDiagCommandAsync(
                    input,
                    session,
                    daemonControlService,
                    daemonRuntime,
                    line => CliLogService.AppendLog(streamLog, line)),
                appCancellation.Token);
            continue;
        }

        if (matched.Trigger.StartsWith("/tag", StringComparison.Ordinal))
        {
            await AwaitWithCancellationAsync(
                () => tagLayerCommandService.HandleTagCommandAsync(
                    input,
                    session,
                    daemonControlService,
                    daemonRuntime,
                    line => CliLogService.AppendLog(streamLog, line)),
                appCancellation.Token);
            continue;
        }

        if (matched.Trigger.StartsWith("/layer", StringComparison.Ordinal))
        {
            await AwaitWithCancellationAsync(
                () => tagLayerCommandService.HandleLayerCommandAsync(
                    input,
                    session,
                    daemonControlService,
                    daemonRuntime,
                    line => CliLogService.AppendLog(streamLog, line)),
                appCancellation.Token);
            continue;
        }

        if (matched.Trigger.StartsWith("/test", StringComparison.Ordinal))
        {
            var testPayload = input.Length > "/test".Length
                ? $"test {input["/test".Length..].Trim()}"
                : "test";
            await AwaitWithCancellationAsync(
                () => testCommandService.HandleTestCommandAsync(
                    testPayload,
                    session,
                    line => CliLogService.AppendLog(streamLog, line),
                    appCancellation.Token),
                appCancellation.Token);
            continue;
        }

        if (matched.Trigger.StartsWith("/diag", StringComparison.Ordinal))
        {
            var diagPayload = input.Length > "/diag".Length
                ? $"diag {input["/diag".Length..].Trim()}"
                : "diag";
            await AwaitWithCancellationAsync(
                () => diagCommandService.HandleDiagCommandAsync(
                    diagPayload,
                    session,
                    daemonControlService,
                    daemonRuntime,
                    line => CliLogService.AppendLog(streamLog, line)),
                appCancellation.Token);
            continue;
        }

        if (matched.Trigger.StartsWith("/console", StringComparison.Ordinal))
        {
            await AwaitWithCancellationAsync(
                () => runtimeCommandService.HandleConsoleCommandAsync(
                    input, session, line => CliLogService.AppendLog(streamLog, line)),
                appCancellation.Token);
            continue;
        }

        if (matched.Trigger.StartsWith("/playmode", StringComparison.Ordinal))
        {
            await AwaitWithCancellationAsync(
                () => runtimeCommandService.HandlePlaymodeCommandAsync(
                    input, session, line => CliLogService.AppendLog(streamLog, line)),
                appCancellation.Token);
            continue;
        }

        if (matched.Trigger.StartsWith("/time", StringComparison.Ordinal))
        {
            await AwaitWithCancellationAsync(
                () => runtimeCommandService.HandleTimeCommandAsync(
                    input, session, line => CliLogService.AppendLog(streamLog, line)),
                appCancellation.Token);
            continue;
        }

        if (matched.Trigger.StartsWith("/compile", StringComparison.Ordinal))
        {
            await AwaitWithCancellationAsync(
                () => runtimeCommandService.HandleCompileCommandAsync(
                    input, session, line => CliLogService.AppendLog(streamLog, line)),
                appCancellation.Token);
            continue;
        }

        if (matched.Trigger.StartsWith("/profiler", StringComparison.Ordinal))
        {
            await AwaitWithCancellationAsync(
                () => runtimeCommandService.HandleProfilerCommandAsync(
                    input, session, line => CliLogService.AppendLog(streamLog, line)),
                appCancellation.Token);
            continue;
        }

        if (matched.Trigger.StartsWith("/recorder", StringComparison.Ordinal))
        {
            await AwaitWithCancellationAsync(
                () => runtimeCommandService.HandleRecorderCommandAsync(
                    input, session, line => CliLogService.AppendLog(streamLog, line)),
                appCancellation.Token);
            continue;
        }

        if (matched.Trigger.StartsWith("/timeline", StringComparison.Ordinal))
        {
            await AwaitWithCancellationAsync(
                () => runtimeCommandService.HandleTimelineCommandAsync(
                    input, session, line => CliLogService.AppendLog(streamLog, line)),
                appCancellation.Token);
            continue;
        }

        if (matched.Trigger is "/keybinds" or "/shortcuts")
        {
            CliLogService.WriteKeybindsHelp(streamLog, session);
            continue;
        }

        if (matched.Trigger == "/version")
        {
            var processPath = Environment.ProcessPath ?? AppContext.BaseDirectory;
            CliLogService.AppendLog(streamLog, $"[grey]version[/]: cli [white]{Markup.Escape(CliVersion.SemVer)}[/], protocol [white]{Markup.Escape(CliVersion.Protocol)}[/]");
            CliLogService.AppendLog(streamLog, $"[grey]binary[/]: [white]{Markup.Escape(processPath)}[/]");
            continue;
        }

        if (matched.Trigger == "/protocol")
        {
            CliLogService.AppendLog(streamLog, $"[grey]protocol[/]: [white]{Markup.Escape(CliVersion.Protocol)}[/]");
            CliLogService.AppendLog(streamLog, "[grey]agentic[/]: schema v1, formats=json|yaml, endpoints=/agent/exec,/agent/capabilities,/agent/status,/agent/dump/{hierarchy|project|inspector}");
            CliLogService.AppendLog(streamLog, "[grey]agentic[/]: exit-codes 0=success, 2=validation, 3=daemon-unavailable, 4=execution-error, 6=permission-escalation-required");
            continue;
        }

        if (matched.Trigger == "/dump")
        {
            await AwaitWithCancellationAsync(
                () => CliDumpService.HandleDumpCommandAsync(input, session, streamLog),
                appCancellation.Token);
            continue;
        }

        if (matched.Trigger.StartsWith("/debug-artifact", StringComparison.Ordinal))
        {
            if (session.Mode != CliMode.Project || string.IsNullOrWhiteSpace(session.CurrentProjectPath))
            {
                CliLogService.AppendLog(streamLog, "[yellow]mode[/]: open a project first with /open");
                continue;
            }

            var isPrep = input.Contains("prep", StringComparison.OrdinalIgnoreCase);
            await AwaitWithCancellationAsync(
                async () =>
                {
                    var tier = 1;
                    var tierMatch = System.Text.RegularExpressions.Regex.Match(input, @"--tier\s+(\d)");
                    if (tierMatch.Success) tier = Math.Clamp(int.Parse(tierMatch.Groups[1].Value), 0, 3);

                    var projectPath = session.CurrentProjectPath!;
                    var daemonPort = DebugArtifactService.ResolveDaemonPort(projectPath);
                    if (daemonPort <= 0)
                    {
                        CliLogService.AppendLog(streamLog, "[red]error[/]: no daemon available");
                        return;
                    }

                    var service = new DebugArtifactService();

                    if (isPrep)
                    {
                        CliLogService.AppendLog(streamLog, $"[grey]artifact[/]: preparing tier {tier} debug session...");
                        var prep = await service.PrepAsync(tier, daemonPort, appCancellation.Token);

                        var statusColor = prep.Ok ? "green" : "yellow";
                        CliLogService.AppendLog(streamLog,
                            $"[{statusColor}]artifact[/]: prep {(prep.Ok ? "complete" : "partial")} — " +
                            $"console cleared: {prep.ConsoleCleared}, profiler: {prep.ProfilerStarted}, recorder: {prep.RecorderStarted}");
                        if (prep.Errors.Count > 0)
                        {
                            foreach (var err in prep.Errors)
                                CliLogService.AppendLog(streamLog, $"[yellow]  {Markup.Escape(err.Operation)}[/]: {Markup.Escape(err.Error)}");
                        }
                        CliLogService.AppendLog(streamLog, $"[grey]next[/]: {Markup.Escape(prep.NextStep)}");
                    }
                    else
                    {
                        CliLogService.AppendLog(streamLog, $"[grey]artifact[/]: collecting tier {tier} debug artifact...");
                        var artifact = await service.CollectAsync(tier, daemonPort, null, appCancellation.Token);
                        var outputPath = service.PersistArtifact(artifact, projectPath);

                        CliLogService.AppendLog(streamLog,
                            $"[green]artifact[/]: tier {tier} debug artifact collected → {Markup.Escape(outputPath)}");
                        if (artifact.Errors.Count > 0)
                        {
                            CliLogService.AppendLog(streamLog,
                                $"[yellow]artifact[/]: {artifact.Errors.Count} sub-operation(s) failed during collection");
                        }
                        CliLogService.AppendLog(streamLog,
                            $"[grey]artifact[/]: duration {artifact.CollectionDurationMs:F0}ms");
                    }
                },
                appCancellation.Token);
            continue;
        }

        CliLogService.AppendLog(streamLog, $"[bold deepskyblue1]unifocl[/] [grey]>[/] [white]{Markup.Escape(input)}[/]");
        if (matched.Trigger.StartsWith("/daemon", StringComparison.Ordinal))
        {
            await AwaitWithCancellationAsync(
                () => daemonControlService.HandleDaemonCommandAsync(
                    input,
                    matched.Trigger,
                    daemonRuntime,
                    session,
                    line => CliLogService.AppendLog(streamLog, line),
                    streamLog),
                appCancellation.Token);
            if (matched.Trigger == "/daemon attach")
            {
                await AwaitWithCancellationAsync(
                    () => buildCommandService.NotifyAttachedBuildIfAnyAsync(
                        session,
                        line => CliLogService.AppendLog(streamLog, line)),
                    appCancellation.Token);
            }
            continue;
        }
        if (await projectLifecycleService.TryHandleLifecycleCommandAsync(
                    input,
                    matched,
                    session,
                    daemonControlService,
                    daemonRuntime,
                    line => CliLogService.AppendLog(streamLog, line))
                .WaitAsync(appCancellation.Token))
        {
            if ((matched.Trigger == "/open" || matched.Trigger == "/new" || matched.Trigger == "/clone" || matched.Trigger == "/recent")
                && session.Mode == CliMode.Project
                && !string.IsNullOrWhiteSpace(session.CurrentProjectPath))
            {
                await projectCommandRouterService.TryHandleProjectCommandAsync(
                        string.Empty,
                        session,
                        daemonControlService,
                        daemonRuntime,
                        line => CliLogService.AppendLog(streamLog, line))
                    .WaitAsync(appCancellation.Token);
            }

            continue;
        }

        CliLogService.AppendLog(streamLog, $"[yellow]command[/]: unsupported route -> {Markup.Escape(matched.Signature)}");
        CliLogService.AppendLog(streamLog, "[grey]hint[/]: run /help for available commands and mode-specific workflows");
    }
        catch (OperationCanceledException) when (appCancellation.IsCancellationRequested)
        {
            CliLogService.AppendLog(streamLog, "[yellow]system[/]: cancellation requested, shutting down");
            break;
        }
        catch (Exception ex)
        {
            CliLogService.LogUnhandledException(streamLog, ex, "command");
        }
    }

    try
    {
        await projectLifecycleService.PerformSafeExitCleanupAsync(
            session,
            daemonControlService,
            daemonRuntime,
            line => CliLogService.AppendLog(streamLog, line));
    }
    catch (OperationCanceledException) when (appCancellation.IsCancellationRequested)
    {
    }
    catch (Exception ex)
    {
        CliLogService.LogUnhandledException(streamLog, ex, "shutdown");
    }
}
finally
{
    Interlocked.Exchange(ref cancellationDisposeRequested, 1);
    Console.CancelKeyPress -= cancelHandler;
    AppDomain.CurrentDomain.ProcessExit -= processExitHandler;

    var spinWait = new SpinWait();
    while (Volatile.Read(ref cancellationRefCount) > 0)
    {
        spinWait.SpinOnce();
    }

    appCancellation.Dispose();
}
