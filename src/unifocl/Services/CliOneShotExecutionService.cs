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
        ValidateCommandService validateCommandService,
        DiagCommandService diagCommandService,
        TestCommandService testCommandService,
        TagLayerCommandService tagLayerCommandService,
        RuntimeCommandService runtimeCommandService,
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
            DaemonControlService.SetAttachedPort(session, options.AttachPort.Value, session.CurrentProjectPath ?? string.Empty);
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
        var blockedByGuard = false;
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
                var autoOpenAttempted = false;
                if (!dump.Ok
                    && ShouldAutoOpenForDump(input, session, dump.Error))
                {
                    autoOpenAttempted = true;
                    await TryAutoOpenProjectForEndpointAsync(
                        session,
                        streamLog,
                        daemonControlService,
                        daemonRuntime,
                        projectLifecycleService,
                        cancellationToken).WaitAsync(cancellationToken);
                    dump = await CliDumpService.ExecuteDumpCommandAsync(input, session).WaitAsync(cancellationToken);
                }

                if (!dump.Ok)
                {
                    if (autoOpenAttempted)
                    {
                        var parsed = CliAgenticIssueService.ParseAgenticIssuesFromLogs(streamLog);
                        if (parsed.Errors.Count > 0)
                        {
                            errors.AddRange(parsed.Errors);
                        }
                    }

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
                if (options.Agentic
                    && session.Mode == CliMode.Project
                    && !string.IsNullOrWhiteSpace(session.CurrentProjectPath)
                    && !input.StartsWith('/')
                    && ProjectVcsProfileService.IsProjectMutationCommand(input))
                {
                    var guard = ProjectVcsProfileService.EvaluateMutationGuardForAgentic(session.CurrentProjectPath!);
                    if (!guard.Allowed)
                    {
                    errors.Add(new AgenticError(
                            "E_VCS_SETUP_REQUIRED",
                            guard.Message,
                            "Prompt the user to run a project mutation in interactive mode and approve VCS setup first."));
                    }
                }

                if (errors.Count > 0)
                {
                    blockedByGuard = true;
                }

                if (!blockedByGuard)
                {
                    var commandSequence = SplitOneShotCommands(input);
                    foreach (var step in commandSequence)
                    {
                        if (step.StartsWith("/dump", StringComparison.OrdinalIgnoreCase))
                        {
                            var dump = await CliDumpService.ExecuteDumpCommandAsync(step, session).WaitAsync(cancellationToken);
                            var autoOpenAttempted = false;
                            if (!dump.Ok && ShouldAutoOpenForDump(step, session, dump.Error))
                            {
                                autoOpenAttempted = true;
                                await TryAutoOpenProjectForEndpointAsync(
                                    session,
                                    streamLog,
                                    daemonControlService,
                                    daemonRuntime,
                                    projectLifecycleService,
                                    cancellationToken).WaitAsync(cancellationToken);
                                dump = await CliDumpService.ExecuteDumpCommandAsync(step, session).WaitAsync(cancellationToken);
                            }

                            if (!dump.Ok)
                            {
                                if (autoOpenAttempted)
                                {
                                    var openParsed = CliAgenticIssueService.ParseAgenticIssuesFromLogs(streamLog);
                                    if (openParsed.Errors.Count > 0)
                                    {
                                        errors.AddRange(openParsed.Errors);
                                    }
                                }

                                errors.Add(dump.Error!);
                            }
                            else
                            {
                                data = dump.PayloadData;
                                extraMeta["format"] = dump.Format.ToString().ToLowerInvariant();
                                extraMeta["category"] = $"dump-{dump.Category}";
                            }

                            continue;
                        }

                        if (step.StartsWith("/mutate", StringComparison.OrdinalIgnoreCase)
                            && session.Mode == CliMode.Project
                            && !string.IsNullOrWhiteSpace(session.CurrentProjectPath))
                        {
                            var mutatePayload = step.Length > "/mutate".Length
                                ? $"mutate {step["/mutate".Length..].Trim()}"
                                : "mutate";
                            var mutateResult = await projectCommandRouterService.HandleMutateCommandAsync(
                                mutatePayload,
                                session,
                                line => CliLogService.AppendLog(streamLog, line)).WaitAsync(cancellationToken);
                            if (mutateResult is not null)
                            {
                                data = new Dictionary<string, object?>
                                {
                                    ["allOk"] = mutateResult.AllOk,
                                    ["total"] = mutateResult.Total,
                                    ["succeeded"] = mutateResult.Succeeded,
                                    ["failed"] = mutateResult.Failed,
                                    ["dryRun"] = mutateResult.DryRun,
                                    ["results"] = mutateResult.Results,
                                };
                            }

                            continue;
                        }

                        await ExecuteCommandForOneShotAsync(
                            step,
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
                            cancellationToken).WaitAsync(cancellationToken);
                    }
                    var parsed = CliAgenticIssueService.ParseAgenticIssuesFromLogs(streamLog);
                    errors.AddRange(parsed.Errors);
                    warnings.AddRange(parsed.Warnings);
                    if (parsed.RequiresEscalation)
                    {
                        extraMeta["requiresEscalation"] = true;
                        extraMeta["escalationEvidence"] = parsed.EscalationEvidence;
                        extraMeta["escalatedRerunCommand"] = BuildEscalatedRerunCommand(options);
                    }

                    if (data is null)
                    {
                        data = new Dictionary<string, object?>
                        {
                            ["logs"] = streamLog.Select(AgenticFormatter.StripMarkup).Where(line => !string.IsNullOrWhiteSpace(line)).ToList()
                        };
                    }
                }
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

    private static bool ShouldAutoOpenForDump(
        string input,
        CliSessionState session,
        AgenticError? error)
    {
        if (string.IsNullOrWhiteSpace(session.CurrentProjectPath))
        {
            return false;
        }

        if (error is null || !error.Code.Equals("E_MODE_INVALID", StringComparison.Ordinal))
        {
            return false;
        }

        return input.StartsWith("/dump hierarchy", StringComparison.OrdinalIgnoreCase)
               || input.StartsWith("/dump inspector", StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> SplitOneShotCommands(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return [];
        }

        var lines = input
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();

        return lines.Count == 0 ? [input.Trim()] : lines;
    }

    private static async Task TryAutoOpenProjectForEndpointAsync(
        CliSessionState session,
        List<string> streamLog,
        DaemonControlService daemonControlService,
        DaemonRuntime daemonRuntime,
        ProjectLifecycleService projectLifecycleService,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(session.CurrentProjectPath))
        {
            return;
        }

        await projectLifecycleService.EnsureProjectOpenForAgenticEndpointAsync(
            session.CurrentProjectPath,
            session,
            daemonControlService,
            daemonRuntime,
            line => CliLogService.AppendLog(streamLog, line)).WaitAsync(cancellationToken);
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
        if (snapshot.AttachedPort is int restoredPort)
        {
            DaemonControlService.SetAttachedPort(session, restoredPort, session.CurrentProjectPath ?? string.Empty);
        }

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
        ValidateCommandService validateCommandService,
        DiagCommandService diagCommandService,
        TestCommandService testCommandService,
        TagLayerCommandService tagLayerCommandService,
        RuntimeCommandService runtimeCommandService,
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
            if (session.ContextMode == CliContextMode.Hierarchy)
            {
                var handledHierarchyCommand = await hierarchyTui.TryHandleOneShotCommandAsync(
                    input,
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
                    }).WaitAsync(cancellationToken);
                if (handledHierarchyCommand)
                {
                    return;
                }
            }

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

            if (CliCommandParsingService.IsTestCommand(input))
            {
                await AwaitWithCancellationAsync(
                    () => testCommandService.HandleTestCommandAsync(
                        input,
                        session,
                        line => CliLogService.AppendLog(streamLog, line),
                        cancellationToken),
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
            await projectLifecycleService.PerformSafeExitCleanupAsync(
                session,
                daemonControlService,
                daemonRuntime,
                line => CliLogService.AppendLog(streamLog, line));
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

        if (matched.Trigger.StartsWith("/debug-artifact", StringComparison.Ordinal))
        {
            if (session.Mode != CliMode.Project || string.IsNullOrWhiteSpace(session.CurrentProjectPath))
            {
                CliLogService.AppendLog(streamLog, "[yellow]mode[/]: open a project first with /open");
                return;
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
                        var prep = await service.PrepAsync(tier, daemonPort, cancellationToken);
                        CliLogService.AppendLog(streamLog,
                            $"[{(prep.Ok ? "green" : "yellow")}]artifact[/]: prep {(prep.Ok ? "complete" : "partial")} — " +
                            $"console: {prep.ConsoleCleared}, profiler: {prep.ProfilerStarted}, recorder: {prep.RecorderStarted}");
                        if (prep.Errors.Count > 0)
                        {
                            foreach (var err in prep.Errors)
                                CliLogService.AppendLog(streamLog, $"[yellow]  {err.Operation}[/]: {err.Error}");
                        }
                        CliLogService.AppendLog(streamLog, $"[grey]next[/]: {prep.NextStep}");
                    }
                    else
                    {
                        var artifact = await service.CollectAsync(tier, daemonPort, null, cancellationToken);
                        var outputPath = service.PersistArtifact(artifact, projectPath);
                        CliLogService.AppendLog(streamLog,
                            $"[green]artifact[/]: tier {tier} collected → {outputPath}");
                        CliLogService.AppendLog(streamLog,
                            $"[grey]  errors: {artifact.Errors.Count}, duration: {artifact.CollectionDurationMs:F0}ms[/]");
                    }
                },
                cancellationToken);
            return;
        }

        if (matched.Trigger.StartsWith("/console", StringComparison.Ordinal))
        {
            await AwaitWithCancellationAsync(
                () => runtimeCommandService.HandleConsoleCommandAsync(
                    input, session, line => CliLogService.AppendLog(streamLog, line)),
                cancellationToken);
            return;
        }

        if (matched.Trigger.StartsWith("/playmode", StringComparison.Ordinal))
        {
            await AwaitWithCancellationAsync(
                () => runtimeCommandService.HandlePlaymodeCommandAsync(
                    input, session, line => CliLogService.AppendLog(streamLog, line)),
                cancellationToken);
            return;
        }

        if (matched.Trigger.StartsWith("/time", StringComparison.Ordinal))
        {
            await AwaitWithCancellationAsync(
                () => runtimeCommandService.HandleTimeCommandAsync(
                    input, session, line => CliLogService.AppendLog(streamLog, line)),
                cancellationToken);
            return;
        }

        if (matched.Trigger.StartsWith("/compile", StringComparison.Ordinal))
        {
            await AwaitWithCancellationAsync(
                () => runtimeCommandService.HandleCompileCommandAsync(
                    input, session, line => CliLogService.AppendLog(streamLog, line)),
                cancellationToken);
            return;
        }

        if (matched.Trigger.StartsWith("/profiler", StringComparison.Ordinal))
        {
            await AwaitWithCancellationAsync(
                () => runtimeCommandService.HandleProfilerCommandAsync(
                    input, session, line => CliLogService.AppendLog(streamLog, line)),
                cancellationToken);
            return;
        }

        if (matched.Trigger.StartsWith("/recorder", StringComparison.Ordinal))
        {
            await AwaitWithCancellationAsync(
                () => runtimeCommandService.HandleRecorderCommandAsync(
                    input, session, line => CliLogService.AppendLog(streamLog, line)),
                cancellationToken);
            return;
        }

        if (matched.Trigger == "/hierarchy")
        {
            if (session.Mode != CliMode.Project || string.IsNullOrWhiteSpace(session.CurrentProjectPath))
            {
                CliLogService.AppendLog(streamLog, "[yellow]mode[/]: open a project first with /open");
                return;
            }

            session.ContextMode = CliContextMode.Hierarchy;
            session.Inspector = null;
            CliLogService.AppendLog(streamLog, "[grey]mode[/]: switched to hierarchy context (agentic one-shot)");
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

        if (matched.Trigger.StartsWith("/addressable", StringComparison.Ordinal))
        {
            if (session.Mode != CliMode.Project || string.IsNullOrWhiteSpace(session.CurrentProjectPath))
            {
                CliLogService.AppendLog(streamLog, "[yellow]mode[/]: open a project first with /open");
                return;
            }

            var addressableInput = input.Length > "/addressable".Length
                ? $"addressable {input["/addressable".Length..].Trim()}"
                : "addressable";
            var handled = await projectCommandRouterService.TryHandleProjectCommandAsync(
                addressableInput,
                session,
                daemonControlService,
                daemonRuntime,
                line => CliLogService.AppendLog(streamLog, line)).WaitAsync(cancellationToken);
            if (!handled)
            {
                CliLogService.AppendLog(streamLog, "[yellow]addressable[/]: unsupported /addressable command");
            }

            return;
        }

        if (matched.Trigger.StartsWith("/asset get", StringComparison.Ordinal)
            || matched.Trigger.StartsWith("/asset set", StringComparison.Ordinal))
        {
            if (session.Mode != CliMode.Project || string.IsNullOrWhiteSpace(session.CurrentProjectPath))
            {
                CliLogService.AppendLog(streamLog, "[yellow]mode[/]: open a project first with /open");
                return;
            }

            var assetInput = input.Length > "/asset".Length
                ? $"asset {input["/asset".Length..].Trim()}"
                : "asset";
            var handled = await projectCommandRouterService.TryHandleProjectCommandAsync(
                assetInput,
                session,
                daemonControlService,
                daemonRuntime,
                line => CliLogService.AppendLog(streamLog, line)).WaitAsync(cancellationToken);
            if (!handled)
            {
                CliLogService.AppendLog(streamLog, "[yellow]asset[/]: unsupported /asset command");
            }

            return;
        }

        if (matched.Trigger.StartsWith("/animator", StringComparison.Ordinal))
        {
            if (session.Mode != CliMode.Project || string.IsNullOrWhiteSpace(session.CurrentProjectPath))
            {
                CliLogService.AppendLog(streamLog, "[yellow]mode[/]: open a project first with /open");
                return;
            }

            var animatorInput = input.Length > "/animator".Length
                ? $"animator {input["/animator".Length..].Trim()}"
                : "animator";
            var handled = await projectCommandRouterService.TryHandleProjectCommandAsync(
                animatorInput,
                session,
                daemonControlService,
                daemonRuntime,
                line => CliLogService.AppendLog(streamLog, line)).WaitAsync(cancellationToken);
            if (!handled)
            {
                CliLogService.AppendLog(streamLog, "[yellow]animator[/]: unsupported /animator command");
            }

            return;
        }

        if (matched.Trigger.StartsWith("/clip", StringComparison.Ordinal))
        {
            if (session.Mode != CliMode.Project || string.IsNullOrWhiteSpace(session.CurrentProjectPath))
            {
                CliLogService.AppendLog(streamLog, "[yellow]mode[/]: open a project first with /open");
                return;
            }

            var clipInput = input.Length > "/clip".Length
                ? $"clip {input["/clip".Length..].Trim()}"
                : "clip";
            var handled = await projectCommandRouterService.TryHandleProjectCommandAsync(
                clipInput,
                session,
                daemonControlService,
                daemonRuntime,
                line => CliLogService.AppendLog(streamLog, line)).WaitAsync(cancellationToken);
            if (!handled)
            {
                CliLogService.AppendLog(streamLog, "[yellow]clip[/]: unsupported /clip command");
            }

            return;
        }

        if (matched.Trigger.StartsWith("/runtime", StringComparison.Ordinal))
        {
            if (session.Mode != CliMode.Project || string.IsNullOrWhiteSpace(session.CurrentProjectPath))
            {
                CliLogService.AppendLog(streamLog, "[yellow]mode[/]: open a project first with /open");
                return;
            }

            var runtimeInput = input.Length > "/runtime".Length
                ? $"runtime {input["/runtime".Length..].Trim()}"
                : "runtime";
            var handled = await projectCommandRouterService.TryHandleProjectCommandAsync(
                runtimeInput,
                session,
                daemonControlService,
                daemonRuntime,
                line => CliLogService.AppendLog(streamLog, line)).WaitAsync(cancellationToken);
            if (!handled)
            {
                CliLogService.AppendLog(streamLog, "[yellow]runtime[/]: unsupported /runtime command");
            }

            return;
        }

        if (matched.Trigger == "/mutate")
        {
            if (session.Mode != CliMode.Project || string.IsNullOrWhiteSpace(session.CurrentProjectPath))
            {
                CliLogService.AppendLog(streamLog, "[yellow]mode[/]: open a project first with /open");
                return;
            }

            // Strip leading /mutate and route through project router — IsMutateCommand handles the rest.
            var mutatePayload = input.Length > "/mutate".Length
                ? $"mutate {input["/mutate".Length..].Trim()}"
                : "mutate";
            await projectCommandRouterService.TryHandleProjectCommandAsync(
                mutatePayload,
                session,
                daemonControlService,
                daemonRuntime,
                line => CliLogService.AppendLog(streamLog, line)).WaitAsync(cancellationToken);
            return;
        }

        if (matched.Trigger == "/eval")
        {
            if (session.Mode != CliMode.Project || string.IsNullOrWhiteSpace(session.CurrentProjectPath))
            {
                CliLogService.AppendLog(streamLog, "[yellow]mode[/]: open a project first with /open");
                return;
            }

            await AwaitWithCancellationAsync(
                () => HandleEvalCommandAsync(input, session, daemonControlService, daemonRuntime, streamLog),
                cancellationToken);
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

        if (matched.Trigger.StartsWith("/validate", StringComparison.Ordinal) || matched.Trigger == "/val")
        {
            await AwaitWithCancellationAsync(
                () => validateCommandService.HandleValidateCommandAsync(
                    input,
                    session,
                    daemonControlService,
                    daemonRuntime,
                    line => CliLogService.AppendLog(streamLog, line)),
                cancellationToken);
            return;
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
                cancellationToken);
            return;
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
                cancellationToken);
            return;
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
                cancellationToken);
            return;
        }

        if (matched.Trigger.StartsWith("/test", StringComparison.Ordinal))
        {
            // Strip leading /test and rewrite as "test <sub>" for the service
            var testPayload = input.Length > "/test".Length
                ? $"test {input["/test".Length..].Trim()}"
                : "test";
            await AwaitWithCancellationAsync(
                () => testCommandService.HandleTestCommandAsync(
                    testPayload,
                    session,
                    line => CliLogService.AppendLog(streamLog, line),
                    cancellationToken),
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

    private static async Task HandleEvalCommandAsync(
        string input,
        CliSessionState session,
        DaemonControlService daemonControlService,
        DaemonRuntime daemonRuntime,
        List<string> streamLog)
    {
        var tokens = CliCommandParsingService.TokenizeComposerInput(input);
        if (tokens.Count < 2)
        {
            CliLogService.AppendLog(streamLog, "[red]eval[/]: usage: /eval '<code>' [--declarations '<decl>'] [--timeout <ms>] [--dry-run]");
            return;
        }

        string? code = null;
        string? declarations = null;
        var timeoutMs = 10000;
        var dryRun = false;

        for (var i = 1; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.Equals("--dry-run", StringComparison.OrdinalIgnoreCase))
            {
                dryRun = true;
                continue;
            }

            if (token.Equals("--declarations", StringComparison.OrdinalIgnoreCase) && i + 1 < tokens.Count)
            {
                declarations = tokens[++i];
                continue;
            }

            if (token.Equals("--timeout", StringComparison.OrdinalIgnoreCase) && i + 1 < tokens.Count)
            {
                if (int.TryParse(tokens[++i], out var parsed) && parsed > 0)
                {
                    timeoutMs = parsed;
                }

                continue;
            }

            code ??= token;
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            CliLogService.AppendLog(streamLog, "[red]eval[/]: code expression is required");
            return;
        }

        // Ensure daemon is attached (re-attach/start if session has a persisted project)
        if (DaemonControlService.GetPort(session) is not int)
        {
            if (!await daemonControlService.TouchAttachedDaemonAsync(session)
                && !string.IsNullOrWhiteSpace(session.CurrentProjectPath))
            {
                if (DaemonControlService.IsUnityClientActiveForProject(session.CurrentProjectPath))
                {
                    await daemonControlService.TryAttachProjectDaemonAsync(session.CurrentProjectPath, session);
                }
                else
                {
                    await daemonControlService.EnsureProjectDaemonAsync(
                        session.CurrentProjectPath, daemonRuntime, session,
                        line => CliLogService.AppendLog(streamLog, line),
                        requireBridgeMode: true);
                }
            }
        }

        if (DaemonControlService.GetPort(session) is not int port)
        {
            CliLogService.AppendLog(streamLog, "[yellow]eval[/]: daemon is not attached — open a project first with /open");
            return;
        }

        var content = System.Text.Json.JsonSerializer.Serialize(new { code, declarations = declarations ?? string.Empty, timeoutMs });
        var baseDto = new ProjectCommandRequestDto("eval-code", null, null, content);
        var withIntent = MutationIntentFactory.EnsureProjectIntent(baseDto);
        var dto = withIntent with { Intent = withIntent.Intent! with { Flags = withIntent.Intent.Flags with { DryRun = dryRun } } };

        CliLogService.AppendLog(streamLog, $"[grey]eval[/]: compiling and executing{(dryRun ? " (dry-run)" : "")}...");
        var client = new HierarchyDaemonClient();
        var response = await client.ExecuteProjectCommandAsync(port, dto);
        if (response.Ok)
        {
            var kind = response.Kind ?? "eval";
            CliLogService.AppendLog(streamLog, $"[green]eval[/]: {Markup.Escape(kind)}");
            if (!string.IsNullOrWhiteSpace(response.Content))
            {
                CliLogService.AppendLog(streamLog, Markup.Escape(response.Content!));
            }
            else if (!string.IsNullOrWhiteSpace(response.Message))
            {
                CliLogService.AppendLog(streamLog, Markup.Escape(response.Message));
            }
        }
        else
        {
            CliLogService.AppendLog(streamLog, $"[red]eval[/]: {Markup.Escape(response.Message)}");
            if (!string.IsNullOrWhiteSpace(response.Content))
            {
                CliLogService.AppendLog(streamLog, $"[grey]{Markup.Escape(response.Content!)}[/]");
            }
        }
    }
}
