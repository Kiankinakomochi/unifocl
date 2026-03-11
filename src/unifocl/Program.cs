using Spectre.Console;
using System.Diagnostics;

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

    await DaemonControlService.RunDaemonServiceAsync(serviceOptions!);
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
    var envelope = await CliOneShotExecutionService.ExecuteOneShotCommandAsync(
        execOptions,
        commands,
        streamLog,
        session,
        daemonControlService,
        daemonRuntime,
        projectLifecycleService,
        projectCommandRouterService,
        hierarchyTui,
        buildCommandService);
    var payload = AgenticFormatter.SerializeEnvelope(envelope, execOptions.Format);
    Console.Out.WriteLine(payload);
    Environment.ExitCode = envelope.Meta.ExitCode;
    return;
}

CliBootLogo.SeedBootLog(streamLog);
CliLogService.RenderInitialLog(streamLog);

while (true)
{
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
            await projectLifecycleService.TryHandleRecentSelectionToggleAsync(
                session,
                daemonControlService,
                daemonRuntime,
                line => CliLogService.AppendLog(streamLog, line));
            continue;
        }

        if (!input.StartsWith('/'))
        {
            if (CliCommandParsingService.TryNormalizeProjectBuildCommand(input, out var normalizedBuildInput))
            {
                await buildCommandService.HandleBuildCommandAsync(
                    normalizedBuildInput,
                    session,
                    daemonControlService,
                    daemonRuntime,
                    line => CliLogService.AppendLog(streamLog, line));
                continue;
            }

            var handledProjectCommand = await projectCommandRouterService.TryHandleProjectCommandAsync(
                input,
                session,
                daemonControlService,
                daemonRuntime,
                line => CliLogService.AppendLog(streamLog, line));
            if (!handledProjectCommand)
            {
                CliLogService.AppendLog(streamLog, "[grey]system[/]: unknown project command; use / for command palette");
            }

            if (handledProjectCommand && session.AutoEnterHierarchyRequested)
            {
                session.AutoEnterHierarchyRequested = false;
                session.ContextMode = CliContextMode.Hierarchy;
                await hierarchyTui.RunAsync(
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
                    });
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
                            line => CliLogService.AppendLog(streamLog, line));
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
            await hierarchyTui.RunAsync(
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
                });
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
                        line => CliLogService.AppendLog(streamLog, line));
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
                line => CliLogService.AppendLog(streamLog, line));
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
                line => CliLogService.AppendLog(streamLog, line));
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
                line => CliLogService.AppendLog(streamLog, line));
            if (!handled)
            {
                CliLogService.AppendLog(streamLog, "[yellow]upm[/]: unsupported /upm command");
            }

            continue;
        }

        if (matched.Trigger.StartsWith("/build", StringComparison.Ordinal))
        {
            await buildCommandService.HandleBuildCommandAsync(
                input,
                session,
                daemonControlService,
                daemonRuntime,
                line => CliLogService.AppendLog(streamLog, line));
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
            await CliDumpService.HandleDumpCommandAsync(input, session, streamLog);
            continue;
        }

        CliLogService.AppendLog(streamLog, $"[bold deepskyblue1]unifocl[/] [grey]>[/] [white]{Markup.Escape(input)}[/]");
        if (matched.Trigger.StartsWith("/daemon", StringComparison.Ordinal))
        {
            await daemonControlService.HandleDaemonCommandAsync(
                input,
                matched.Trigger,
                daemonRuntime,
                session,
                line => CliLogService.AppendLog(streamLog, line),
                streamLog);
            if (matched.Trigger == "/daemon attach")
            {
                await buildCommandService.NotifyAttachedBuildIfAnyAsync(
                    session,
                    line => CliLogService.AppendLog(streamLog, line));
            }
            continue;
        }
        if (await projectLifecycleService.TryHandleLifecycleCommandAsync(
                input,
                matched,
                session,
                daemonControlService,
                daemonRuntime,
                line => CliLogService.AppendLog(streamLog, line)))
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
                    line => CliLogService.AppendLog(streamLog, line));
            }

            continue;
        }

        CliLogService.AppendLog(streamLog, $"[yellow]command[/]: not implemented yet -> {Markup.Escape(matched.Signature)}");
        CliLogService.AppendLog(streamLog, "[grey]hint[/]: run /help for implemented commands and mode-specific workflows");
    }
    catch (Exception ex)
    {
        CliLogService.LogUnhandledException(streamLog, ex, "command");
    }
}
