using Spectre.Console;

internal static class CliOneShotExecutionService
{
    public static async Task<AgenticResponseEnvelope> ExecuteOneShotCommandAsync(
        ExecLaunchOptions options,
        List<CommandSpec> commands,
        List<string> streamLog,
        CliSessionState session,
        DaemonControlService daemonControlService,
        DaemonRuntime daemonRuntime,
        ProjectLifecycleService projectLifecycleService,
        ProjectCommandRouterService projectCommandRouterService,
        HierarchyTui hierarchyTui,
        BuildCommandService buildCommandService)
    {
        var requestId = string.IsNullOrWhiteSpace(options.RequestId) ? Guid.NewGuid().ToString("N") : options.RequestId!;
        var extraMeta = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["command"] = options.CommandText
        };

        if (!string.IsNullOrWhiteSpace(options.ProjectPath))
        {
            session.CurrentProjectPath = Path.GetFullPath(options.ProjectPath!);
            session.Mode = CliMode.Project;
            session.ContextMode = options.ContextMode ?? CliContextMode.Project;
        }

        if (options.AttachPort is not null)
        {
            session.AttachedPort = options.AttachPort.Value;
        }

        if (options.ContextMode is not null)
        {
            session.ContextMode = options.ContextMode.Value;
        }

        if (session.ContextMode == CliContextMode.None
            && session.Mode == CliMode.Project
            && !string.IsNullOrWhiteSpace(session.CurrentProjectPath))
        {
            session.ContextMode = CliContextMode.Project;
        }

        object? data = null;
        var errors = new List<AgenticError>();
        var warnings = new List<AgenticWarning>();
        CliDryRunDiffService.Reset();
        try
        {
            var input = options.CommandText.Trim();
            if (string.IsNullOrWhiteSpace(input))
            {
                errors.Add(new AgenticError("E_PARSE", "empty command text", "pass a command after exec"));
            }
            else if (input.StartsWith("/dump", StringComparison.OrdinalIgnoreCase))
            {
                var dump = await CliDumpService.ExecuteDumpCommandAsync(input, session);
                if (!dump.Ok)
                {
                    errors.Add(dump.Error!);
                }
                else
                {
                    data = dump.PayloadData;
                    extraMeta["format"] = dump.Format.ToString().ToLowerInvariant();
                    extraMeta["category"] = $"dump-{dump.Category}";
                }
            }
            else
            {
                await ExecuteCommandForOneShotAsync(
                    input,
                    commands,
                    streamLog,
                    session,
                    daemonControlService,
                    daemonRuntime,
                    projectLifecycleService,
                    projectCommandRouterService,
                    hierarchyTui,
                    buildCommandService);
                var parsed = CliAgenticIssueService.ParseAgenticIssuesFromLogs(streamLog);
                errors.AddRange(parsed.Errors);
                warnings.AddRange(parsed.Warnings);
                data = new Dictionary<string, object?>
                {
                    ["logs"] = streamLog.Select(AgenticFormatter.StripMarkup).Where(line => !string.IsNullOrWhiteSpace(line)).ToList()
                };
            }
        }
        catch (Exception ex)
        {
            errors.Add(new AgenticError("E_INTERNAL", $"unhandled execution exception: {ex.GetType().Name}: {ex.Message}"));
        }

        var exitCode = CliAgenticIssueService.ResolveExitCode(errors);
        var modeName = session.ContextMode switch
        {
            CliContextMode.Project => "project",
            CliContextMode.Hierarchy => "hierarchy",
            CliContextMode.Inspector => "inspector",
            _ => "none"
        };
        var action = CliCommandParsingService.ExtractActionLabel(options.CommandText);
        var diff = CliDryRunDiffService.ConsumeCurrentDiff();

        return new AgenticResponseEnvelope(
            errors.Count == 0 ? "success" : "error",
            requestId,
            modeName,
            action,
            data,
            errors,
            warnings,
            new AgenticMeta(
                "agentic.v1",
                CliVersion.Protocol,
                exitCode,
                DateTime.UtcNow.ToString("O"),
                extraMeta),
            diff);
    }

    private static async Task ExecuteCommandForOneShotAsync(
        string input,
        List<CommandSpec> commands,
        List<string> streamLog,
        CliSessionState session,
        DaemonControlService daemonControlService,
        DaemonRuntime daemonRuntime,
        ProjectLifecycleService projectLifecycleService,
        ProjectCommandRouterService projectCommandRouterService,
        HierarchyTui hierarchyTui,
        BuildCommandService buildCommandService)
    {
        if (input.Equals(":focus-recent", StringComparison.OrdinalIgnoreCase))
        {
            await projectLifecycleService.TryHandleRecentSelectionToggleAsync(
                session,
                daemonControlService,
                daemonRuntime,
                line => CliLogService.AppendLog(streamLog, line));
            return;
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
                return;
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

            return;
        }

        if (input == "/")
        {
            return;
        }

        input = CliCommandParsingService.NormalizeSlashCommand(input);
        var matched = CliCommandParsingService.MatchCommand(input, commands);
        if (matched is null)
        {
            CliLogService.AppendLog(streamLog, $"[grey]system[/]: unknown command [white]{Markup.Escape(input)}[/]");
            return;
        }

        if (matched.Trigger == "/quit")
        {
            CliLogService.AppendLog(streamLog, "[grey]Session closed.[/]");
            return;
        }

        if (matched.Trigger == "/clear")
        {
            streamLog.Clear();
            return;
        }

        if (matched.Trigger == "/dump")
        {
            await CliDumpService.HandleDumpCommandAsync(input, session, streamLog);
            return;
        }

        if (matched.Trigger == "/project")
        {
            if (session.Mode != CliMode.Project || string.IsNullOrWhiteSpace(session.CurrentProjectPath))
            {
                CliLogService.AppendLog(streamLog, "[yellow]mode[/]: open a project first with /open");
                return;
            }

            session.ContextMode = CliContextMode.Project;
            session.Inspector = null;
            await projectCommandRouterService.TryHandleProjectCommandAsync(
                string.Empty,
                session,
                daemonControlService,
                daemonRuntime,
                line => CliLogService.AppendLog(streamLog, line));
            return;
        }

        if (matched.Trigger == "/inspect")
        {
            if (session.Mode != CliMode.Project || string.IsNullOrWhiteSpace(session.CurrentProjectPath))
            {
                CliLogService.AppendLog(streamLog, "[yellow]mode[/]: open a project first with /open");
                return;
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

            return;
        }

        if (matched.Trigger.StartsWith("/upm", StringComparison.Ordinal))
        {
            if (session.Mode != CliMode.Project || string.IsNullOrWhiteSpace(session.CurrentProjectPath))
            {
                CliLogService.AppendLog(streamLog, "[yellow]mode[/]: open a project first with /open");
                return;
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

            return;
        }

        if (matched.Trigger.StartsWith("/build", StringComparison.Ordinal))
        {
            await buildCommandService.HandleBuildCommandAsync(
                input,
                session,
                daemonControlService,
                daemonRuntime,
                line => CliLogService.AppendLog(streamLog, line));
            return;
        }

        if (matched.Trigger is "/version")
        {
            var processPath = Environment.ProcessPath ?? AppContext.BaseDirectory;
            CliLogService.AppendLog(streamLog, $"[grey]version[/]: cli [white]{Markup.Escape(CliVersion.SemVer)}[/], protocol [white]{Markup.Escape(CliVersion.Protocol)}[/]");
            CliLogService.AppendLog(streamLog, $"[grey]binary[/]: [white]{Markup.Escape(processPath)}[/]");
            return;
        }

        if (matched.Trigger is "/protocol")
        {
            CliLogService.AppendLog(streamLog, $"[grey]protocol[/]: [white]{Markup.Escape(CliVersion.Protocol)}[/]");
            CliLogService.AppendLog(streamLog, "[grey]agentic[/]: schema v1, formats=json|yaml, endpoints=/agent/exec,/agent/capabilities,/agent/status,/agent/dump/{hierarchy|project|inspector}");
            return;
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

            return;
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

            return;
        }

        CliLogService.AppendLog(streamLog, $"[yellow]command[/]: not implemented yet -> {Markup.Escape(matched.Signature)}");
        CliLogService.AppendLog(streamLog, "[grey]hint[/]: run /help for implemented commands and mode-specific workflows");
    }
}
