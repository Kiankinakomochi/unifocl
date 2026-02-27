using Spectre.Console;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;

internal sealed class DaemonControlService
{
    private const int DefaultInactivityTimeoutSeconds = 600;

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
                await HandleDaemonStartAsync(input, runtime, log);
                break;
            case "/daemon stop":
                await HandleDaemonStopAsync(runtime, session, log);
                break;
            case "/daemon restart":
                await HandleDaemonRestartAsync(runtime, session, log);
                break;
            case "/daemon ps":
                HandleDaemonPs(runtime, session, streamLog, log);
                break;
            case "/daemon attach":
                await HandleDaemonAttachAsync(input, runtime, session, log);
                break;
            case "/daemon detach":
                HandleDaemonDetach(session, log);
                break;
            default:
                log("[red]daemon[/]: command handler not implemented");
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

    public static async Task RunDaemonServiceAsync(DaemonServiceOptions options)
    {
        var runtimePath = Path.Combine(Environment.CurrentDirectory, ".unifocl-runtime");
        var runtime = new DaemonRuntime(runtimePath);
        var pid = Environment.ProcessId;
        var startedAtUtc = DateTime.UtcNow;
        var state = new DaemonInstance(options.Port, pid, startedAtUtc, options.UnityPath, options.Headless, options.ProjectPath, DateTime.UtcNow);
        var hierarchyBridge = new HierarchyDaemonBridge(options.ProjectPath);

        runtime.Upsert(state);
        using var cts = new CancellationTokenSource();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        var lastActivityUtc = DateTime.UtcNow;

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

        TcpListener? listener = null;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, options.Port);
            listener.Start();

            while (!cts.Token.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(cts.Token);
                _ = Task.Run(async () =>
                {
                    await using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
                    using var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };
                    var line = await reader.ReadLineAsync();
                    var command = line?.Trim().ToUpperInvariant();

                    switch (command)
                    {
                        case "PING":
                            await writer.WriteLineAsync("PONG");
                            break;
                        case "TOUCH":
                            lastActivityUtc = DateTime.UtcNow;
                            await writer.WriteLineAsync("OK");
                            break;
                        case "STOP":
                            await writer.WriteLineAsync("STOPPING");
                            cts.Cancel();
                            break;
                        default:
                            if (hierarchyBridge.TryHandle(line, out var hierarchyResponse))
                            {
                                lastActivityUtc = DateTime.UtcNow;
                                await writer.WriteLineAsync(hierarchyResponse);
                            }
                            else
                            {
                                lastActivityUtc = DateTime.UtcNow;
                                await writer.WriteLineAsync("ERR");
                            }
                            break;
                    }

                    client.Dispose();
                }, cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (SocketException)
        {
        }
        finally
        {
            cts.Cancel();
            try
            {
                await heartbeatTask;
            }
            catch (OperationCanceledException)
            {
            }

            listener?.Stop();
            runtime.Remove(options.Port);
        }
    }

    public async Task<bool> EnsureProjectDaemonAsync(
        string projectPath,
        DaemonRuntime runtime,
        CliSessionState session,
        Action<string> log)
    {
        var port = ComputeProjectDaemonPort(projectPath);
        var existing = runtime.GetByPort(port);

        if (existing is not null && await TrySendControlAsync(port, "PING", "PONG"))
        {
            session.AttachedPort = port;
            await TrySendControlAsync(port, "TOUCH", "OK");
            return true;
        }

        if (existing is not null)
        {
            runtime.Remove(port);
        }

        var startOptions = new DaemonStartOptions(
            port,
            ResolveDefaultUnityPath(),
            projectPath,
            Headless: true);

        var started = await StartDaemonAsync(startOptions, runtime, log);
        if (!started)
        {
            return false;
        }

        session.AttachedPort = port;
        await TrySendControlAsync(port, "TOUCH", "OK");
        return true;
    }

    public async Task<bool> TouchAttachedDaemonAsync(CliSessionState session)
    {
        if (session.AttachedPort is null)
        {
            return false;
        }

        return await TrySendControlAsync(session.AttachedPort.Value, "TOUCH", "OK");
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
            return true;
        }
    }

    public static int ComputeProjectDaemonPort(string projectPath)
    {
        return 18080 + Math.Abs(projectPath.GetHashCode()) % 2000;
    }

    private async Task HandleDaemonStartAsync(string input, DaemonRuntime runtime, Action<string> log)
    {
        if (!TryParseDaemonStartArgs(input, out var startOptions, out var parseError))
        {
            log($"[red]daemon[/]: {Markup.Escape(parseError)}");
            return;
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

        var ready = await WaitForDaemonReadyAsync(startOptions.Port, TimeSpan.FromSeconds(25));
        if (!ready)
        {
            log($"[red]daemon[/]: process launched (pid {process.Id}) but not responding on port {startOptions.Port}");
            return false;
        }

        log($"[green]daemon[/]: started [white]pid={process.Id}[/] [white]port={startOptions.Port}[/] [white]headless={startOptions.Headless}[/]");
        return true;
    }

    private async Task HandleDaemonStopAsync(DaemonRuntime runtime, CliSessionState session, Action<string> log)
    {
        var target = ResolveTargetDaemon(runtime, session);
        if (target is null)
        {
            log("[yellow]daemon[/]: no target daemon selected. Attach first or keep exactly one daemon running.");
            return;
        }

        var stopOk = await TrySendControlAsync(target.Port, "STOP", "STOPPING");
        if (!stopOk)
        {
            log($"[red]daemon[/]: failed to stop daemon on port {target.Port} via control socket");
            return;
        }

        var deadline = DateTime.UtcNow.AddSeconds(4);
        while (DateTime.UtcNow < deadline && ProcessUtil.IsAlive(target.Pid))
        {
            await Task.Delay(120);
        }

        runtime.Remove(target.Port);
        if (session.AttachedPort == target.Port)
        {
            session.AttachedPort = null;
        }

        log($"[green]daemon[/]: stopped port {target.Port}");
    }

    private async Task HandleDaemonRestartAsync(DaemonRuntime runtime, CliSessionState session, Action<string> log)
    {
        var target = ResolveTargetDaemon(runtime, session);
        var restartPort = target?.Port ?? 8080;
        var restartHeadless = target?.Headless ?? false;
        var restartUnity = target?.UnityPath;
        var restartProject = target?.ProjectPath;

        if (target is not null)
        {
            var stopOk = await TrySendControlAsync(target.Port, "STOP", "STOPPING");
            if (!stopOk)
            {
                log($"[red]daemon[/]: could not stop daemon on port {target.Port}; aborting restart");
                return;
            }

            runtime.Remove(target.Port);
            if (session.AttachedPort == target.Port)
            {
                session.AttachedPort = null;
            }
        }

        var synthesized = $"/daemon start --port {restartPort}" +
                          (restartUnity is null ? string.Empty : $" --unity \"{restartUnity}\"") +
                          (restartProject is null ? string.Empty : $" --project \"{restartProject}\"") +
                          (restartHeadless ? " --headless" : string.Empty);
        await HandleDaemonStartAsync(synthesized, runtime, log);
    }

    private static void HandleDaemonPs(
        DaemonRuntime runtime,
        CliSessionState session,
        List<string> streamLog,
        Action<string> log)
    {
        runtime.CleanStaleEntries();
        var instances = runtime.GetAll().OrderBy(i => i.Port).ToList();
        if (instances.Count == 0)
        {
            log("[grey]daemon[/]: no running daemon instances");
            return;
        }

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Port");
        table.AddColumn("PID");
        table.AddColumn("Uptime");
        table.AddColumn("Unity");
        table.AddColumn("Headless");
        table.AddColumn("Attached");

        foreach (var instance in instances)
        {
            var uptime = FormatUptime(instance.StartedAtUtc);
            table.AddRow(
                instance.Port.ToString(),
                instance.Pid.ToString(),
                uptime,
                instance.UnityPath ?? "-",
                instance.Headless ? "yes" : "no",
                session.AttachedPort == instance.Port ? "yes" : "no");
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

        var instance = runtime.GetByPort(port);
        if (instance is null || !ProcessUtil.IsAlive(instance.Pid))
        {
            log($"[red]daemon[/]: no running daemon registered on port {port}");
            return;
        }

        if (!await TrySendControlAsync(port, "PING", "PONG"))
        {
            log($"[red]daemon[/]: daemon on port {port} is not responding");
            return;
        }

        session.AttachedPort = port;
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
        options = new DaemonStartOptions(8080, null, null, false);
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
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                CreateNoWindow = true
            };

            unityPsi.ArgumentList.Add("-projectPath");
            unityPsi.ArgumentList.Add(options.ProjectPath);
            unityPsi.ArgumentList.Add("-batchmode");
            unityPsi.ArgumentList.Add("-nographics");
            unityPsi.ArgumentList.Add("-noUpm");
            unityPsi.ArgumentList.Add("-vcsMode");
            unityPsi.ArgumentList.Add("None");
            unityPsi.ArgumentList.Add("-executeMethod");
            unityPsi.ArgumentList.Add("CLIDaemon.StartServer");
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

    private static async Task<bool> WaitForDaemonReadyAsync(int port, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            if (await TrySendControlAsync(port, "PING", "PONG"))
            {
                return true;
            }

            await Task.Delay(180);
        }

        return false;
    }

    private static async Task<bool> TrySendControlAsync(int port, string request, string expectedResponse)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await client.ConnectAsync(IPAddress.Loopback, port, cts.Token);
            await using var stream = client.GetStream();
            using var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            await writer.WriteLineAsync(request);
            var response = await reader.ReadLineAsync();
            return string.Equals(response, expectedResponse, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static string? ResolveDefaultUnityPath()
    {
        var fromEnv = Environment.GetEnvironmentVariable("UNITY_PATH");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return fromEnv;
        }

        return null;
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
}
