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
        BuildCommandService buildCommandService,
        CancellationToken cancellationToken = default)
    {
        var requestId = string.IsNullOrWhiteSpace(options.RequestId) ? Guid.NewGuid().ToString("N") : options.RequestId!;
        var sessionSeed = AgenticStatePersistenceService.NormalizeSessionSeed(options.SessionSeed);
        var extraMeta = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["command"] = options.CommandText,
            ["sessionSeed"] = sessionSeed
        };

        var persisted = AgenticStatePersistenceService.TryReadSessionSnapshot(sessionSeed);
        if (persisted is not null)
        {
            ApplyPersistedSessionSnapshot(session, persisted);
            extraMeta["sessionRestored"] = true;
        }
        else
        {
            extraMeta["sessionRestored"] = false;
        }

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
                var dump = await CliDumpService.ExecuteDumpCommandAsync(input, session).WaitAsync(cancellationToken);
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
                    buildCommandService,
                    cancellationToken).WaitAsync(cancellationToken);
                var parsed = CliAgenticIssueService.ParseAgenticIssuesFromLogs(streamLog);
                errors.AddRange(parsed.Errors);
                warnings.AddRange(parsed.Warnings);
                if (parsed.RequiresEscalation)
                {
                    extraMeta["requiresEscalation"] = true;
                    extraMeta["escalationEvidence"] = parsed.EscalationEvidence;
                    extraMeta["escalatedRerunCommand"] = BuildEscalatedRerunCommand(options);
                }

                data = new Dictionary<string, object?>
                {
                    ["logs"] = streamLog.Select(AgenticFormatter.StripMarkup).Where(line => !string.IsNullOrWhiteSpace(line)).ToList()
                };
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            errors.Add(new AgenticError("E_CANCELED", "execution canceled"));
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
        AgenticStatePersistenceService.WriteSessionSnapshot(sessionSeed, session, requestId, options.CommandText);

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

    private static string BuildEscalatedRerunCommand(ExecLaunchOptions options)
    {
        static string QuoteIfNeeded(string value)
        {
            return value.Contains(' ', StringComparison.Ordinal)
                ? $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\""
                : value;
        }

        var args = new List<string>
        {
            "unifocl",
            "exec",
            QuoteIfNeeded(options.CommandText),
            "--agentic",
            "--format",
            options.Format.ToString().ToLowerInvariant()
        };

        if (!string.IsNullOrWhiteSpace(options.ProjectPath))
        {
            args.Add("--project");
            args.Add(QuoteIfNeeded(options.ProjectPath!));
        }

        if (options.ContextMode is not null)
        {
            args.Add("--mode");
            args.Add(options.ContextMode.Value.ToString().ToLowerInvariant());
        }

        if (options.AttachPort is int attachPort)
        {
            args.Add("--attach-port");
            args.Add(attachPort.ToString());
        }

        if (!string.IsNullOrWhiteSpace(options.RequestId))
        {
            args.Add("--request-id");
            args.Add(QuoteIfNeeded(options.RequestId!));
        }

        if (!string.IsNullOrWhiteSpace(options.SessionSeed))
        {
            args.Add("--session-seed");
            args.Add(QuoteIfNeeded(options.SessionSeed!));
        }

        return string.Join(' ', args);
    }

    private static void ApplyPersistedSessionSnapshot(CliSessionState session, AgenticSessionSnapshot snapshot)
    {
        session.Mode = snapshot.Mode.Equals("project", StringComparison.OrdinalIgnoreCase)
            ? CliMode.Project
            : CliMode.Boot;
        session.ContextMode = snapshot.ContextMode.ToLowerInvariant() switch
        {
            "project" => CliContextMode.Project,
            "hierarchy" => CliContextMode.Hierarchy,
            "inspector" => CliContextMode.Inspector,
            _ => CliContextMode.None
        };
        session.CurrentProjectPath = snapshot.CurrentProjectPath;
        session.AttachedPort = snapshot.AttachedPort;
        session.FocusPath = string.IsNullOrWhiteSpace(snapshot.InspectorTargetPath)
            ? snapshot.FocusPath
            : snapshot.InspectorTargetPath!;
        session.SafeModeEnabled = snapshot.SafeModeEnabled;
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
        BuildCommandService buildCommandService,
        CancellationToken cancellationToken = default)
    {
        static async Task AwaitWithCancellationAsync(Func<Task> operation, CancellationToken token)
        {
            await operation().WaitAsync(token);
        }

        if (session.ContextMode == CliContextMode.Inspector
            && session.Inspector is null
            && !input.StartsWith("inspect ", StringComparison.OrdinalIgnoreCase)
            && !input.Equals("inspect", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(session.FocusPath))
        {
            var escapedFocusPath = session.FocusPath!.Contains(' ', StringComparison.Ordinal)
                ? $"\"{session.FocusPath}\""
                : session.FocusPath;
            await projectCommandRouterService.TryHandleProjectCommandAsync(
                $"inspect {escapedFocusPath}",
                session,
                daemonControlService,
                daemonRuntime,
                line => CliLogService.AppendLog(streamLog, line)).WaitAsync(cancellationToken);
        }

        if (input.Equals(":focus-recent", StringComparison.OrdinalIgnoreCase))
        {
            await AwaitWithCancellationAsync(
                () => projectLifecycleService.TryHandleRecentSelectionToggleAsync(
                    session,
                    daemonControlService,
                    daemonRuntime,
                    line => CliLogService.AppendLog(streamLog, line)),
                cancellationToken);
            return;
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
                    cancellationToken);
                return;
            }

            var handledProjectCommand = await projectCommandRouterService.TryHandleProjectCommandAsync(
                input,
                session,
                daemonControlService,
                daemonRuntime,
                line => CliLogService.AppendLog(streamLog, line)).WaitAsync(cancellationToken);
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
            await AwaitWithCancellationAsync(
                () => CliDumpService.HandleDumpCommandAsync(input, session, streamLog),
                cancellationToken);
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
                line => CliLogService.AppendLog(streamLog, line)).WaitAsync(cancellationToken);
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
                line => CliLogService.AppendLog(streamLog, line)).WaitAsync(cancellationToken);
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
                line => CliLogService.AppendLog(streamLog, line)).WaitAsync(cancellationToken);
            if (!handled)
            {
                CliLogService.AppendLog(streamLog, "[yellow]upm[/]: unsupported /upm command");
            }

            return;
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
                cancellationToken);
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
            await AwaitWithCancellationAsync(
                () => daemonControlService.HandleDaemonCommandAsync(
                    input,
                    matched.Trigger,
                    daemonRuntime,
                    session,
                    line => CliLogService.AppendLog(streamLog, line),
                    streamLog),
                cancellationToken);
            if (matched.Trigger == "/daemon attach")
            {
                await AwaitWithCancellationAsync(
                    () => buildCommandService.NotifyAttachedBuildIfAnyAsync(
                        session,
                        line => CliLogService.AppendLog(streamLog, line)),
                    cancellationToken);
            }

            return;
        }

        if (await projectLifecycleService.TryHandleLifecycleCommandAsync(
                input,
                matched,
                session,
                daemonControlService,
                daemonRuntime,
                line => CliLogService.AppendLog(streamLog, line)).WaitAsync(cancellationToken))
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
                    line => CliLogService.AppendLog(streamLog, line)).WaitAsync(cancellationToken);
            }

            return;
        }

        CliLogService.AppendLog(streamLog, $"[yellow]command[/]: unsupported route -> {Markup.Escape(matched.Signature)}");
        CliLogService.AppendLog(streamLog, "[grey]hint[/]: run /help for available commands and mode-specific workflows");
    }
}
