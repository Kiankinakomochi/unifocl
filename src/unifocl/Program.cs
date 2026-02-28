using Spectre.Console;
using System.Text;

var launchArgs = Environment.GetCommandLineArgs().Skip(1).ToArray();
if (DaemonControlService.TryParseDaemonServiceArgs(launchArgs, out var serviceOptions, out var daemonParseError))
{
    if (daemonParseError is not null)
    {
        AnsiConsole.MarkupLine($"[red]{Markup.Escape(daemonParseError)}[/]");
        Environment.ExitCode = 2;
        return;
    }

    await DaemonControlService.RunDaemonServiceAsync(serviceOptions!);
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
    new("/daemon start [--port 8080] [--unity <path>] [--project <path>] [--headless]", "Start always-warm daemon", "/daemon start"),
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
    new("/init [path-to-project]", "Install editor-side CLI bridge dependencies", "/init"),
    new("/install-hook", "Install/validate Unity editor bridge", "/install-hook"),
    new("/help [topic]", "Show help by topic", "/help"),
    new("/examples", "Show common next-step flows", "/examples"),
    new("/keybinds", "Show modal keybinds/shortcuts", "/keybinds"),
    new("/shortcuts", "Alias for keybinds", "/shortcuts"),

    // Utilities
    new("/update", "Check for CLI updates", "/update"),
    new("/version", "Show CLI and protocol version", "/version"),
    new("/protocol", "Show supported JSON schema capabilities", "/protocol"),
    new("/hierarchy", "Open hierarchy TUI mode", "/hierarchy"),
    new("/exit", "Exit unifocl", "/exit"),
    new("/clear", "Clear and redraw boot screen", "/clear")
};

var streamLog = new List<string>();
var runtimePath = Path.Combine(Environment.CurrentDirectory, ".unifocl-runtime");
var daemonRuntime = new DaemonRuntime(runtimePath);
var session = new CliSessionState();
var daemonControlService = new DaemonControlService();
var projectLifecycleService = new ProjectLifecycleService();
var projectCommandRouterService = new ProjectCommandRouterService();
var hierarchyTui = new HierarchyTui();
SeedBootLog(streamLog);
RenderInitialLog(streamLog);

while (true)
{
    var rawInput = ReadInput(commands, streamLog, session);
    if (rawInput is null)
    {
        await projectLifecycleService.PerformSafeExitCleanupAsync(
            session,
            daemonControlService,
            daemonRuntime,
            line => AppendLog(streamLog, line));
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
        var handledProjectCommand = await projectCommandRouterService.TryHandleProjectCommandAsync(
            input,
            session,
            daemonControlService,
            daemonRuntime,
            line => AppendLog(streamLog, line));
        if (!handledProjectCommand)
        {
            AppendLog(streamLog, "[grey]system[/]: unknown project command; use / for command palette");
        }
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
        await projectLifecycleService.PerformSafeExitCleanupAsync(
            session,
            daemonControlService,
            daemonRuntime,
            line => AppendLog(streamLog, line));
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

    if (matched.Trigger == "/hierarchy")
    {
        await hierarchyTui.RunAsync(
            session,
            daemonControlService,
            daemonRuntime,
            line => AppendLog(streamLog, line));
        continue;
    }

    AppendLog(streamLog, $"[bold deepskyblue1]unifocl[/] [grey]>[/] [white]{Markup.Escape(input)}[/]");
    if (matched.Trigger.StartsWith("/daemon", StringComparison.Ordinal))
    {
        await daemonControlService.HandleDaemonCommandAsync(
            input,
            matched.Trigger,
            daemonRuntime,
            session,
            line => AppendLog(streamLog, line),
            streamLog);
        continue;
    }
    if (await projectLifecycleService.TryHandleLifecycleCommandAsync(
            input,
            matched,
            session,
            daemonControlService,
            daemonRuntime,
            line => AppendLog(streamLog, line)))
    {
        if ((matched.Trigger == "/open" || matched.Trigger == "/new" || matched.Trigger == "/clone")
            && session.Mode == CliMode.Project
            && !string.IsNullOrWhiteSpace(session.CurrentProjectPath))
        {
            await projectCommandRouterService.TryHandleProjectCommandAsync(
                string.Empty,
                session,
                daemonControlService,
                daemonRuntime,
                line => AppendLog(streamLog, line));
        }

        continue;
    }

    AppendLog(streamLog, $"[deepskyblue1]stub[/]: {Markup.Escape(matched.Signature)}");
    WriteMockCommandStream(input, streamLog);
}

static string? ReadInput(List<CommandSpec> commands, List<string> streamLog, CliSessionState session)
{
    if (Console.IsInputRedirected)
    {
        AnsiConsole.Markup($"[bold deepskyblue1]{Markup.Escape(BuildPromptLabel(session))}[/] [grey]>[/] ");
        return Console.ReadLine();
    }

    return ReadInteractiveInput(commands, streamLog, session);
}

static string? ReadInteractiveInput(List<CommandSpec> commands, List<string> streamLog, CliSessionState session)
{
    var input = new StringBuilder();
    var renderedLines = RenderComposerFrame(input.ToString(), commands, session);

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
        renderedLines = RenderComposerFrame(input.ToString(), commands, session);
    }
}

static int RenderComposerFrame(string input, List<CommandSpec> commands, CliSessionState session)
{
    var lines = new List<string>
    {
        "[grey]Input[/]",
        $"[bold deepskyblue1]{Markup.Escape(BuildPromptLabel(session))}[/] [grey]>[/] [bold white]{Markup.Escape(input)}[/]"
    };

    if (input.StartsWith('/'))
    {
        lines.Add(string.Empty);
        lines.AddRange(GetSuggestionLines(input, commands));
    }
    else if (session.Inspector is not null)
    {
        lines.Add("[dim]Inspector commands: inspect [idx], ls, set <field> <value>, toggle <field|idx>, scroll [body|stream] <up|down> [n], :i[/]");
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

static string BuildPromptLabel(CliSessionState session)
{
    var context = session.Inspector;
    if (context is null)
    {
        return "unifocl";
    }

    return context.PromptLabel;
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
