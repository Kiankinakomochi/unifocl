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
    var runtimePath = Path.Combine(Environment.CurrentDirectory, ".unifocl-runtime");
    var daemonRuntime = new DaemonRuntime(runtimePath);
    var session = new CliSessionState();
    var daemonControlService = new DaemonControlService();
    var projectLifecycleService = new ProjectLifecycleService();
    var projectCommandRouterService = new ProjectCommandRouterService();
    var hierarchyTui = new HierarchyTui();
    var buildCommandService = new BuildCommandService();
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
                appCancellation.Token).WaitAsync(appCancellation.Token);
        }
        catch (OperationCanceledException) when (appCancellation.IsCancellationRequested)
        {
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
            CliLogService.AppendLog(streamLog, "[grey]agentic[/]: exit-codes 0=success, 2=validation, 3=daemon-unavailable, 4=execution-error");
            continue;
        }

        if (matched.Trigger == "/dump")
        {
            await AwaitWithCancellationAsync(
                () => CliDumpService.HandleDumpCommandAsync(input, session, streamLog),
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

        CliLogService.AppendLog(streamLog, $"[yellow]command[/]: not implemented yet -> {Markup.Escape(matched.Signature)}");
        CliLogService.AppendLog(streamLog, "[grey]hint[/]: run /help for implemented commands and mode-specific workflows");
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
