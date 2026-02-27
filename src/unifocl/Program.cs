using Spectre.Console;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;

var launchArgs = Environment.GetCommandLineArgs().Skip(1).ToArray();
if (TryParseDaemonServiceArgs(launchArgs, out var serviceOptions, out var daemonParseError))
{
    if (daemonParseError is not null)
    {
        AnsiConsole.MarkupLine($"[red]{Markup.Escape(daemonParseError)}[/]");
        Environment.ExitCode = 2;
        return;
    }

    await RunDaemonServiceAsync(serviceOptions!);
    return;
}

var commands = new List<CommandSpec>
{
    // Project/session lifecycle
    new("/open <path>", "Open project (starts/attaches daemon, loads project)", "/open"),
    new("/new <project-name> [unity-version]", "Bootstrap a new Unity project", "/new"),
    new("/clone <git-url>", "Clone repo and set local CLI bridge config", "/clone"),
    new("/recent [n]", "List recently opened projects", "/recent"),
    new("/close", "Detach from current project (return to boot)", "/close"),
    new("/switch <recent-index|path>", "Convenience wrapper for recent + open", "/switch"),
    new("/status", "Show daemon/editor/project/session status", "/status"),
    new("/doctor", "Run diagnostics for environment and tooling", "/doctor"),
    new("/logs [daemon|unity] [-f]", "Tail or follow daemon/unity logs", "/logs"),

    // Daemon control
    new("/daemon start [--port 8080] [--unity <path>] [--headless]", "Start always-warm daemon", "/daemon start"),
    new("/daemon stop", "Stop daemon", "/daemon stop"),
    new("/daemon restart", "Restart daemon", "/daemon restart"),
    new("/daemon ps", "Show instances, ports, uptime, project", "/daemon ps"),
    new("/daemon attach <port>", "Attach CLI to existing daemon", "/daemon attach"),
    new("/daemon detach", "Detach CLI and keep daemon alive", "/daemon detach"),

    // Discovery
    new("/scan [--root <dir>] [--depth n]", "Find Unity projects under a directory", "/scan"),
    new("/info <path>", "Read project metadata (Unity version/name/paths)", "/info"),
    new("/unity detect", "List installed Unity editors", "/unity detect"),
    new("/unity set <path>", "Set default Unity editor path", "/unity set"),

    // Configuration
    new("/config get <key>", "Read configuration value", "/config get"),
    new("/config set <key> <value>", "Write configuration value", "/config set"),
    new("/config list", "List current configuration", "/config list"),
    new("/config reset", "Reset configuration to defaults", "/config reset"),

    // Onboarding
    new("/init", "Run first-run setup wizard", "/init"),
    new("/install-hook", "Install/validate Unity editor bridge", "/install-hook"),
    new("/help [topic]", "Show help by topic", "/help"),
    new("/examples", "Show common next-step flows", "/examples"),
    new("/keybinds", "Show modal keybinds/shortcuts", "/keybinds"),
    new("/shortcuts", "Alias for keybinds", "/shortcuts"),

    // Utilities
    new("/update", "Check for CLI updates", "/update"),
    new("/version", "Show CLI and protocol version", "/version"),
    new("/protocol", "Show supported JSON schema capabilities", "/protocol"),
    new("/exit", "Exit unifocl", "/exit"),
    new("/clear", "Clear and redraw boot screen", "/clear")
};

var streamLog = new List<string>();
var runtimePath = Path.Combine(Environment.CurrentDirectory, ".unifocl-runtime");
var daemonRuntime = new DaemonRuntime(runtimePath);
var session = new CliSessionState();
SeedBootLog(streamLog);
RenderInitialLog(streamLog);

while (true)
{
    var rawInput = ReadInput(commands, streamLog);
    if (rawInput is null)
    {
        AnsiConsole.MarkupLine("[grey]Input stream closed. Session ended.[/]");
        return;
    }

    var input = rawInput.Trim();
    if (string.IsNullOrWhiteSpace(input))
    {
        continue;
    }

    if (!input.StartsWith('/'))
    {
        AppendLog(streamLog, "[grey]system[/]: boot mode is slash-first; type / to see available commands");
        continue;
    }

    if (input == "/")
    {
        continue;
    }

    var matched = MatchCommand(input, commands);
    if (matched is null)
    {
        AppendLog(streamLog, $"[grey]system[/]: unknown command [white]{Markup.Escape(input)}[/]");
        continue;
    }

    if (matched.Trigger == "/exit")
    {
        AnsiConsole.MarkupLine("[grey]Session closed.[/]");
        return;
    }

    if (matched.Trigger == "/clear")
    {
        streamLog.Clear();
        SeedBootLog(streamLog);
        AnsiConsole.Clear();
        RenderInitialLog(streamLog);
        continue;
    }

    AppendLog(streamLog, $"[bold deepskyblue1]unifocl[/] [grey]>[/] [white]{Markup.Escape(input)}[/]");
    if (matched.Trigger.StartsWith("/daemon", StringComparison.Ordinal))
    {
        await HandleDaemonCommandAsync(input, matched.Trigger, daemonRuntime, session, streamLog);
        continue;
    }

    AppendLog(streamLog, $"[deepskyblue1]stub[/]: {Markup.Escape(matched.Signature)}");
    WriteMockCommandStream(input, streamLog);
}

static string? ReadInput(List<CommandSpec> commands, List<string> streamLog)
{
    if (Console.IsInputRedirected)
    {
        AnsiConsole.Markup("[bold deepskyblue1]unifocl[/] [grey]>[/] ");
        return Console.ReadLine();
    }

    return ReadInteractiveInput(commands, streamLog);
}

static string? ReadInteractiveInput(List<CommandSpec> commands, List<string> streamLog)
{
    var input = new StringBuilder();
    var renderedLines = RenderComposerFrame(input.ToString(), commands);

    while (true)
    {
        var key = Console.ReadKey(intercept: true);

        switch (key.Key)
        {
            case ConsoleKey.Enter:
                Console.WriteLine();
                return input.ToString();
            case ConsoleKey.Backspace:
                if (input.Length > 0)
                {
                    input.Remove(input.Length - 1, 1);
                }
                break;
            case ConsoleKey.Escape:
                input.Clear();
                break;
            default:
                if (!char.IsControl(key.KeyChar))
                {
                    input.Append(key.KeyChar);
                }
                break;
        }

        ClearComposerFrame(renderedLines);
        renderedLines = RenderComposerFrame(input.ToString(), commands);
    }
}

static int RenderComposerFrame(string input, List<CommandSpec> commands)
{
    var lines = new List<string>
    {
        "[grey]Input[/]",
        $"[bold deepskyblue1]unifocl[/] [grey]>[/] [bold white]{Markup.Escape(input)}[/]"
    };

    if (input.StartsWith('/'))
    {
        lines.Add(string.Empty);
        lines.AddRange(GetSuggestionLines(input, commands));
    }
    else
    {
        lines.Add("[dim]Type / to open command palette. Use your mouse wheel to scroll log history.[/]");
    }

    foreach (var line in lines)
    {
        AnsiConsole.MarkupLine(line);
    }

    return lines.Count;
}

static void ClearComposerFrame(int renderedLines)
{
    for (var i = 0; i < renderedLines; i++)
    {
        Console.Write("\u001b[1A");
        Console.Write("\r\u001b[2K");
    }
}

static void RenderInitialLog(List<string> streamLog)
{
    foreach (var line in streamLog)
    {
        AnsiConsole.MarkupLine(line);
    }
}

static void SeedBootLog(List<string> streamLog)
{
    streamLog.Add("[bold deepskyblue1]unifocl[/]");
    streamLog.Add("[bold green]Welcome to unifocl[/]");
    streamLog.Add(string.Empty);

    var logo = """
                       .;?tXOb*&%@$$$$@%&*bOXt];.                       
                   .!/LaB$$$$%#hpm0QQ0Zph#B$$$$BaL/i.                   
                 +vk@$$@oQu\)1(/jnvccvnrf)I]\n0o@$$@kv+.                 
              "td@$$W0t})nLkWB$$$$$$$$$@Q\vhkQx)}tQW$$@dt,              
            ,u#$$BZ\]fZM$$$$$$$$$$$$$@L)XW$$$$$$WZf]|ZB$$#u,            
          't#$$8U?108$$$$$$$$$$$$$$$d(uW$$$$$$$$$$$801-Y8$$Mf.          
         iq$$$C++-$$$$$$$$$$$$$$$$8n|k$$$$$$$$$$$$$$$$b(~C$$$q~         
        (&$$*[+q%ib$$$$$$$$$$$$$$k{U@$$$$$$$$$$$$$$$$$$$q_[*$$&(        
       j@$$m:j%$$X{$$$$$$$$@@$$$C{k$$$$$$$$$$$@@$$$$$$$$$%j:Z$$@j       
      t$$$L`J$$$$B?Q$$$$$$$$$$$u(&$$$$$$$$$$$$$$$$$$$$$$$$$J^L$$$t      
     -%$$w'C$$$$$$h~*$$$$$$$$@ff@$$$BhqZ0QLCCCCCCL0Zwdh*&B$$Q`m$$B-     
    'k$$M,j$$$$$$$$Q?8$$$$$$$fr$$&Zt-/LpkkkhhhhhkdpwZ0CXvuucL-,M$$k'    
    )$$$f:8$$$$$$$$$z)%$$$$@vt*L):   .I(L#$$$$$$$$$$$$$$$@8#b0,f$$$(    
    m$$W`v$$$$$$$$$$$v(%$$$#:-^          `-vpB$$$$$$$$$$$$$$$$v`W$$w    
   ^M$$m k$$$$$$$$$$$$X)&$$#`              .n|*$$$$$$$$$$$$$$$k m$$M^   
   I%$$X`W$$$$$$$$$$$$$L{o$#`              `Wq[p$$$$$$$$$$$$$$&`Y$$%I   
   I%$$X`W$$$$$$$$$$$$$$p}wW`              `*$o}Q$$$$$$$$$$$$$&`X$$%l   
   ^M$$m k$$$$$$$$$$$$$$$#/n'              '*$$&1Y$$$$$$$$$$$$k m$$M^   
    m$$W`c$$$$$$$$$$$$$$$$@bc?^          ^-:#$$$8)X$$$$$$$$$$$c`W$$m    
    )$$$f"zLpoW%@$$$$$$$$$$$$$W0\l.   ;)L*/c@$$$$%{Y$$$$$$$$$%;t$$$)    
    'k$$M,1pQYvuvvzYJLQ0OZZZOOOZ0X)_jw&$$fr$$$$$$$&?0$$$$$$$$x"#$$k'    
     -%$$m`0$$$$B8M*ohkbddppdbbha*M@$$$@tj@$$$$$$$$o~h$$$$$$0'Z$$%-     
      /$$$L^L$$$$$$$$$$$$$$$$$$$$$$$$$%\n$$$$$$$$$$$O-%$$$$Q^U$$$t      
       j@$$O:xB$$$$$$$$$$$$$$$$$$$$$$#1Y$$$$$$$$$$$$$(v$$Bn:0$$@r       
        (&$$o]-p$$$$$$$$$$$$$$$$$$$$w[m$$$$$$$$$$$$$$oi&b??a$$&|        
         ~q$$@J~|k$$$$$$$$$$$$$$$$Bv(*$$$$$$$$$$$$$$$$}~+U@$$pi         
          .f#$$8X?)Z8$$$$$$$$$$$$d)zB$$$$$$$$$$$$$$%m(?z&$$#f'          
            ,n#$$BZ|]jZW$$$$$$$*r\h$$$$$$$$$$$$$Wmj](0%$$#u,            
              ,/d@$$MQt{(nQkW*c/q$$$$$$$$$BWh0n({tQM$$$dt,              
                 +vk@$$@o0u\)+I/nuzXXzvxt|))\nQa@$$@kv+.                 
                   .!/LhB$$$$BMhqZQLLQZqk*8$$$$BaL/!.                   
                       .;]fXOk#&B$$$$$$B&#kZYf]I.                       
                              '^:I!ii!l;"'                  
""";

    foreach (var line in logo.Split('\n'))
    {
        streamLog.Add($"[grey]{Markup.Escape(line)}[/]");
    }

    streamLog.Add(string.Empty);
    streamLog.Add("[grey]No project attached.[/]");
    streamLog.Add(string.Empty);
}

static IEnumerable<string> GetSuggestionLines(string query, List<CommandSpec> commands)
{
    var normalized = query.Trim().ToLowerInvariant();
    var matches = commands
        .Where(c => c.Signature.Contains(normalized, StringComparison.OrdinalIgnoreCase)
                    || c.Description.Contains(normalized, StringComparison.OrdinalIgnoreCase)
                    || c.Trigger.StartsWith(normalized, StringComparison.OrdinalIgnoreCase)
                    || normalized.StartsWith(c.Trigger, StringComparison.OrdinalIgnoreCase))
        .Take(14)
        .ToList();

    if (matches.Count == 0)
    {
        return new[] { $"[dim]no matches for {Markup.Escape(query)}[/]" };
    }

    return matches.Select(match =>
        $"[grey]{Markup.Escape(match.Signature)}[/] [dim]- {Markup.Escape(match.Description)}[/]");
}

static CommandSpec? MatchCommand(string input, List<CommandSpec> commands)
{
    var normalized = input.Trim().ToLowerInvariant();

    return commands
        .OrderByDescending(c => c.Trigger.Length)
        .FirstOrDefault(c => normalized == c.Trigger
                             || normalized.StartsWith(c.Trigger + " ", StringComparison.OrdinalIgnoreCase));
}

static void WriteMockCommandStream(string input, List<string> streamLog)
{
    var lines = new[]
    {
        "[grey]stream[/]: validating command payload",
        "[grey]stream[/]: checking daemon reachability",
        "[grey]stream[/]: loading mock capability graph",
        "[grey]stream[/]: command stub executed",
        "[green]stream[/]: done"
    };

    foreach (var line in lines)
    {
        AppendLog(streamLog, $"{line} [dim]({Markup.Escape(input)})[/]");
        Thread.Sleep(110);
    }
}

static void AppendLog(List<string> streamLog, string line)
{
    streamLog.Add(line);
    AnsiConsole.MarkupLine(line);
}

static async Task HandleDaemonCommandAsync(
    string input,
    string trigger,
    DaemonRuntime runtime,
    CliSessionState session,
    List<string> streamLog)
{
    switch (trigger)
    {
        case "/daemon start":
            await HandleDaemonStartAsync(input, runtime, streamLog);
            break;
        case "/daemon stop":
            await HandleDaemonStopAsync(runtime, session, streamLog);
            break;
        case "/daemon restart":
            await HandleDaemonRestartAsync(input, runtime, session, streamLog);
            break;
        case "/daemon ps":
            HandleDaemonPs(runtime, session, streamLog);
            break;
        case "/daemon attach":
            await HandleDaemonAttachAsync(input, runtime, session, streamLog);
            break;
        case "/daemon detach":
            HandleDaemonDetach(session, streamLog);
            break;
        default:
            AppendLog(streamLog, "[red]daemon[/]: command handler not implemented");
            break;
    }
}

static async Task HandleDaemonStartAsync(string input, DaemonRuntime runtime, List<string> streamLog)
{
    if (!TryParseDaemonStartArgs(input, out var startOptions, out var parseError))
    {
        AppendLog(streamLog, $"[red]daemon[/]: {Markup.Escape(parseError)}");
        return;
    }

    var existing = runtime.GetByPort(startOptions.Port);
    if (existing is not null && ProcessUtil.IsAlive(existing.Pid) && await TrySendControlAsync(existing.Port, "PING", "PONG"))
    {
        AppendLog(streamLog, $"[yellow]daemon[/]: port {startOptions.Port} already has a running daemon (pid {existing.Pid})");
        return;
    }

    if (existing is not null)
    {
        runtime.Remove(startOptions.Port);
    }

    var launch = ResolveDaemonLaunch(startOptions);
    var process = Process.Start(launch);
    if (process is null)
    {
        AppendLog(streamLog, "[red]daemon[/]: failed to start daemon process");
        return;
    }

    var ready = await WaitForDaemonReadyAsync(startOptions.Port, TimeSpan.FromSeconds(6));
    if (!ready)
    {
        AppendLog(streamLog, $"[red]daemon[/]: process launched (pid {process.Id}) but not responding on port {startOptions.Port}");
        return;
    }

    AppendLog(streamLog,
        $"[green]daemon[/]: started [white]pid={process.Id}[/] [white]port={startOptions.Port}[/] [white]headless={startOptions.Headless}[/]");
}

static async Task HandleDaemonStopAsync(DaemonRuntime runtime, CliSessionState session, List<string> streamLog)
{
    var target = ResolveTargetDaemon(runtime, session);
    if (target is null)
    {
        AppendLog(streamLog,
            "[yellow]daemon[/]: no target daemon selected. Attach first or keep exactly one daemon running.");
        return;
    }

    var stopOk = await TrySendControlAsync(target.Port, "STOP", "STOPPING");
    if (!stopOk)
    {
        AppendLog(streamLog, $"[red]daemon[/]: failed to stop daemon on port {target.Port} via control socket");
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

    AppendLog(streamLog, $"[green]daemon[/]: stopped port {target.Port}");
}

static async Task HandleDaemonRestartAsync(
    string input,
    DaemonRuntime runtime,
    CliSessionState session,
    List<string> streamLog)
{
    var target = ResolveTargetDaemon(runtime, session);
    var restartPort = target?.Port ?? 8080;
    var restartHeadless = target?.Headless ?? false;
    var restartUnity = target?.UnityPath;

    if (target is not null)
    {
        var stopOk = await TrySendControlAsync(target.Port, "STOP", "STOPPING");
        if (!stopOk)
        {
            AppendLog(streamLog, $"[red]daemon[/]: could not stop daemon on port {target.Port}; aborting restart");
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
                      (restartHeadless ? " --headless" : string.Empty);
    await HandleDaemonStartAsync(synthesized, runtime, streamLog);
}

static void HandleDaemonPs(DaemonRuntime runtime, CliSessionState session, List<string> streamLog)
{
    runtime.CleanStaleEntries();
    var instances = runtime.GetAll().OrderBy(i => i.Port).ToList();
    if (instances.Count == 0)
    {
        AppendLog(streamLog, "[grey]daemon[/]: no running daemon instances");
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

static async Task HandleDaemonAttachAsync(
    string input,
    DaemonRuntime runtime,
    CliSessionState session,
    List<string> streamLog)
{
    var args = TokenizeArgs(input);
    if (args.Count < 3 || !int.TryParse(args[2], out var port))
    {
        AppendLog(streamLog, "[red]daemon[/]: usage /daemon attach <port>");
        return;
    }

    var instance = runtime.GetByPort(port);
    if (instance is null || !ProcessUtil.IsAlive(instance.Pid))
    {
        AppendLog(streamLog, $"[red]daemon[/]: no running daemon registered on port {port}");
        return;
    }

    if (!await TrySendControlAsync(port, "PING", "PONG"))
    {
        AppendLog(streamLog, $"[red]daemon[/]: daemon on port {port} is not responding");
        return;
    }

    session.AttachedPort = port;
    AppendLog(streamLog, $"[green]daemon[/]: attached to port {port}");
}

static void HandleDaemonDetach(CliSessionState session, List<string> streamLog)
{
    if (session.AttachedPort is null)
    {
        AppendLog(streamLog, "[yellow]daemon[/]: no daemon attached");
        return;
    }

    var detachedPort = session.AttachedPort.Value;
    session.AttachedPort = null;
    AppendLog(streamLog, $"[green]daemon[/]: detached from port {detachedPort}; daemon kept running");
}

static DaemonInstance? ResolveTargetDaemon(DaemonRuntime runtime, CliSessionState session)
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

static bool TryParseDaemonStartArgs(string input, out DaemonStartOptions options, out string error)
{
    var tokens = TokenizeArgs(input);
    options = new DaemonStartOptions(8080, null, false);
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

static ProcessStartInfo ResolveDaemonLaunch(DaemonStartOptions options)
{
    var processPath = Environment.ProcessPath;
    if (string.IsNullOrWhiteSpace(processPath))
    {
        throw new InvalidOperationException("Unable to resolve current process path for daemon launch.");
    }

    var daemonArgs = new List<string>
    {
        "--daemon-service",
        "--port", options.Port.ToString()
    };

    if (options.UnityPath is not null)
    {
        daemonArgs.Add("--unity");
        daemonArgs.Add(options.UnityPath);
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
    else
    {
        var psi = new ProcessStartInfo(processPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            CreateNoWindow = true
        };
        foreach (var arg in daemonArgs)
        {
            psi.ArgumentList.Add(arg);
        }

        return psi;
    }
}

static async Task<bool> WaitForDaemonReadyAsync(int port, TimeSpan timeout)
{
    var deadline = DateTime.UtcNow.Add(timeout);
    while (DateTime.UtcNow < deadline)
    {
        if (await TrySendControlAsync(port, "PING", "PONG"))
        {
            return true;
        }

        await Task.Delay(120);
    }

    return false;
}

static async Task<bool> TrySendControlAsync(int port, string request, string expectedResponse)
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

static string FormatUptime(DateTime startedAtUtc)
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

static bool TryParseDaemonServiceArgs(string[] args, out DaemonServiceOptions? options, out string? error)
{
    options = null;
    error = null;

    if (!args.Contains("--daemon-service", StringComparer.Ordinal))
    {
        return false;
    }

    var port = 8080;
    string? unityPath = null;
    var headless = false;

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
            case "--headless":
                headless = true;
                break;
        }
    }

    options = new DaemonServiceOptions(port, unityPath, headless);
    return true;
}

static async Task RunDaemonServiceAsync(DaemonServiceOptions options)
{
    var runtimePath = Path.Combine(Environment.CurrentDirectory, ".unifocl-runtime");
    var runtime = new DaemonRuntime(runtimePath);
    var pid = Environment.ProcessId;
    var startedAtUtc = DateTime.UtcNow;
    var state = new DaemonInstance(options.Port, pid, startedAtUtc, options.UnityPath, options.Headless, null, DateTime.UtcNow);

    runtime.Upsert(state);
    using var cts = new CancellationTokenSource();
    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
    var heartbeatTask = Task.Run(async () =>
    {
        while (await timer.WaitForNextTickAsync(cts.Token))
        {
            runtime.Upsert(state with { LastHeartbeatUtc = DateTime.UtcNow });
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
                    case "STOP":
                        await writer.WriteLineAsync("STOPPING");
                        cts.Cancel();
                        break;
                    default:
                        await writer.WriteLineAsync("ERR");
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

static List<string> TokenizeArgs(string input)
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

internal sealed record CommandSpec(string Signature, string Description, string Trigger);
internal sealed record DaemonStartOptions(int Port, string? UnityPath, bool Headless);
internal sealed record DaemonServiceOptions(int Port, string? UnityPath, bool Headless);
internal sealed record DaemonInstance(
    int Port,
    int Pid,
    DateTime StartedAtUtc,
    string? UnityPath,
    bool Headless,
    string? ProjectPath,
    DateTime LastHeartbeatUtc);

internal sealed class CliSessionState
{
    public int? AttachedPort { get; set; }
}

internal sealed class DaemonRuntime
{
    private readonly string _rootPath;
    private readonly string _registryPath;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    public DaemonRuntime(string rootPath)
    {
        _rootPath = rootPath;
        _registryPath = Path.Combine(_rootPath, "daemons");
        Directory.CreateDirectory(_registryPath);
    }

    public IEnumerable<DaemonInstance> GetAll()
    {
        foreach (var file in Directory.EnumerateFiles(_registryPath, "*.json"))
        {
            DaemonInstance? instance = null;
            try
            {
                var json = File.ReadAllText(file);
                instance = JsonSerializer.Deserialize<DaemonInstance>(json, _jsonOptions);
            }
            catch
            {
                // Ignore malformed files in runtime directory.
            }

            if (instance is not null && ProcessUtil.IsAlive(instance.Pid))
            {
                yield return instance;
            }
        }
    }

    public DaemonInstance? GetByPort(int port)
    {
        var path = GetPath(port);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(path);
            var state = JsonSerializer.Deserialize<DaemonInstance>(json, _jsonOptions);
            return state is not null && ProcessUtil.IsAlive(state.Pid) ? state : null;
        }
        catch
        {
            return null;
        }
    }

    public void Upsert(DaemonInstance instance)
    {
        var path = GetPath(instance.Port);
        var json = JsonSerializer.Serialize(instance, _jsonOptions);
        File.WriteAllText(path, json);
    }

    public void Remove(int port)
    {
        var path = GetPath(port);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public void CleanStaleEntries()
    {
        foreach (var file in Directory.EnumerateFiles(_registryPath, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var instance = JsonSerializer.Deserialize<DaemonInstance>(json, _jsonOptions);
                if (instance is null || !ProcessUtil.IsAlive(instance.Pid))
                {
                    File.Delete(file);
                }
            }
            catch
            {
                File.Delete(file);
            }
        }
    }

    private string GetPath(int port) => Path.Combine(_registryPath, $"{port}.json");
}

internal static class ProcessUtil
{
    public static bool IsAlive(int pid)
    {
        try
        {
            var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }
}
