using Spectre.Console;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;

internal sealed class DaemonControlService
{
    private const int DefaultInactivityTimeoutSeconds = 600;
    private const int ProcessOutputTailMaxLines = 40;
    private static readonly TimeSpan ProjectCommandReadyTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan UnityBridgeAttachWaitTimeout = TimeSpan.FromMinutes(3);
    private static readonly HttpClient Http = new();
    private DaemonStartupFailure? _lastStartupFailure;

    public async Task HandleDaemonCommandAsync(
        string input,
        string trigger,
        DaemonRuntime runtime,
        CliSessionState session,
        Action<string> log,
        List<string> streamLog)
    {
        switch (trigger)
        {
            case "/daemon start":
                await HandleDaemonStartAsync(input, runtime, session, log);
                break;
            case "/daemon stop":
                await HandleDaemonStopAsync(runtime, session, log);
                break;
            case "/daemon restart":
                await HandleDaemonRestartAsync(runtime, session, log);
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
                log("[yellow]daemon[/]: usage /daemon <start|stop|restart|ps|attach|detach>");
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
            }
        }

        options = new DaemonServiceOptions(port, unityPath, projectPath, headless, ttlSeconds);
        return true;
    }

    public static async Task RunDaemonServiceAsync(DaemonServiceOptions options, CancellationToken cancellationToken = default)
    {
        var runtimePath = Path.Combine(Environment.CurrentDirectory, ".unifocl-runtime");
        var runtime = new DaemonRuntime(runtimePath);
        var pid = Environment.ProcessId;
        var startedAtUtc = DateTime.UtcNow;
        var state = new DaemonInstance(options.Port, pid, startedAtUtc, options.UnityPath, options.Headless, options.ProjectPath, DateTime.UtcNow);
        var hierarchyBridge = new HierarchyDaemonBridge(options.ProjectPath);
        using var assetIndexBridge = new AssetIndexDaemonBridge(options.ProjectPath);
        var inspectorBridge = new InspectorDaemonBridge();
        var projectBridge = new ProjectDaemonBridge(options.ProjectPath);

        runtime.Upsert(state);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        var lastActivityUtc = DateTime.UtcNow;
        var requestTasks = new ConcurrentBag<Task>();

        var heartbeatTask = Task.Run(async () =>
        {
            while (await timer.WaitForNextTickAsync(cts.Token))
            {
                runtime.Upsert(state with { LastHeartbeatUtc = DateTime.UtcNow });

                if (DateTime.UtcNow - lastActivityUtc >= TimeSpan.FromSeconds(options.InactivityTimeoutSeconds))
                {
                    cts.Cancel();
                    break;
                }
            }
        }, cts.Token);

        HttpListener? listener = null;
        try
        {
            listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{options.Port}/");
            listener.Start();

            while (!cts.Token.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync().WaitAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (HttpListenerException)
                {
                    break;
                }

                var requestTask = HandleDaemonRequestAsync(
                    context,
                    hierarchyBridge,
                    assetIndexBridge,
                    inspectorBridge,
                    projectBridge,
                    options,
                    cts.Token,
                    () => lastActivityUtc = DateTime.UtcNow,
                    () => cts.Cancel());
                requestTasks.Add(requestTask);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (HttpListenerException)
        {
        }
        finally
        {
            cts.Cancel();

            if (listener is not null)
            {
                try
                {
                    listener.Stop();
                }
                catch (ObjectDisposedException)
                {
                }
            }

            try
            {
                await heartbeatTask;
            }
            catch (OperationCanceledException)
            {
            }

            await AwaitBackgroundTasksAsync(requestTasks, TimeSpan.FromSeconds(5));
            runtime.Remove(options.Port);
        }
    }

    private static async Task HandleDaemonRequestAsync(
        HttpListenerContext context,
        HierarchyDaemonBridge hierarchyBridge,
        AssetIndexDaemonBridge assetIndexBridge,
        InspectorDaemonBridge inspectorBridge,
        ProjectDaemonBridge projectBridge,
        DaemonServiceOptions options,
        CancellationToken cancellationToken,
        Action touchActivity,
        Action requestShutdown)
    {
        var request = context.Request;
        var path = (request.Url?.AbsolutePath ?? "/").TrimEnd('/');
        if (path.Length == 0)
        {
            path = "/";
        }

        try
        {
            if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) && path.Equals("/ping", StringComparison.OrdinalIgnoreCase))
            {
                await WriteTextResponseAsync(context.Response, "PONG", cancellationToken: cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) && path.Equals("/touch", StringComparison.OrdinalIgnoreCase))
            {
                touchActivity();
                await WriteTextResponseAsync(context.Response, "OK", cancellationToken: cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) && path.Equals("/stop", StringComparison.OrdinalIgnoreCase))
            {
                await WriteTextResponseAsync(context.Response, "STOPPING", cancellationToken: cancellationToken);
                requestShutdown();
                return;
            }

            if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) && path.Equals("/asset-index", StringComparison.OrdinalIgnoreCase))
            {
                var revisionRaw = request.QueryString["revision"];
                var command = int.TryParse(revisionRaw, out var revision) && revision > 0
                    ? $"ASSET_INDEX_SYNC {revision}"
                    : "ASSET_INDEX_GET";
                if (assetIndexBridge.TryHandle(command, out var assetResponse))
                {
                    touchActivity();
                    await WriteJsonResponseAsync(context.Response, assetResponse, cancellationToken: cancellationToken);
                    return;
                }
            }

            if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) && path.Equals("/hierarchy/snapshot", StringComparison.OrdinalIgnoreCase))
            {
                if (hierarchyBridge.TryHandle("HIERARCHY_GET", out var hierarchySnapshot))
                {
                    touchActivity();
                    await WriteJsonResponseAsync(context.Response, hierarchySnapshot, cancellationToken: cancellationToken);
                    return;
                }
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) && path.Equals("/hierarchy/command", StringComparison.OrdinalIgnoreCase))
            {
                var payload = await ReadRequestBodyAsync(request, cancellationToken);
                if (hierarchyBridge.TryHandle($"HIERARCHY_CMD {payload}", out var hierarchyResponse))
                {
                    touchActivity();
                    await WriteJsonResponseAsync(context.Response, hierarchyResponse, cancellationToken: cancellationToken);
                    return;
                }
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) && path.Equals("/hierarchy/find", StringComparison.OrdinalIgnoreCase))
            {
                var payload = await ReadRequestBodyAsync(request, cancellationToken);
                if (hierarchyBridge.TryHandle($"HIERARCHY_FIND {payload}", out var hierarchySearch))
                {
                    touchActivity();
                    await WriteJsonResponseAsync(context.Response, hierarchySearch, cancellationToken: cancellationToken);
                    return;
                }
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) && path.Equals("/inspect", StringComparison.OrdinalIgnoreCase))
            {
                var payload = await ReadRequestBodyAsync(request, cancellationToken);
                if (inspectorBridge.TryHandle($"INSPECT {payload}", out var inspectorResponse))
                {
                    touchActivity();
                    await WriteJsonResponseAsync(context.Response, inspectorResponse, cancellationToken: cancellationToken);
                    return;
                }
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) && path.Equals("/project/command", StringComparison.OrdinalIgnoreCase))
            {
                var payload = await ReadRequestBodyAsync(request, cancellationToken);
                if (projectBridge.TryHandle($"PROJECT_CMD {payload}", out var projectResponse))
                {
                    touchActivity();
                    await WriteJsonResponseAsync(context.Response, projectResponse, cancellationToken: cancellationToken);
                    return;
                }
            }

            if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) && path.Equals("/agent/capabilities", StringComparison.OrdinalIgnoreCase))
            {
                touchActivity();
                await WriteJsonResponseAsync(context.Response, BuildAgenticCapabilitiesPayload(), cancellationToken: cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) && path.Equals("/agent/status", StringComparison.OrdinalIgnoreCase))
            {
                touchActivity();
                var requestId = request.QueryString["requestId"] ?? string.Empty;
                await WriteJsonResponseAsync(context.Response, BuildAgenticStatusPayload(requestId), cancellationToken: cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) && path.StartsWith("/agent/dump/", StringComparison.OrdinalIgnoreCase))
            {
                var category = path["/agent/dump/".Length..].Trim();
                var format = request.QueryString["format"];
                var dumpCommand = string.IsNullOrWhiteSpace(format)
                    ? $"/dump {category}"
                    : $"/dump {category} --format {format}";
                var payload = await ExecuteAgenticExecAsync(new AgenticExecutionRequest(
                    dumpCommand,
                    "project",
                    string.Empty,
                    string.IsNullOrWhiteSpace(format) ? "json" : format!,
                    Guid.NewGuid().ToString("N")), options, cancellationToken);
                touchActivity();
                await WriteJsonResponseAsync(context.Response, payload, cancellationToken: cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) && path.Equals("/agent/exec", StringComparison.OrdinalIgnoreCase))
            {
                var payload = await ReadRequestBodyAsync(request, cancellationToken);
                AgenticExecutionRequest? parsed;
                try
                {
                    parsed = JsonSerializer.Deserialize<AgenticExecutionRequest>(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                }
                catch
                {
                    parsed = null;
                }

                if (parsed is null || string.IsNullOrWhiteSpace(parsed.CommandText))
                {
                    touchActivity();
                    await WriteJsonResponseAsync(
                        context.Response,
                        BuildAgenticValidationError("invalid /agent/exec payload"),
                        cancellationToken: cancellationToken);
                    return;
                }

                var normalizedRequest = parsed with
                {
                    SessionSeed = AgenticStatePersistenceService.NormalizeSessionSeed(parsed.SessionSeed)
                };
                var responsePayload = await ExecuteAgenticExecAsync(normalizedRequest, options, cancellationToken);
                touchActivity();
                await WriteJsonResponseAsync(context.Response, responsePayload, cancellationToken: cancellationToken);
                return;
            }

            touchActivity();
            await WriteTextResponseAsync(context.Response, "ERR", statusCode: 404, cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            if (!cancellationToken.IsCancellationRequested && context.Response.OutputStream.CanWrite)
            {
                try
                {
                    await WriteTextResponseAsync(context.Response, "ERR", statusCode: 500, cancellationToken: cancellationToken);
                }
                catch
                {
                }
            }
        }
    }

    private static async Task AwaitBackgroundTasksAsync(IEnumerable<Task> tasks, TimeSpan timeout)
    {
        var pending = tasks.Where(task => task is not null).ToArray();
        if (pending.Length == 0)
        {
            return;
        }

        try
        {
            using var timeoutCts = new CancellationTokenSource(timeout);
            await Task.WhenAll(pending).WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Best effort drain for background tasks; bounded to avoid shutdown hang.
        }
        catch
        {
        }
    }

    private static async Task<string> ReadRequestBodyAsync(HttpListenerRequest request, CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    private static async Task WriteTextResponseAsync(
        HttpListenerResponse response,
        string payload,
        int statusCode = 200,
        CancellationToken cancellationToken = default)
    {
        var bytes = Encoding.UTF8.GetBytes(payload + Environment.NewLine);
        response.StatusCode = statusCode;
        response.ContentType = "text/plain; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        try
        {
            await response.OutputStream.WriteAsync(bytes.AsMemory(), cancellationToken);
        }
        finally
        {
            response.Close();
        }
    }

    private static async Task WriteJsonResponseAsync(
        HttpListenerResponse response,
        string jsonPayload,
        int statusCode = 200,
        CancellationToken cancellationToken = default)
    {
        var bytes = Encoding.UTF8.GetBytes(jsonPayload + Environment.NewLine);
        response.StatusCode = statusCode;
        response.ContentType = "application/json; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        try
        {
            await response.OutputStream.WriteAsync(bytes.AsMemory(), cancellationToken);
        }
        finally
        {
            response.Close();
        }
    }

    public async Task<bool> EnsureProjectDaemonAsync(
        string projectPath,
        DaemonRuntime runtime,
        CliSessionState session,
        Action<string> log,
        bool requireBridgeMode = false,
        bool preferHostMode = false,
        bool allowUnsafe = false)
    {
        var port = ResolveProjectDaemonPort(projectPath);
        var existing = runtime.GetByPort(port);
        var unityEditorActive = IsUnityClientActiveForProject(projectPath);

        // Unity's InitializeOnLoad Bridge mode endpoint can already be serving this port even when it's not in runtime registry.
        if (await TryAttachProjectDaemonAsync(projectPath, session, attemptCount: 1))
        {
            var projectCommandReady = await IsProjectCommandEndpointResponsiveAsync(port, TimeSpan.FromSeconds(3));
            if (!projectCommandReady)
            {
                if (unityEditorActive)
                {
                    log($"[grey]daemon[/]: endpoint 127.0.0.1:{port} is reachable; waiting for Unity editor bridge command readiness");
                    var bridgeWait = await WaitForUnityBridgeAttachWithDiagnosticsAsync(projectPath, port, session, log, UnityBridgeAttachWaitTimeout);
                    if (bridgeWait.Attached)
                    {
                        return true;
                    }

                    if (bridgeWait.EditorClosedDuringWait)
                    {
                        log("[yellow]daemon[/]: Unity editor closed while waiting for Bridge mode; continuing with Host mode startup");
                    }
                    else
                    {
                        _lastStartupFailure = new DaemonStartupFailure(
                            IsCompileError: false,
                            Summary: $"Unity editor bridge endpoint is reachable but project commands are not ready on port {port} ({bridgeWait.DiagnosticSummary})",
                            Lines: []);
                        log($"[red]daemon[/]: Unity editor bridge is not ready on [white]127.0.0.1:{port}[/] ({Markup.Escape(bridgeWait.DiagnosticSummary)})");
                        return false;
                    }
                }
                else
                {
                    log($"[yellow]daemon[/]: endpoint 127.0.0.1:{port} responds to ping but project commands are unresponsive; restarting bridge");
                    await TrySendControlAsync(port, "STOP", "STOPPING");
                    session.AttachedPort = null;
                    await Task.Delay(200);
                }
            }
            else
            {
                var bridgeSatisfied = !requireBridgeMode || SupportsBridgeMode(existing);
                var hostModeSatisfied = !preferHostMode || (existing?.Headless ?? false);
                var managedRuntimePresent = existing is not null;

                if (managedRuntimePresent && bridgeSatisfied && hostModeSatisfied)
                {
                    return true;
                }

                if (!preferHostMode && bridgeSatisfied)
                {
                    return true;
                }

                log($"[yellow]daemon[/]: endpoint 127.0.0.1:{port} is attachable but unmanaged; restarting in managed Host mode");
                await TrySendControlAsync(port, "STOP", "STOPPING");
                session.AttachedPort = null;
                await Task.Delay(200);
            }
        }

        if (unityEditorActive)
        {
            log($"[grey]daemon[/]: Unity editor lock detected for project; waiting for Bridge mode endpoint on [white]127.0.0.1:{port}[/]");
            var bridgeWait = await WaitForUnityBridgeAttachWithDiagnosticsAsync(projectPath, port, session, log, UnityBridgeAttachWaitTimeout);
            if (bridgeWait.Attached)
            {
                return true;
            }

            if (bridgeWait.EditorClosedDuringWait)
            {
                log("[yellow]daemon[/]: Unity editor closed while waiting for Bridge mode; continuing with Host mode startup");
            }
            else
            {
                _lastStartupFailure = new DaemonStartupFailure(
                    IsCompileError: false,
                    Summary: $"Unity editor lock detected for project, but bridge endpoint 127.0.0.1:{port} is not attachable ({bridgeWait.DiagnosticSummary})",
                    Lines: []);
                log($"[red]daemon[/]: Unity editor is already running for this project, but Bridge mode endpoint [white]127.0.0.1:{port}[/] is not attachable");
                log($"[yellow]daemon[/]: bridge diagnostics -> {Markup.Escape(bridgeWait.DiagnosticSummary)}");
                log("[yellow]daemon[/]: Host mode launch is skipped while Unity lock is active to avoid Unity file-lock startup failure");
                return false;
            }
        }

        if (existing is not null)
        {
            var stopped = await TrySendControlAsync(port, "STOP", "STOPPING");
            if (stopped)
            {
                var deadline = DateTime.UtcNow.AddSeconds(4);
                while (DateTime.UtcNow < deadline && ProcessUtil.IsAlive(existing.Pid))
                {
                    await Task.Delay(120);
                }
            }

            runtime.Remove(port);
        }

        var unityPath = ResolveDefaultUnityPath(projectPath);
        if (requireBridgeMode && string.IsNullOrWhiteSpace(unityPath))
        {
            log("[red]daemon[/]: hierarchy asset load requires Bridge mode, but no Unity editor path is configured");
            return false;
        }

        var startOptions = new DaemonStartOptions(
            port,
            unityPath,
            projectPath,
            Headless: true,
            AllowUnsafe: allowUnsafe);

        var started = await StartDaemonAsync(startOptions, runtime, log);
        if (!started)
        {
            return false;
        }

        session.AttachedPort = port;
        await TrySendControlAsync(port, "TOUCH", "OK");
        var projectCommandReadyAfterStart = await WaitForProjectCommandReadyAsync(
            port,
            ProjectCommandReadyTimeout,
            elapsed => log($"[grey]daemon[/]: waiting for project command endpoint... {elapsed.TotalSeconds:0}s elapsed"));
        if (!projectCommandReadyAfterStart)
        {
            var probe = await ProbeProjectCommandEndpointAsync(port, TimeSpan.FromSeconds(8));
            _lastStartupFailure = new DaemonStartupFailure(
                IsCompileError: false,
                Summary: $"project command endpoint is not ready on port {port} ({probe.Detail})",
                Lines: []);
            log($"[red]daemon[/]: project command endpoint is not ready on [white]127.0.0.1:{port}[/] ({Markup.Escape(probe.Detail)})");
            await TrySendControlAsync(port, "STOP", "STOPPING");
            var launched = runtime.GetByPort(port);
            if (launched is not null)
            {
                var deadline = DateTime.UtcNow.AddSeconds(4);
                while (DateTime.UtcNow < deadline && ProcessUtil.IsAlive(launched.Pid))
                {
                    await Task.Delay(120);
                }
            }

            runtime.Remove(port);
            session.AttachedPort = null;
            return false;
        }

        return true;
    }

    public async Task<bool> HasStableProjectDaemonAsync(
        string projectPath,
        DaemonRuntime runtime,
        CliSessionState session,
        bool requireManagedRuntime = false)
    {
        var port = ResolveProjectDaemonPort(projectPath);
        if (session.AttachedPort != port)
        {
            return false;
        }

        if (requireManagedRuntime)
        {
            var instance = runtime.GetByPort(port);
            if (instance is null || string.IsNullOrWhiteSpace(instance.UnityPath))
            {
                return false;
            }
        }

        if (!await TrySendControlAsync(port, "PING", "PONG"))
        {
            return false;
        }

        return await WaitForProjectCommandReadyAsync(port, TimeSpan.FromSeconds(8));
    }

    public async Task<bool> HasStableManagedProjectDaemonAsync(string projectPath, DaemonRuntime runtime, CliSessionState session)
    {
        return await HasStableProjectDaemonAsync(projectPath, runtime, session, requireManagedRuntime: true);
    }

    private static bool SupportsBridgeMode(DaemonInstance? instance)
    {
        if (instance is null)
        {
            // Port is serving but not from runtime registry (likely InitializeOnLoad Bridge mode).
            return true;
        }

        return !string.IsNullOrWhiteSpace(instance.UnityPath);
    }

    public async Task<bool> TryAttachProjectDaemonAsync(
        string projectPath,
        CliSessionState session,
        Action<string>? log = null,
        int attemptCount = 8,
        int attemptDelayMs = 250)
    {
        var port = ResolveProjectDaemonPort(projectPath);
        var normalizedAttemptCount = Math.Max(1, attemptCount);
        var normalizedDelayMs = Math.Max(50, attemptDelayMs);

        for (var attempt = 1; attempt <= normalizedAttemptCount; attempt++)
        {
            if (await TrySendControlAsync(port, "PING", "PONG"))
            {
                session.AttachedPort = port;
                await TrySendControlAsync(port, "TOUCH", "OK");
                return true;
            }

            if (attempt < normalizedAttemptCount)
            {
                await Task.Delay(normalizedDelayMs);
            }
        }

        log?.Invoke($"[yellow]daemon[/]: project daemon endpoint 127.0.0.1:{port} is not ready for attachment");
        return false;
    }

    public async Task<bool> TouchAttachedDaemonAsync(CliSessionState session)
    {
        if (session.AttachedPort is null)
        {
            return false;
        }

        return await TrySendControlAsync(session.AttachedPort.Value, "TOUCH", "OK");
    }

    public async Task<bool> StopDaemonByPortAsync(
        int port,
        DaemonRuntime runtime,
        CliSessionState session,
        Action<string> log)
    {
        runtime.CleanStaleEntries();
        var target = runtime.GetByPort(port);
        if (target is null)
        {
            var pingOk = await TrySendControlAsync(port, "PING", "PONG");
            if (pingOk)
            {
                var stopAttachedOnly = await TrySendControlAsync(port, "STOP", "STOPPING");
                if (!stopAttachedOnly)
                {
                    log($"[red]daemon[/]: failed to stop live endpoint on port {port}");
                    return false;
                }
            }

            runtime.Remove(port);
            if (session.AttachedPort == port)
            {
                session.AttachedPort = null;
            }

            return true;
        }

        var stopOk = await TrySendControlAsync(target.Port, "STOP", "STOPPING");
        if (!stopOk)
        {
            log($"[red]daemon[/]: failed to stop daemon on port {target.Port} via control socket");
            return false;
        }

        var deadline = DateTime.UtcNow.AddSeconds(4);
        while (DateTime.UtcNow < deadline && ProcessUtil.IsAlive(target.Pid))
        {
            await Task.Delay(120);
        }

        if (ProcessUtil.IsAlive(target.Pid))
        {
            if (target.Headless)
            {
                TryTerminateHostModeByPid(target.Pid, log);
            }
            else
            {
                log($"[yellow]daemon[/]: process pid={target.Pid} is still alive after stop request; skipped force-kill because daemon is not running in Host mode");
            }
        }

        if (ProcessUtil.IsAlive(target.Pid))
        {
            log($"[red]daemon[/]: daemon process pid={target.Pid} is still alive after shutdown");
            return false;
        }

        runtime.Remove(target.Port);
        if (session.AttachedPort == target.Port)
        {
            session.AttachedPort = null;
        }

        return true;
    }

    public static bool IsUnityClientActiveForProject(string projectPath)
    {
        var lockFile = Path.Combine(projectPath, "Temp", "UnityLockfile");
        if (!File.Exists(lockFile))
        {
            return false;
        }

        try
        {
            return Process.GetProcessesByName("Unity").Any() || Process.GetProcessesByName("Unity Editor").Any();
        }
        catch
        {
            return false;
        }
    }

    public static int ComputeProjectDaemonPort(string projectPath)
    {
        var normalized = Path.GetFullPath(projectPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Replace('\\', '/');

        unchecked
        {
            var hash = 17;
            foreach (var ch in normalized)
            {
                hash = (hash * 31) + ch;
            }

            return 18080 + ((hash & 0x7fffffff) % 2000);
        }
    }

    public static int ResolveProjectDaemonPort(string projectPath)
    {
        var bridgePath = Path.Combine(projectPath, ".unifocl", "bridge.json");
        try
        {
            if (File.Exists(bridgePath))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(bridgePath));
                if (document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("daemon", out var daemonElement)
                    && daemonElement.ValueKind == JsonValueKind.Object
                    && daemonElement.TryGetProperty("port", out var portElement)
                    && portElement.TryGetInt32(out var configuredPort)
                    && configuredPort is > 0 and <= 65535)
                {
                    return configuredPort;
                }
            }
        }
        catch
        {
            // Ignore malformed local bridge config and fall back to deterministic port.
        }

        return ComputeProjectDaemonPort(projectPath);
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
                    session.AttachedPort = null;
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

    private async Task<bool> StartDaemonAsync(DaemonStartOptions startOptions, DaemonRuntime runtime, Action<string> log)
    {
        var existing = runtime.GetByPort(startOptions.Port);
        if (existing is not null && ProcessUtil.IsAlive(existing.Pid) && await TrySendControlAsync(existing.Port, "PING", "PONG"))
        {
            log($"[yellow]daemon[/]: port {startOptions.Port} already has a running daemon (pid {existing.Pid})");
            return true;
        }

        if (existing is not null)
        {
            runtime.Remove(startOptions.Port);
        }

        var launch = ResolveDaemonLaunch(startOptions);
        var process = Process.Start(launch);
        if (process is null)
        {
            log("[red]daemon[/]: failed to start daemon process");
            return false;
        }
        var launchedAtUtc = DateTime.UtcNow;

        var outputTail = new Queue<string>();
        var outputLines = new ConcurrentQueue<string>();
        using var outputDrainCts = new CancellationTokenSource();
        var outputDrainTasks = Array.Empty<Task>();
        if (startOptions.Headless)
        {
            var mode = startOptions.AllowUnsafe ? "unsafe (UPM disabled, ignore compile errors)" : "safe (UPM enabled)";
            log($"[grey]daemon[/]: starting Unity in Host mode on port {startOptions.Port} ({mode})");
        }
        outputDrainTasks = StartBackgroundOutputDrain(
            process,
            startOptions.Headless,
            outputDrainCts.Token,
            line =>
            {
                CaptureProcessOutputLine(outputTail, line);
                outputLines.Enqueue(line);
            });

        var startupTimeout = TimeSpan.FromSeconds(25);
        var ready = startOptions.Headless
            ? await WaitForHostModeDaemonReadyAsync(
                startOptions.Port,
                process,
                outputLines,
                elapsed => log($"[grey]daemon[/]: startup in progress... {elapsed.TotalSeconds:0}s elapsed"),
                line => log($"[grey]unity[/]: {Markup.Escape(line)}"))
            : await WaitForDaemonReadyAsync(startOptions.Port, startupTimeout);
        if (!ready)
        {
            while (outputLines.TryDequeue(out var line))
            {
                log($"[grey]unity[/]: {Markup.Escape(line)}");
            }

            if (process.HasExited)
            {
                var details = BuildProcessFailureSummary(outputTail);
                var compileLines = ExtractCompileErrorLines(outputTail);
                _lastStartupFailure = new DaemonStartupFailure(
                    IsCompileError: compileLines.Count > 0,
                    Summary: string.IsNullOrWhiteSpace(details) ? $"process exited with code {process.ExitCode}" : details,
                    Lines: compileLines.Count > 0 ? compileLines : outputTail.ToList());
                log($"[red]daemon[/]: process exited before daemon became ready (pid {process.Id}, exit {process.ExitCode})");
                if (!string.IsNullOrWhiteSpace(details))
                {
                    log($"[red]daemon[/]: startup output -> {Markup.Escape(details)}");
                }
            }
            else
            {
                _lastStartupFailure = new DaemonStartupFailure(
                    IsCompileError: false,
                    Summary: $"daemon did not respond on port {startOptions.Port} within {startupTimeout.TotalSeconds:0}s",
                    Lines: outputTail.ToList());
                log($"[red]daemon[/]: process launched (pid {process.Id}) but not responding on port {startOptions.Port} within {startupTimeout.TotalSeconds:0}s");
                TryTerminateSpawnedProcess(process, log);
            }

            runtime.Remove(startOptions.Port);
            outputDrainCts.Cancel();
            await AwaitBackgroundTasksAsync(outputDrainTasks, TimeSpan.FromSeconds(2));
            return false;
        }

        // Host mode Unity launch does not self-register in daemon runtime metadata.
        if (startOptions.Headless)
        {
            runtime.Upsert(new DaemonInstance(
                startOptions.Port,
                process.Id,
                launchedAtUtc,
                startOptions.UnityPath,
                startOptions.Headless,
                startOptions.ProjectPath,
                DateTime.UtcNow));
        }

        _lastStartupFailure = null;
        outputDrainCts.Cancel();
        await AwaitBackgroundTasksAsync(outputDrainTasks, TimeSpan.FromSeconds(2));
        log($"[green]daemon[/]: started [white]pid={process.Id}[/] [white]port={startOptions.Port}[/] [white]mode={(startOptions.Headless ? "host" : "bridge")}[/]");
        return true;
    }

    public bool TryGetLastStartupFailure(out DaemonStartupFailure? failure)
    {
        failure = _lastStartupFailure;
        return failure is not null;
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

            if (session.AttachedPort == attachedPort)
            {
                session.AttachedPort = null;
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
            unityPath = ResolveDefaultUnityPath(projectPath);
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
        if (instances.Count == 0 && session.AttachedPort is int attachedPortProbe)
        {
            hasLiveAttachedOnly = await TrySendControlAsync(attachedPortProbe, "PING", "PONG");
            if (!hasLiveAttachedOnly)
            {
                session.AttachedPort = null;
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
                session.AttachedPort == instance.Port ? "yes" : "no");
        }

        if (session.AttachedPort is int attachedPort && instances.All(i => i.Port != attachedPort))
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
                session.AttachedPort = null;
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
                session.AttachedPort == projectBridgePort ? "yes" : "no");
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

        session.AttachedPort = port;
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
        if (session.AttachedPort is null)
        {
            log("[yellow]daemon[/]: no daemon attached");
            return;
        }

        var detachedPort = session.AttachedPort.Value;
        session.AttachedPort = null;
        log($"[green]daemon[/]: detached from port {detachedPort}; daemon kept running");
    }

    private static DaemonInstance? ResolveTargetDaemon(DaemonRuntime runtime, CliSessionState session)
    {
        runtime.CleanStaleEntries();
        var instances = runtime.GetAll().ToList();
        if (instances.Count == 0)
        {
            return null;
        }

        if (session.AttachedPort is int attachedPort)
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

    private static ProcessStartInfo ResolveDaemonLaunch(DaemonStartOptions options)
    {
        if (options.Headless && !string.IsNullOrWhiteSpace(options.UnityPath) && !string.IsNullOrWhiteSpace(options.ProjectPath))
        {
            var unityPsi = new ProcessStartInfo(options.UnityPath)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            unityPsi.ArgumentList.Add("-projectPath");
            unityPsi.ArgumentList.Add(options.ProjectPath);
            unityPsi.ArgumentList.Add("-batchmode");
            unityPsi.ArgumentList.Add("-nographics");
            unityPsi.ArgumentList.Add("-vcsMode");
            unityPsi.ArgumentList.Add("None");
            if (options.AllowUnsafe)
            {
                unityPsi.ArgumentList.Add("-noUpm");
                unityPsi.ArgumentList.Add("-ignoreCompileErrors");
            }
            unityPsi.ArgumentList.Add("-executeMethod");
            unityPsi.ArgumentList.Add("UniFocl.EditorBridge.CLIDaemon.StartServer");
            unityPsi.ArgumentList.Add("--daemon-service");
            unityPsi.ArgumentList.Add("--port");
            unityPsi.ArgumentList.Add(options.Port.ToString());
            unityPsi.ArgumentList.Add("--project");
            unityPsi.ArgumentList.Add(options.ProjectPath);
            unityPsi.ArgumentList.Add("--headless");
            unityPsi.ArgumentList.Add("--ttl-seconds");
            unityPsi.ArgumentList.Add(DefaultInactivityTimeoutSeconds.ToString());
            return unityPsi;
        }

        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            throw new InvalidOperationException("Unable to resolve current process path for daemon launch.");
        }

        var daemonArgs = new List<string>
        {
            "--daemon-service",
            "--port", options.Port.ToString(),
            "--ttl-seconds", DefaultInactivityTimeoutSeconds.ToString()
        };

        if (options.UnityPath is not null)
        {
            daemonArgs.Add("--unity");
            daemonArgs.Add(options.UnityPath);
        }

        if (options.ProjectPath is not null)
        {
            daemonArgs.Add("--project");
            daemonArgs.Add(options.ProjectPath);
        }

        if (options.Headless)
        {
            daemonArgs.Add("--headless");
        }

        if (options.AllowUnsafe)
        {
            daemonArgs.Add("--allow-unsafe");
        }

        if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var assemblyPath = Assembly.GetEntryAssembly()?.Location
                               ?? throw new InvalidOperationException("Unable to resolve entry assembly path.");
            var psi = new ProcessStartInfo(processPath)
            {
                UseShellExecute = false,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add(assemblyPath);
            foreach (var arg in daemonArgs)
            {
                psi.ArgumentList.Add(arg);
            }

            return psi;
        }

        var directPsi = new ProcessStartInfo(processPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            CreateNoWindow = true
        };
        foreach (var arg in daemonArgs)
        {
            directPsi.ArgumentList.Add(arg);
        }

        return directPsi;
    }

    private static Task[] StartBackgroundOutputDrain(
        Process process,
        bool headless,
        CancellationToken cancellationToken,
        Action<string>? onLine = null)
    {
        if (!headless)
        {
            return [];
        }

        var tasks = new List<Task>(2);
        if (process.StartInfo.RedirectStandardOutput)
        {
            tasks.Add(DrainProcessOutputAsync(process.StandardOutput, onLine, cancellationToken));
        }

        if (process.StartInfo.RedirectStandardError)
        {
            tasks.Add(DrainProcessOutputAsync(process.StandardError, onLine, cancellationToken));
        }

        return [.. tasks];
    }

    private static async Task DrainProcessOutputAsync(StreamReader reader, Action<string>? onLine, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync().WaitAsync(cancellationToken);
                if (line is null)
                {
                    break;
                }

                onLine?.Invoke(line);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
        }
    }

    private static void CaptureProcessOutputLine(Queue<string> tail, string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        var trimmed = line.Trim();
        if (tail.Count >= ProcessOutputTailMaxLines)
        {
            tail.Dequeue();
        }

        tail.Enqueue(trimmed);
    }

    private static string BuildProcessFailureSummary(Queue<string> outputTail)
    {
        if (outputTail.Count == 0)
        {
            return string.Empty;
        }

        var prioritized = outputTail
            .Where(line => line.Contains("error", StringComparison.OrdinalIgnoreCase)
                           || line.Contains("exception", StringComparison.OrdinalIgnoreCase)
                           || line.Contains("failed", StringComparison.OrdinalIgnoreCase))
            .Take(5)
            .ToList();
        if (prioritized.Count > 0)
        {
            return string.Join(" | ", prioritized);
        }

        return string.Join(" | ", outputTail.TakeLast(3));
    }

    private static List<string> ExtractCompileErrorLines(Queue<string> outputTail)
    {
        var lines = outputTail
            .Where(line =>
                line.Contains("error CS", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Scripts have compiler errors", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Script Compilation Error", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Tundra build failed", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal)
            .Take(30)
            .ToList();
        return lines;
    }

    private static void TryTerminateSpawnedProcess(Process process, Action<string> log)
    {
        try
        {
            if (process.HasExited)
            {
                return;
            }

            process.Kill(entireProcessTree: true);
            if (!process.WaitForExit(5000))
            {
                log($"[yellow]daemon[/]: failed to terminate stalled Unity process pid={process.Id} within 5s");
                return;
            }

            log($"[yellow]daemon[/]: terminated stalled Unity process pid={process.Id}");
        }
        catch (Exception ex)
        {
            log($"[yellow]daemon[/]: unable to terminate stalled Unity process pid={process.Id} ({Markup.Escape(ex.Message)})");
        }
    }

    private static void TryTerminateHostModeByPid(int pid, Action<string> log)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            TryTerminateSpawnedProcess(process, log);
        }
        catch (ArgumentException)
        {
            // Process already exited.
        }
        catch (Exception ex)
        {
            log($"[yellow]daemon[/]: unable to terminate Host mode Unity process pid={pid} ({Markup.Escape(ex.Message)})");
        }
    }

    private static async Task<bool> WaitForDaemonReadyAsync(int port, TimeSpan timeout, Action<TimeSpan>? onProgress = null)
    {
        var startedAt = DateTime.UtcNow;
        var deadline = startedAt.Add(timeout);
        var nextProgressAt = startedAt.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (await TrySendControlAsync(port, "PING", "PONG"))
            {
                return true;
            }

            var now = DateTime.UtcNow;
            if (onProgress is not null && now >= nextProgressAt)
            {
                onProgress(now - startedAt);
                nextProgressAt = now.AddSeconds(5);
            }

            await Task.Delay(180);
        }

        return false;
    }

    private static async Task<bool> WaitForHostModeDaemonReadyAsync(
        int port,
        Process process,
        ConcurrentQueue<string> outputLines,
        Action<TimeSpan>? onProgress = null,
        Action<string>? onOutput = null)
    {
        var startedAt = DateTime.UtcNow;
        var nextProgressAt = startedAt.AddSeconds(5);

        while (true)
        {
            while (outputLines.TryDequeue(out var line))
            {
                onOutput?.Invoke(line);
            }

            if (await TrySendControlAsync(port, "PING", "PONG"))
            {
                return true;
            }

            if (process.HasExited)
            {
                return false;
            }

            var now = DateTime.UtcNow;
            if (onProgress is not null && now >= nextProgressAt)
            {
                onProgress(now - startedAt);
                nextProgressAt = now.AddSeconds(5);
            }

            await Task.Delay(180);
        }
    }

    private static async Task<bool> TrySendControlAsync(int port, string request, string expectedResponse)
    {
        var endpoint = request switch
        {
            "PING" => ("GET", $"http://127.0.0.1:{port}/ping"),
            "TOUCH" => ("POST", $"http://127.0.0.1:{port}/touch"),
            "STOP" => ("POST", $"http://127.0.0.1:{port}/stop"),
            _ => default
        };
        if (string.IsNullOrWhiteSpace(endpoint.Item2))
        {
            return false;
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            HttpResponseMessage response;
            if (endpoint.Item1 == "GET")
            {
                response = await Http.GetAsync(endpoint.Item2, cts.Token);
            }
            else
            {
                response = await Http.PostAsync(endpoint.Item2, new StringContent(string.Empty, Encoding.UTF8, "text/plain"), cts.Token);
            }

            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            var payload = (await response.Content.ReadAsStringAsync(cts.Token)).Trim();
            return string.Equals(payload, expectedResponse, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> IsProjectCommandEndpointResponsiveAsync(int port, TimeSpan timeout)
    {
        var probe = await ProbeProjectCommandEndpointAsync(port, timeout);
        return probe.Ok;
    }

    private static async Task<ProjectCommandProbeResult> ProbeProjectCommandEndpointAsync(int port, TimeSpan timeout)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            using var content = new StringContent("{\"action\":\"healthcheck\"}", Encoding.UTF8, "application/json");
            var response = await Http.PostAsync($"http://127.0.0.1:{port}/project/command", content, cts.Token);
            var payload = (await response.Content.ReadAsStringAsync(cts.Token)).Trim();
            if (!response.IsSuccessStatusCode)
            {
                return new ProjectCommandProbeResult(false, $"HTTP {(int)response.StatusCode}: {(string.IsNullOrWhiteSpace(payload) ? "<empty>" : payload)}");
            }

            if (string.IsNullOrWhiteSpace(payload))
            {
                return new ProjectCommandProbeResult(false, "HTTP 200 with empty payload");
            }

            return new ProjectCommandProbeResult(true, "ok");
        }
        catch (OperationCanceledException)
        {
            return new ProjectCommandProbeResult(false, $"timeout after {(int)timeout.TotalSeconds}s");
        }
        catch (Exception ex)
        {
            return new ProjectCommandProbeResult(false, ex.Message);
        }
    }

    private static async Task<bool> WaitForProjectCommandReadyAsync(
        int port,
        TimeSpan timeout,
        Action<TimeSpan>? onProgress = null)
    {
        var startedAt = DateTime.UtcNow;
        var deadline = startedAt.Add(timeout);
        var nextProgressAt = startedAt.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (await IsProjectCommandEndpointResponsiveAsync(port, TimeSpan.FromSeconds(8)))
            {
                return true;
            }

            var now = DateTime.UtcNow;
            if (onProgress is not null && now >= nextProgressAt)
            {
                onProgress(now - startedAt);
                nextProgressAt = now.AddSeconds(5);
            }

            await Task.Delay(1000);
        }

        return false;
    }

    private readonly record struct ProjectCommandProbeResult(bool Ok, string Detail);

    private static async Task<int?> ResolveAttachedOnlyPortAsync(CliSessionState session)
    {
        if (session.AttachedPort is not int attachedPort)
        {
            return null;
        }

        return await TrySendControlAsync(attachedPort, "PING", "PONG") ? attachedPort : null;
    }

    private static string? ResolveDefaultUnityPath(string projectPath)
    {
        if (UnityEditorPathService.TryGetProjectEditorPath(projectPath, out var projectEditorPath))
        {
            return projectEditorPath;
        }

        if (UnityEditorPathService.TryGetDefaultEditorPath(out var defaultEditorPath))
        {
            return defaultEditorPath;
        }

        var fromEnv = Environment.GetEnvironmentVariable("UNITY_PATH");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return fromEnv;
        }

        return null;
    }

    private static async Task<UnityBridgeAttachWaitResult> WaitForUnityBridgeAttachWithDiagnosticsAsync(
        string projectPath,
        int port,
        CliSessionState session,
        Action<string> log,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        var nextDiagnosticAt = DateTime.UtcNow;
        var nextProgressAt = DateTime.UtcNow.AddSeconds(10);
        var lastSignature = string.Empty;
        var latestDiagnostics = new UnityBridgeBootDiagnostics(
            "package-import=unknown, script-compile=unknown, domain-reload=unknown",
            "initial");

        while (DateTime.UtcNow < deadline)
        {
            if (!IsUnityClientActiveForProject(projectPath))
            {
                return new UnityBridgeAttachWaitResult(
                    Attached: false,
                    DiagnosticSummary: "unity editor closed during bridge wait",
                    EditorClosedDuringWait: true);
            }

            if (await TrySendControlAsync(port, "PING", "PONG"))
            {
                session.AttachedPort = port;
                await TrySendControlAsync(port, "TOUCH", "OK");
                var readyAfterAttach = await WaitForProjectCommandReadyAsync(
                    port,
                    ProjectCommandReadyTimeout,
                    elapsed => log($"[grey]daemon[/]: waiting for editor bridge endpoint... {elapsed.TotalSeconds:0}s elapsed"));
                if (readyAfterAttach)
                {
                    return new UnityBridgeAttachWaitResult(true, latestDiagnostics.Summary, EditorClosedDuringWait: false);
                }
            }

            var now = DateTime.UtcNow;
            if (now >= nextDiagnosticAt)
            {
                latestDiagnostics = CollectUnityBridgeBootDiagnostics(projectPath);
                var signature = latestDiagnostics.Signature;
                if (!signature.Equals(lastSignature, StringComparison.Ordinal))
                {
                    log($"[grey]daemon[/]: bridge-wait diagnostics -> {Markup.Escape(latestDiagnostics.Summary)}");
                    lastSignature = signature;
                }

                nextDiagnosticAt = now.AddSeconds(3);
            }

            if (now >= nextProgressAt)
            {
                var remaining = deadline - now;
                if (remaining < TimeSpan.Zero)
                {
                    remaining = TimeSpan.Zero;
                }

                log($"[grey]daemon[/]: still waiting for Unity bridge startup ({remaining.TotalSeconds:0}s timeout remaining)");
                nextProgressAt = now.AddSeconds(10);
            }

            await Task.Delay(500);
        }

        return new UnityBridgeAttachWaitResult(false, latestDiagnostics.Summary, EditorClosedDuringWait: false);
    }

    private static UnityBridgeBootDiagnostics CollectUnityBridgeBootDiagnostics(string projectPath)
    {
        var packageState = "unknown";
        var compileState = "unknown";
        var domainReloadState = "unknown";

        if (TryReadUnityEditorLogTail(256 * 1024, out var editorLogTail))
        {
            if (ContainsAny(editorLogTail, "Package Manager", "Resolving packages", "Registering", "UPM", "package resolution"))
            {
                packageState = "in-progress";
            }
            else
            {
                packageState = "idle";
            }

            if (ContainsAny(editorLogTail, "Script compilation", "Compiling", "Assembly-CSharp", "compiler errors", "Compilation failed"))
            {
                compileState = "in-progress-or-failed";
            }
            else
            {
                compileState = "idle";
            }

            if (ContainsAny(editorLogTail, "Domain Reload", "ReloadAssembly", "Reloading assemblies"))
            {
                domainReloadState = "in-progress";
            }
            else
            {
                domainReloadState = "idle";
            }
        }

        var packageJsonPath = Path.Combine(projectPath, "Packages", "packages-lock.json");
        if (File.Exists(packageJsonPath))
        {
            var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(packageJsonPath);
            if (age < TimeSpan.FromSeconds(30))
            {
                packageState = "recently-updated";
            }
        }

        var scriptAssembliesDir = Path.Combine(projectPath, "Library", "ScriptAssemblies");
        if (Directory.Exists(scriptAssembliesDir))
        {
            var newestAssemblyWrite = Directory.EnumerateFiles(scriptAssembliesDir, "*.dll", SearchOption.TopDirectoryOnly)
                .Select(File.GetLastWriteTimeUtc)
                .DefaultIfEmpty(DateTime.MinValue)
                .Max();
            if (newestAssemblyWrite != DateTime.MinValue && DateTime.UtcNow - newestAssemblyWrite < TimeSpan.FromSeconds(30))
            {
                compileState = "recently-updated";
            }
        }

        var summary =
            $"package-import={packageState}, script-compile={compileState}, domain-reload={domainReloadState}";
        return new UnityBridgeBootDiagnostics(
            summary,
            $"{packageState}|{compileState}|{domainReloadState}");
    }

    private static bool TryReadUnityEditorLogTail(int maxBytes, out string text)
    {
        text = string.Empty;
        var path = ResolveUnityEditorLogPath();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length == 0)
            {
                return false;
            }

            var length = (int)Math.Min(maxBytes, stream.Length);
            stream.Seek(-length, SeekOrigin.End);
            var buffer = new byte[length];
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read <= 0)
            {
                return false;
            }

            text = Encoding.UTF8.GetString(buffer, 0, read);
            return !string.IsNullOrWhiteSpace(text);
        }
        catch
        {
            return false;
        }
    }

    private static string? ResolveUnityEditorLogPath()
    {
        if (OperatingSystem.IsMacOS())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(home))
            {
                return Path.Combine(home, "Library", "Logs", "Unity", "Editor.log");
            }
        }

        if (OperatingSystem.IsWindows())
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                return Path.Combine(localAppData, "Unity", "Editor", "Editor.log");
            }
        }

        var unixHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(unixHome))
        {
            return Path.Combine(unixHome, ".config", "unity3d", "Editor.log");
        }

        return null;
    }

    private static bool ContainsAny(string text, params string[] markers)
    {
        foreach (var marker in markers)
        {
            if (text.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private readonly record struct UnityBridgeAttachWaitResult(bool Attached, string DiagnosticSummary, bool EditorClosedDuringWait);
    private readonly record struct UnityBridgeBootDiagnostics(string Summary, string Signature);

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

    internal sealed record DaemonStartupFailure(
        bool IsCompileError,
        string Summary,
        IReadOnlyList<string> Lines);
}
