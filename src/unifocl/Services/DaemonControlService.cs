using Spectre.Console;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;

internal sealed partial class DaemonControlService
{
    private const int DefaultInactivityTimeoutSeconds = 600;
    private const int ProcessOutputTailMaxLines = 40;
    private static readonly TimeSpan MinimumDaemonStartupTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ProjectCommandReadyTimeout = MinimumDaemonStartupTimeout;
    private static readonly TimeSpan UnityBridgeAttachWaitTimeout = TimeSpan.FromMinutes(3);
    private static readonly HttpClient Http = new();
    private DaemonStartupFailure? _lastStartupFailure;
    private static readonly ExecSessionService _sessionService = new();

    public async Task HandleDaemonCommandAsync(
        string input,
        string trigger,
        DaemonRuntime runtime,
        CliSessionState session,
        Action<string> log,
        List<string> streamLog)
    {
        var normalizedInput = input.Trim();
        if (normalizedInput.StartsWith("/daemon start", StringComparison.OrdinalIgnoreCase)
            || normalizedInput.StartsWith("/d start", StringComparison.OrdinalIgnoreCase)
            || normalizedInput.Equals("/daemon restart", StringComparison.OrdinalIgnoreCase)
            || normalizedInput.Equals("/d restart", StringComparison.OrdinalIgnoreCase))
        {
            log("[yellow]daemon[/]: explicit daemon startup/restart is removed");
            log("[grey]daemon[/]: use [white]/open <project>[/] to provision and attach daemon context");
            return;
        }

        switch (trigger)
        {
            case "/daemon start":
                log("[yellow]daemon[/]: explicit daemon startup is removed");
                log("[grey]daemon[/]: use [white]/open <project>[/] to provision and attach daemon context");
                break;
            case "/daemon stop":
                await HandleDaemonStopAsync(runtime, session, log);
                break;
            case "/daemon restart":
                log("[yellow]daemon[/]: explicit daemon restart is removed");
                log("[grey]daemon[/]: use [white]/open <project>[/] to provision and attach daemon context");
                break;
            case "/daemon ps":
                await HandleDaemonPsAsync(runtime, session, streamLog, log);
                break;
            case "/daemon attach":
                await HandleDaemonAttachAsync(input, runtime, session, log);
                break;
            case "/daemon detach":
                HandleDaemonDetach(session, log);
                break;
            default:
                log("[yellow]daemon[/]: usage /daemon <stop|ps|attach|detach>");
                await HandleDaemonPsAsync(runtime, session, streamLog, log);
                break;
        }
    }

    public static bool TryParseDaemonServiceArgs(string[] args, out DaemonServiceOptions? options, out string? error)
    {
        options = null;
        error = null;

        if (!args.Contains("--daemon-service", StringComparer.Ordinal))
        {
            return false;
        }

        var port = 8080;
        string? unityPath = null;
        string? projectPath = null;
        var headless = false;
        var ttlSeconds = DefaultInactivityTimeoutSeconds;
        var unsafeHttp = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--port":
                    if (i + 1 >= args.Length || !int.TryParse(args[i + 1], out port) || port is < 1 or > 65535)
                    {
                        error = "Invalid --port value for daemon service.";
                        return true;
                    }

                    i++;
                    break;
                case "--unity":
                    if (i + 1 >= args.Length)
                    {
                        error = "Missing --unity value for daemon service.";
                        return true;
                    }

                    unityPath = args[i + 1];
                    i++;
                    break;
                case "--project":
                    if (i + 1 >= args.Length)
                    {
                        error = "Missing --project value for daemon service.";
                        return true;
                    }

                    projectPath = args[i + 1];
                    i++;
                    break;
                case "--ttl-seconds":
                    if (i + 1 >= args.Length || !int.TryParse(args[i + 1], out ttlSeconds) || ttlSeconds < 1)
                    {
                        error = "Invalid --ttl-seconds value for daemon service.";
                        return true;
                    }

                    i++;
                    break;
                case "--headless":
                    headless = true;
                    break;
                case "--unsafe-http":
                    unsafeHttp = true;
                    break;
            }
        }

        options = new DaemonServiceOptions(port, unityPath, projectPath, headless, ttlSeconds, unsafeHttp);
        return true;
    }

    private async Task HandleDaemonStartAsync(string input, DaemonRuntime runtime, CliSessionState session, Action<string> log)
    {
        if (!TryParseDaemonStartArgs(input, out var startOptions, out var parseError))
        {
            log($"[red]daemon[/]: {Markup.Escape(parseError)}");
            return;
        }

        startOptions = PromoteToHostModeProjectStart(startOptions, session, log);

        if (startOptions.Headless && !string.IsNullOrWhiteSpace(startOptions.ProjectPath))
        {
            var projectPath = startOptions.ProjectPath;
            if (IsUnityClientActiveForProject(projectPath))
            {
                var projectPort = ResolveProjectDaemonPort(projectPath);
                if (await TryAttachProjectDaemonAsync(projectPath, session, log, attemptCount: 2, attemptDelayMs: 250))
                {
                    var projectCommandReady = await IsProjectCommandEndpointResponsiveAsync(projectPort, TimeSpan.FromSeconds(3));
                    if (projectCommandReady)
                    {
                        log($"[green]daemon[/]: Unity editor is already running for project; attached in Bridge mode on [white]127.0.0.1:{projectPort}[/]");
                        return;
                    }

                    log($"[yellow]daemon[/]: existing Bridge mode endpoint on port {projectPort} responds to ping but project commands are unresponsive; restarting");
                    await TrySendControlAsync(projectPort, "STOP", "STOPPING");
                    ClearAttachedPort(session);
                    await Task.Delay(200);
                }
                else
                {
                    log("[yellow]daemon[/]: Unity lock detected, but Bridge mode endpoint is not attachable; starting a new Host mode daemon");
                }
            }
        }

        await StartDaemonAsync(startOptions, runtime, log);
    }

    private async Task HandleDaemonStopAsync(DaemonRuntime runtime, CliSessionState session, Action<string> log)
    {
        var target = ResolveTargetDaemon(runtime, session);
        if (target is null)
        {
            log("[yellow]daemon[/]: no target daemon selected. Attach first or keep exactly one daemon running.");
            return;
        }

        if (!await StopDaemonByPortAsync(target.Port, runtime, session, log))
        {
            return;
        }

        log($"[green]daemon[/]: stopped port {target.Port}");
    }

    private async Task HandleDaemonRestartAsync(DaemonRuntime runtime, CliSessionState session, Action<string> log)
    {
        var target = ResolveTargetDaemon(runtime, session);
        var attachedOnlyPort = target is null ? await ResolveAttachedOnlyPortAsync(session) : null;
        var restartPort = target?.Port ?? attachedOnlyPort ?? 8080;
        var restartHeadless = target?.Headless ?? false;
        var restartUnity = target?.UnityPath;
        var restartProject = target?.ProjectPath;

        if (target is not null)
        {
            if (!await StopDaemonByPortAsync(target.Port, runtime, session, log))
            {
                log($"[red]daemon[/]: could not stop daemon on port {target.Port}; aborting restart");
                return;
            }
        }
        else if (attachedOnlyPort is int attachedPort)
        {
            var stopped = await TrySendControlAsync(attachedPort, "STOP", "STOPPING");
            if (!stopped)
            {
                log($"[red]daemon[/]: could not stop attached endpoint on port {attachedPort}; aborting restart");
                return;
            }

            if (GetPort(session) == attachedPort)
            {
                ClearAttachedPort(session);
            }

            log($"[grey]daemon[/]: stopped attached endpoint on port {attachedPort}");
        }

        var synthesized = $"/daemon start --port {restartPort}" +
                          (restartUnity is null ? string.Empty : $" --unity \"{restartUnity}\"") +
                          (restartProject is null ? string.Empty : $" --project \"{restartProject}\"") +
                          (restartHeadless ? " --headless" : string.Empty);
        await HandleDaemonStartAsync(synthesized, runtime, session, log);
    }

    private static DaemonStartOptions PromoteToHostModeProjectStart(DaemonStartOptions options, CliSessionState session, Action<string> log)
    {
        if (options.Headless)
        {
            return options;
        }

        var projectPath = options.ProjectPath;
        if (string.IsNullOrWhiteSpace(projectPath) && !string.IsNullOrWhiteSpace(session.CurrentProjectPath))
        {
            projectPath = session.CurrentProjectPath;
        }

        if (string.IsNullOrWhiteSpace(projectPath))
        {
            return options;
        }

        var unityPath = options.UnityPath;
        if (string.IsNullOrWhiteSpace(unityPath))
        {
            unityPath = ResolveDefaultUnityPath(projectPath, log);
        }

        if (string.IsNullOrWhiteSpace(unityPath))
        {
            log("[yellow]daemon[/]: project context found but Unity path is unresolved; starting daemon without Host mode");
            return options with { ProjectPath = projectPath };
        }

        var promotedPort = options.Port == 8080 ? ResolveProjectDaemonPort(projectPath) : options.Port;
        log($"[grey]daemon[/]: defaulting to Host mode for project [white]{Markup.Escape(projectPath)}[/]");
        return options with
        {
            Port = promotedPort,
            ProjectPath = projectPath,
            UnityPath = unityPath,
            Headless = true
        };
    }

    private async Task HandleDaemonPsAsync(
        DaemonRuntime runtime,
        CliSessionState session,
        List<string> streamLog,
        Action<string> log)
    {
        runtime.CleanStaleEntries();
        var instances = runtime.GetAll().OrderBy(i => i.Port).ToList();
        var hasLiveAttachedOnly = false;
        if (instances.Count == 0 && GetPort(session) is int attachedPortProbe)
        {
            hasLiveAttachedOnly = await TrySendControlAsync(attachedPortProbe, "PING", "PONG");
            if (!hasLiveAttachedOnly)
            {
                ClearAttachedPort(session);
            }
        }

        var hasLiveProjectBridgeOnly = false;
        var projectBridgePort = 0;
        if (instances.Count == 0 && !hasLiveAttachedOnly && !string.IsNullOrWhiteSpace(session.CurrentProjectPath))
        {
            projectBridgePort = ResolveProjectDaemonPort(session.CurrentProjectPath);
            hasLiveProjectBridgeOnly = await TrySendControlAsync(projectBridgePort, "PING", "PONG");
        }

        if (instances.Count == 0 && !hasLiveAttachedOnly && !hasLiveProjectBridgeOnly)
        {
            log("[grey]daemon[/]: no running daemon instances");
            if (!string.IsNullOrWhiteSpace(session.CurrentProjectPath) && IsUnityClientActiveForProject(session.CurrentProjectPath))
            {
                log("[yellow]daemon[/]: Unity editor lock exists for current project, but bridge endpoint is not responding yet");
            }
            return;
        }

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Port");
        table.AddColumn("PID");
        table.AddColumn("Uptime");
        table.AddColumn("Unity");
        table.AddColumn("Mode");
        table.AddColumn("Attached");

        foreach (var instance in instances)
        {
            var uptime = FormatUptime(instance.StartedAtUtc);
            table.AddRow(
                instance.Port.ToString(),
                instance.Pid.ToString(),
                uptime,
                instance.UnityPath ?? "-",
                instance.Headless ? "host" : "bridge",
                GetPort(session) == instance.Port ? "yes" : "no");
        }

        if (GetPort(session) is int attachedPort && instances.All(i => i.Port != attachedPort))
        {
            if (await TrySendControlAsync(attachedPort, "PING", "PONG"))
            {
                table.AddRow(
                    attachedPort.ToString(),
                    "-",
                    "-",
                    "-",
                    "unknown",
                    "yes");
            }
            else
            {
                ClearAttachedPort(session);
                log($"[yellow]daemon[/]: detached stale session from port {attachedPort} (endpoint not responding)");
            }
        }

        if (hasLiveProjectBridgeOnly && instances.All(i => i.Port != projectBridgePort))
        {
            table.AddRow(
                projectBridgePort.ToString(),
                "-",
                "-",
                "-",
                IsUnityClientActiveForProject(session.CurrentProjectPath!) ? "editor-bridge" : "unknown",
                GetPort(session) == projectBridgePort ? "yes" : "no");
        }

        AnsiConsole.Write(table);
        streamLog.Add("[grey]daemon[/]: listed active instances");
    }

    private async Task HandleDaemonAttachAsync(string input, DaemonRuntime runtime, CliSessionState session, Action<string> log)
    {
        var args = TokenizeArgs(input);
        if (args.Count < 3 || !int.TryParse(args[2], out var port))
        {
            log("[red]daemon[/]: usage /daemon attach <port>");
            return;
        }

        if (!await TrySendControlAsync(port, "PING", "PONG"))
        {
            log($"[red]daemon[/]: daemon on port {port} is not responding");
            return;
        }

        var attachProjectPath = runtime.GetByPort(port)?.ProjectPath ?? session.CurrentProjectPath ?? string.Empty;
        SetAttachedPort(session, port, attachProjectPath);
        var known = runtime.GetByPort(port);
        if (known is null)
        {
            log($"[yellow]daemon[/]: attached to live port {port} (runtime metadata not found)");
            return;
        }

        log($"[green]daemon[/]: attached to port {port}");
    }

    private static void HandleDaemonDetach(CliSessionState session, Action<string> log)
    {
        if (GetPort(session) is not int detachedPort)
        {
            log("[yellow]daemon[/]: no daemon attached");
            return;
        }

        ClearAttachedPort(session);
        log($"[green]daemon[/]: detached from port {detachedPort}; daemon kept running");
    }

    // ── Session port helpers (Sprint 7: AttachedPort removed from CliSessionState) ─────────
    // Port is the single source of truth via ExecSessionService keyed on SessionId.

    /// <summary>
    /// Returns the daemon port the session is attached to, or null if not attached.
    /// Resolves via ExecSessionService using session.SessionId.
    /// </summary>
    internal static int? GetPort(CliSessionState session)
        => _sessionService.Get(session.SessionId)?.Port;

    /// <summary>
    /// Opens (or replaces) an ExecSession for <paramref name="port"/> and writes the
    /// resulting SessionId back to <paramref name="session"/>.
    /// </summary>
    internal static void SetAttachedPort(CliSessionState session, int port, string projectPath)
    {
        var s = _sessionService.OpenForPort(port, projectPath);
        session.SessionId = s.SessionId;
    }

    /// <summary>
    /// Closes the ExecSession bound to this session and clears SessionId.
    /// </summary>
    internal static void ClearAttachedPort(CliSessionState session)
    {
        if (_sessionService.Get(session.SessionId)?.Port is int port)
        {
            _sessionService.CloseByPort(port);
        }

        session.SessionId = null;
    }

    private static DaemonInstance? ResolveTargetDaemon(DaemonRuntime runtime, CliSessionState session)
    {
        runtime.CleanStaleEntries();
        var instances = runtime.GetAll().ToList();
        if (instances.Count == 0)
        {
            return null;
        }

        if (GetPort(session) is int attachedPort)
        {
            return instances.FirstOrDefault(i => i.Port == attachedPort);
        }

        return instances.Count == 1 ? instances[0] : null;
    }

    private static bool TryParseDaemonStartArgs(string input, out DaemonStartOptions options, out string error)
    {
        var tokens = TokenizeArgs(input);
        options = new DaemonStartOptions(8080, null, null, false, false);
        error = string.Empty;

        for (var i = 2; i < tokens.Count; i++)
        {
            var token = tokens[i];
            switch (token)
            {
                case "--port":
                    if (i + 1 >= tokens.Count || !int.TryParse(tokens[i + 1], out var port) || port is < 1 or > 65535)
                    {
                        error = "invalid --port value";
                        return false;
                    }

                    options = options with { Port = port };
                    i++;
                    break;
                case "--unity":
                    if (i + 1 >= tokens.Count)
                    {
                        error = "missing value for --unity";
                        return false;
                    }

                    options = options with { UnityPath = tokens[i + 1] };
                    i++;
                    break;
                case "--project":
                    if (i + 1 >= tokens.Count)
                    {
                        error = "missing value for --project";
                        return false;
                    }

                    options = options with { ProjectPath = tokens[i + 1] };
                    i++;
                    break;
                case "--headless":
                    options = options with { Headless = true };
                    break;
                case "--allow-unsafe":
                    options = options with { AllowUnsafe = true };
                    break;
                default:
                    error = $"unrecognized option {token}";
                    return false;
            }
        }

        return true;
    }

    private static string BuildAgenticCapabilitiesPayload()
    {
        var payload = new AgenticCapabilities(
            "agentic.v1",
            CliVersion.Protocol,
            ["json", "yaml"],
            ["project", "hierarchy", "inspector"],
            ["/agent/exec", "/agent/capabilities", "/agent/status", "/agent/dump/{hierarchy|project|inspector}"],
            new Dictionary<string, string[]>
            {
                ["bash"] = ["src/unifocl/scripts/agent-worktree.sh"],
                ["powershell"] = ["src/unifocl/scripts/agent-worktree.ps1"]
            });
        return JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private static string BuildAgenticStatusPayload(string requestId)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            var missingRequestEnvelope = new AgenticResponseEnvelope(
                "success",
                Guid.NewGuid().ToString("N"),
                "none",
                "status",
                new Dictionary<string, object?>
                {
                    ["active"] = false,
                    ["state"] = "unknown",
                    ["note"] = "pass requestId to query a tracked agentic request"
                },
                [],
                [],
                new AgenticMeta(
                    "agentic.v1",
                    CliVersion.Protocol,
                    0,
                    DateTime.UtcNow.ToString("O")));
            return JsonSerializer.Serialize(missingRequestEnvelope, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }

        var requestStatus = AgenticStatePersistenceService.TryReadRequestStatus(requestId);
        if (requestStatus is null)
        {
            var notFoundEnvelope = new AgenticResponseEnvelope(
                "success",
                requestId,
                "none",
                "status",
                new Dictionary<string, object?>
                {
                    ["active"] = false,
                    ["state"] = "unknown",
                    ["note"] = "no tracked agentic request found for this requestId"
                },
                [],
                [],
                new AgenticMeta(
                    "agentic.v1",
                    CliVersion.Protocol,
                    0,
                    DateTime.UtcNow.ToString("O")));
            return JsonSerializer.Serialize(notFoundEnvelope, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }

        var warnings = new List<AgenticWarning>();
        if (string.Equals(requestStatus.State, "error", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(requestStatus.ErrorMessage))
        {
            warnings.Add(new AgenticWarning(
                requestStatus.ErrorCode ?? "W_EXEC",
                requestStatus.ErrorMessage!));
        }

        var envelope = new AgenticResponseEnvelope(
            "success",
            requestId,
            requestStatus.Mode,
            "status",
            new Dictionary<string, object?>
            {
                ["active"] = string.Equals(requestStatus.State, "running", StringComparison.OrdinalIgnoreCase),
                ["state"] = requestStatus.State,
                ["sessionSeed"] = requestStatus.SessionSeed,
                ["command"] = requestStatus.CommandText,
                ["outputMode"] = requestStatus.OutputMode,
                ["startedAtUtc"] = requestStatus.StartedAtUtc,
                ["completedAtUtc"] = requestStatus.CompletedAtUtc,
                ["exitCode"] = requestStatus.ExitCode,
                ["action"] = requestStatus.Action
            },
            [],
            warnings,
            new AgenticMeta(
                "agentic.v1",
                CliVersion.Protocol,
                0,
                DateTime.UtcNow.ToString("O")));
        return JsonSerializer.Serialize(envelope, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private static string BuildAgenticValidationError(string message)
    {
        var envelope = new AgenticResponseEnvelope(
            "error",
            Guid.NewGuid().ToString("N"),
            "none",
            "exec",
            null,
            [new AgenticError("E_PARSE", message)],
            [],
            new AgenticMeta(
                "agentic.v1",
                CliVersion.Protocol,
                2,
                DateTime.UtcNow.ToString("O")));
        return JsonSerializer.Serialize(envelope, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private static async Task<string> ExecuteAgenticExecAsync(
        AgenticExecutionRequest request,
        DaemonServiceOptions daemonOptions,
        CancellationToken cancellationToken = default)
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            return BuildAgenticValidationError("unable to resolve process path for agent exec");
        }

        var format = string.IsNullOrWhiteSpace(request.OutputMode) ? "json" : request.OutputMode.Trim().ToLowerInvariant();
        if (format is not "json" and not "yaml")
        {
            format = "json";
        }

        var mode = string.IsNullOrWhiteSpace(request.ContextMode) ? "project" : request.ContextMode.Trim().ToLowerInvariant();
        if (mode is not ("project" or "hierarchy" or "inspector"))
        {
            mode = "project";
        }

        var sessionSeed = AgenticStatePersistenceService.NormalizeSessionSeed(request.SessionSeed);
        var requestId = string.IsNullOrWhiteSpace(request.RequestId) ? Guid.NewGuid().ToString("N") : request.RequestId;

        var psi = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add(request.CommandText);
        psi.ArgumentList.Add("--agentic");
        psi.ArgumentList.Add("--format");
        psi.ArgumentList.Add(format);
        psi.ArgumentList.Add("--mode");
        psi.ArgumentList.Add(mode);
        psi.ArgumentList.Add("--attach-port");
        psi.ArgumentList.Add(daemonOptions.Port.ToString());
        psi.ArgumentList.Add("--session-seed");
        psi.ArgumentList.Add(sessionSeed);
        if (!string.IsNullOrWhiteSpace(daemonOptions.ProjectPath))
        {
            psi.ArgumentList.Add("--project");
            psi.ArgumentList.Add(daemonOptions.ProjectPath);
        }

        psi.ArgumentList.Add("--request-id");
        psi.ArgumentList.Add(requestId);

        AgenticStatePersistenceService.MarkRequestStarted(requestId, sessionSeed, request.CommandText, format);
        using var process = Process.Start(psi);
        if (process is null)
        {
            AgenticStatePersistenceService.MarkRequestCompleted(
                requestId,
                sessionSeed,
                request.CommandText,
                format,
                processExitCode: 1,
                BuildAgenticValidationError("failed to spawn agentic exec process"));
            return BuildAgenticValidationError("failed to spawn agentic exec process");
        }

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);
        var completed = true;
        try
        {
            await process.WaitForExitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException)
        {
            completed = false;
        }
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        if (!completed)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            var timeoutEnvelope = new AgenticResponseEnvelope(
                "error",
                requestId,
                mode,
                "exec",
                null,
                [new AgenticError(
                    cancellationToken.IsCancellationRequested ? "E_CANCELED" : "E_TIMEOUT",
                    cancellationToken.IsCancellationRequested ? "agent exec canceled" : "agent exec timed out after 120s")],
                [],
                new AgenticMeta("agentic.v1", CliVersion.Protocol, cancellationToken.IsCancellationRequested ? 5 : 3, DateTime.UtcNow.ToString("O")));
            var timeoutPayload = JsonSerializer.Serialize(timeoutEnvelope, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            AgenticStatePersistenceService.MarkRequestCompleted(
                requestId,
                sessionSeed,
                request.CommandText,
                format,
                cancellationToken.IsCancellationRequested ? 130 : 3,
                timeoutPayload);
            return timeoutPayload;
        }

        if (!string.IsNullOrWhiteSpace(stdout))
        {
            var payload = stdout.Trim();
            AgenticStatePersistenceService.MarkRequestCompleted(
                requestId,
                sessionSeed,
                request.CommandText,
                format,
                process.ExitCode,
                payload);
            return payload;
        }

        var fallbackEnvelope = new AgenticResponseEnvelope(
            "error",
            requestId,
            mode,
            "exec",
            null,
            [new AgenticError("E_INTERNAL", $"agent exec returned empty payload (exit={process.ExitCode}, stderr={stderr.Trim()})")],
            [],
            new AgenticMeta("agentic.v1", CliVersion.Protocol, 4, DateTime.UtcNow.ToString("O")));
        var fallbackPayload = JsonSerializer.Serialize(fallbackEnvelope, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        AgenticStatePersistenceService.MarkRequestCompleted(
            requestId,
            sessionSeed,
            request.CommandText,
            format,
            process.ExitCode,
            fallbackPayload);
        return fallbackPayload;
    }

    private static string FormatUptime(DateTime startedAtUtc)
    {
        var elapsed = DateTime.UtcNow - startedAtUtc;
        if (elapsed.TotalSeconds < 0)
        {
            elapsed = TimeSpan.Zero;
        }

        if (elapsed.TotalHours >= 1)
        {
            return $"{(int)elapsed.TotalHours}h {elapsed.Minutes}m";
        }

        if (elapsed.TotalMinutes >= 1)
        {
            return $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds}s";
        }

        return $"{(int)elapsed.TotalSeconds}s";
    }

    private static List<string> TokenizeArgs(string input)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        foreach (var ch in input)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (!inQuotes && char.IsWhiteSpace(ch))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(ch);
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    /// <summary>
    /// Scans <c>{projectPath}/Assets/</c> for <c>.cs</c> files and logs a warning if any were
    /// modified after the daemon started (indicating the daemon may be serving stale compiled code).
    /// </summary>
    private static void WarnIfProjectSourceStale(DaemonInstance daemon, Action<string> log)
    {
        if (string.IsNullOrWhiteSpace(daemon.ProjectPath)) return;

        var assetsPath = Path.Combine(daemon.ProjectPath, "Assets");
        if (!Directory.Exists(assetsPath)) return;

        try
        {
            var newestSource = Directory
                .EnumerateFiles(assetsPath, "*.cs", SearchOption.AllDirectories)
                .Select(f => new FileInfo(f).LastWriteTimeUtc)
                .DefaultIfEmpty(DateTime.MinValue)
                .Max();

            // Allow a 5-second grace period to account for filesystem timestamp skew.
            if (newestSource > daemon.StartedAtUtc.AddSeconds(5))
            {
                log($"[yellow]daemon[/]: stale compiled code suspected — Unity source files in Assets/ were modified after daemon start " +
                    $"({newestSource:yyyy-MM-dd HH:mm:ss}Z > {daemon.StartedAtUtc:yyyy-MM-dd HH:mm:ss}Z); " +
                    $"consider [white]/daemon restart[/] to recompile");
            }
        }
        catch
        {
            // Non-critical warning; ignore enumeration errors.
        }
    }

    internal sealed record DaemonStartupFailure(
        bool IsCompileError,
        string Summary,
        IReadOnlyList<string> Lines);
}
