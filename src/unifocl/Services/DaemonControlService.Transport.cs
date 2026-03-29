using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

internal sealed partial class DaemonControlService
{
    public static async Task RunDaemonServiceAsync(DaemonServiceOptions options, CancellationToken cancellationToken = default)
    {
        var runtimePath = Path.Combine(Environment.CurrentDirectory, ".unifocl-runtime");
        var runtime = new DaemonRuntime(runtimePath);
        var pid = Environment.ProcessId;
        var startedAtUtc = DateTime.UtcNow;
        var state = new DaemonInstance(options.Port, pid, startedAtUtc, options.UnityPath, options.Headless, options.ProjectPath, DateTime.UtcNow);
        var hierarchyBridge = new HierarchyDaemonBridge(options.ProjectPath);
        using var assetIndexBridge = new AssetIndexDaemonBridge(options.ProjectPath);
        var inspectorBridge = new InspectorDaemonBridge(options.ProjectPath);
        var projectBridge = new ProjectDaemonBridge(options.ProjectPath);
        var execRegistry = new ExecCommandRegistry();
        var approvalStorePath = ResolveApprovalStorePath(options.Port);
        var execApproval = new ExecApprovalService(approvalStorePath);
        var execRouter = new ExecOperationRouter(execRegistry, execApproval, _sessionService);

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

        // Build transport servers: UDS always (Windows 10 1803+ supported), HTTP only when --unsafe-http.
        var udsSocketPath = ResolveUdsSocketPath(options.Port);
        UdsExecTransportServer? udsServer = !string.IsNullOrWhiteSpace(udsSocketPath)
            ? new UdsExecTransportServer(udsSocketPath)
            : null;

        // When HTTP is enabled, generate a per-session secret and write it to disk.
        // Clients must send X-Unifocl-Token: <secret> with every request.
        string? httpToken = null;
        if (options.UnsafeHttp)
        {
            httpToken = Guid.NewGuid().ToString("N");
            var tokenPath = ResolveHttpTokenPath(options.Port);
            if (tokenPath is not null)
            {
                try
                {
                    var dir = Path.GetDirectoryName(tokenPath);
                    if (!string.IsNullOrWhiteSpace(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    File.WriteAllText(tokenPath, httpToken);
                    if (!System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
                    {
                        File.SetUnixFileMode(tokenPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                    }
                }
                catch
                {
                    // Couldn't persist the token — still apply it in-memory for the session.
                }
            }
        }

        HttpExecTransportServer? httpServer = options.UnsafeHttp
            ? new HttpExecTransportServer(options.Port, httpToken)
            : null;

        Task DispatchCtx(IExecRequestContext ctx) => HandleDaemonRequestAsync(
            ctx, hierarchyBridge, assetIndexBridge, inspectorBridge, projectBridge,
            execRouter, options, cts.Token,
            () => lastActivityUtc = DateTime.UtcNow,
            () => cts.Cancel());

        try
        {
            udsServer?.Start();
            httpServer?.Start();

            var udsLoop = udsServer is not null
                ? RunAcceptLoopAsync(udsServer, requestTasks, DispatchCtx, cts.Token)
                : Task.CompletedTask;
            var httpLoop = httpServer is not null
                ? RunAcceptLoopAsync(httpServer, requestTasks, DispatchCtx, cts.Token)
                : Task.CompletedTask;

            await Task.WhenAll(udsLoop, httpLoop);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            cts.Cancel();
            udsServer?.Dispose();
            httpServer?.Dispose();
            execApproval.Dispose();

            // Delete the HTTP token file so stale tokens cannot be reused after shutdown.
            if (httpToken is not null)
            {
                try
                {
                    var tokenPath = ResolveHttpTokenPath(options.Port);
                    if (tokenPath is not null)
                    {
                        File.Delete(tokenPath);
                    }
                }
                catch
                {
                    // best-effort cleanup
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

    private static async Task RunAcceptLoopAsync(
        IExecTransportServer server,
        ConcurrentBag<Task> requestTasks,
        Func<IExecRequestContext, Task> dispatch,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            IExecRequestContext ctx;
            try
            {
                ctx = await server.AcceptAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                break;
            }

            requestTasks.Add(dispatch(ctx));
        }
    }

    private static string? ResolveUdsSocketPath(int port)
    {
        try
        {
            var runtimeDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".unifocl-runtime");
            return Path.Combine(runtimeDir, $"daemon-{port}.sock");
        }
        catch
        {
            return null;
        }
    }

    private static string? ResolveHttpTokenPath(int port)
    {
        try
        {
            var runtimeDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".unifocl-runtime");
            return Path.Combine(runtimeDir, $"http-token-{port}.txt");
        }
        catch
        {
            return null;
        }
    }

    private static string? ResolveApprovalStorePath(int port)
    {
        try
        {
            var runtimeDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".unifocl-runtime");
            return Path.Combine(runtimeDir, $"approvals-{port}.json");
        }
        catch
        {
            return null;
        }
    }

    private static async Task HandleDaemonRequestAsync(
        IExecRequestContext ctx,
        HierarchyDaemonBridge hierarchyBridge,
        AssetIndexDaemonBridge assetIndexBridge,
        InspectorDaemonBridge inspectorBridge,
        ProjectDaemonBridge projectBridge,
        ExecOperationRouter execRouter,
        DaemonServiceOptions options,
        CancellationToken cancellationToken,
        Action touchActivity,
        Action requestShutdown)
    {
        var path = ctx.Path;

        try
        {
            if (ctx.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) && path.Equals("/ping", StringComparison.OrdinalIgnoreCase))
            {
                await ctx.WriteTextAsync( "PONG", ct: cancellationToken);
                return;
            }

            if (ctx.Method.Equals("POST", StringComparison.OrdinalIgnoreCase) && path.Equals("/touch", StringComparison.OrdinalIgnoreCase))
            {
                if (!ctx.IsInternal)
                {
                    await ctx.WriteTextAsync("ERR", statusCode: 403, ct: cancellationToken);
                    return;
                }

                touchActivity();
                await ctx.WriteTextAsync("OK", ct: cancellationToken);
                return;
            }

            if (ctx.Method.Equals("POST", StringComparison.OrdinalIgnoreCase) && path.Equals("/stop", StringComparison.OrdinalIgnoreCase))
            {
                if (!ctx.IsInternal)
                {
                    await ctx.WriteTextAsync("ERR", statusCode: 403, ct: cancellationToken);
                    return;
                }

                await ctx.WriteTextAsync("STOPPING", ct: cancellationToken);
                requestShutdown();
                return;
            }

            if (ctx.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) && path.Equals("/asset-index", StringComparison.OrdinalIgnoreCase))
            {
                var revisionRaw = ctx.Query["revision"];
                var command = int.TryParse(revisionRaw, out var revision) && revision > 0
                    ? $"ASSET_INDEX_SYNC {revision}"
                    : "ASSET_INDEX_GET";
                if (assetIndexBridge.TryHandle(command, out var assetResponse))
                {
                    touchActivity();
                    await ctx.WriteJsonAsync( assetResponse, ct: cancellationToken);
                    return;
                }
            }

            if (ctx.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) && path.Equals("/hierarchy/snapshot", StringComparison.OrdinalIgnoreCase))
            {
                if (hierarchyBridge.TryHandle("HIERARCHY_GET", out var hierarchySnapshot))
                {
                    touchActivity();
                    await ctx.WriteJsonAsync( hierarchySnapshot, ct: cancellationToken);
                    return;
                }
            }

            if (ctx.Method.Equals("POST", StringComparison.OrdinalIgnoreCase) && path.Equals("/hierarchy/command", StringComparison.OrdinalIgnoreCase))
            {
                var payload = await ctx.ReadBodyAsync(cancellationToken);
                if (hierarchyBridge.TryHandle($"HIERARCHY_CMD {payload}", out var hierarchyResponse))
                {
                    touchActivity();
                    await ctx.WriteJsonAsync( hierarchyResponse, ct: cancellationToken);
                    return;
                }
            }

            if (ctx.Method.Equals("POST", StringComparison.OrdinalIgnoreCase) && path.Equals("/hierarchy/find", StringComparison.OrdinalIgnoreCase))
            {
                var payload = await ctx.ReadBodyAsync(cancellationToken);
                if (hierarchyBridge.TryHandle($"HIERARCHY_FIND {payload}", out var hierarchySearch))
                {
                    touchActivity();
                    await ctx.WriteJsonAsync( hierarchySearch, ct: cancellationToken);
                    return;
                }
            }

            if (ctx.Method.Equals("POST", StringComparison.OrdinalIgnoreCase) && path.Equals("/inspect", StringComparison.OrdinalIgnoreCase))
            {
                var payload = await ctx.ReadBodyAsync(cancellationToken);
                if (inspectorBridge.TryHandle($"INSPECT {payload}", out var inspectorResponse))
                {
                    touchActivity();
                    await ctx.WriteJsonAsync( inspectorResponse, ct: cancellationToken);
                    return;
                }
            }

            if (ctx.Method.Equals("POST", StringComparison.OrdinalIgnoreCase) && path.Equals("/project/command", StringComparison.OrdinalIgnoreCase))
            {
                var payload = await ctx.ReadBodyAsync(cancellationToken);
                if (projectBridge.TryHandle($"PROJECT_CMD {payload}", out var projectResponse))
                {
                    touchActivity();
                    await ctx.WriteJsonAsync( projectResponse, ct: cancellationToken);
                    return;
                }
            }

            if (ctx.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) && path.Equals("/agent/capabilities", StringComparison.OrdinalIgnoreCase))
            {
                touchActivity();
                await ctx.WriteJsonAsync( BuildAgenticCapabilitiesPayload(), ct: cancellationToken);
                return;
            }

            if (ctx.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) && path.Equals("/agent/status", StringComparison.OrdinalIgnoreCase))
            {
                touchActivity();
                var requestId = ctx.Query["requestId"] ?? string.Empty;
                await ctx.WriteJsonAsync( BuildAgenticStatusPayload(requestId), ct: cancellationToken);
                return;
            }

            if (ctx.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) && path.StartsWith("/agent/dump/", StringComparison.OrdinalIgnoreCase))
            {
                var category = path["/agent/dump/".Length..].Trim();
                var format = ctx.Query["format"];
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
                await ctx.WriteJsonAsync( payload, ct: cancellationToken);
                return;
            }

            if (ctx.Method.Equals("POST", StringComparison.OrdinalIgnoreCase) && path.Equals("/agent/exec", StringComparison.OrdinalIgnoreCase))
            {
                var payload = await ctx.ReadBodyAsync(cancellationToken);

                // Detect ExecV2 by presence of "operation" field in the JSON body
                ExecV2Request? v2Request = null;
                try
                {
                    using var doc = JsonDocument.Parse(payload);
                    if (doc.RootElement.TryGetProperty("operation", out _))
                    {
                        v2Request = JsonSerializer.Deserialize<ExecV2Request>(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                    }
                }
                catch
                {
                    // leave v2Request null — will be rejected below
                }

                if (v2Request is not null && !string.IsNullOrWhiteSpace(v2Request.Operation))
                {
                    var v2Response = execRouter.Route(v2Request, projectBridge);
                    touchActivity();
                    await ctx.WriteJsonAsync(
                        JsonSerializer.Serialize(v2Response, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                        ct: cancellationToken);
                    return;
                }

                // Free-form CommandText exec is removed. All callers must use structured ExecV2.
                touchActivity();
                await ctx.WriteJsonAsync(
                    BuildAgenticValidationError(
                        "invalid /agent/exec payload: 'operation' field is required. " +
                        "Use structured ExecV2 (e.g. {\"operation\":\"asset.rename\", ...})."),
                    ct: cancellationToken);
                return;
            }

            touchActivity();
            await ctx.WriteTextAsync( "ERR", statusCode: 404, ct: cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (RequestTooLargeException)
        {
            try
            {
                await ctx.WriteTextAsync("Request body too large (limit: 1 MB)", statusCode: 413, ct: cancellationToken);
            }
            catch
            {
            }
        }
        catch
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await ctx.WriteTextAsync("ERR", statusCode: 500, ct: cancellationToken);
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
}
