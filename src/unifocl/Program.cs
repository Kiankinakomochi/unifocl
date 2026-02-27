using Spectre.Console;
using System.Text;

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
RenderBootHeader();

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
        ShowSuggestions("/", commands);
        continue;
    }

    var matched = MatchCommand(input, commands);
    if (matched is null)
    {
        ShowSuggestions(input, commands);
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
        RenderBootHeader();
        continue;
    }

    AppendLog(streamLog, $"[deepskyblue1]stub[/]: {Markup.Escape(matched.Signature)}");
    WriteMockCommandStream(input, commands, streamLog);
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
    RenderComposerFrame(input.ToString(), commands, streamLog);

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

        RenderComposerFrame(input.ToString(), commands, streamLog);
    }
}

static void RenderComposerFrame(string input, List<CommandSpec> commands, List<string> streamLog)
{
    AnsiConsole.Clear();
    RenderBootHeader();
    RenderStreamLog(streamLog);
    AnsiConsole.MarkupLine("[grey]Input[/]");
    AnsiConsole.Markup($"[bold deepskyblue1]unifocl[/] [grey]>[/] [bold white]{Markup.Escape(input)}[/]");
    AnsiConsole.WriteLine();

    if (input.StartsWith('/'))
    {
        ShowSuggestions(input, commands);
        return;
    }

    AnsiConsole.MarkupLine("[dim]Type / to open command palette.[/]");
}

static void RenderStreamLog(List<string> streamLog)
{
    if (streamLog.Count == 0)
    {
        return;
    }

    var visible = streamLog.TakeLast(14);
    foreach (var line in visible)
    {
        AnsiConsole.MarkupLine(line);
    }

    AnsiConsole.WriteLine();
}

static void RenderBootHeader()
{
    AnsiConsole.Write(
        new FigletText("unifocl")
            .LeftJustified()
            .Color(Color.DeepSkyBlue1));

    AnsiConsole.MarkupLine("[bold green]Welcome to unifocl[/]");
    AnsiConsole.WriteLine();

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

    AnsiConsole.MarkupLine($"[grey]{Markup.Escape(logo)}[/]");
    AnsiConsole.WriteLine();
    AnsiConsole.MarkupLine("[grey]No project attached.[/]");
    AnsiConsole.WriteLine();
}

static void ShowSuggestions(string query, List<CommandSpec> commands)
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
        AnsiConsole.MarkupLine($"[dim]no matches for {Markup.Escape(query)}[/]");
        return;
    }

    AnsiConsole.WriteLine();
    foreach (var match in matches)
    {
        AnsiConsole.MarkupLine($"[grey]{Markup.Escape(match.Signature)}[/] [dim]- {Markup.Escape(match.Description)}[/]");
    }
}

static CommandSpec? MatchCommand(string input, List<CommandSpec> commands)
{
    var normalized = input.Trim().ToLowerInvariant();

    return commands
        .OrderByDescending(c => c.Trigger.Length)
        .FirstOrDefault(c => normalized == c.Trigger
                             || normalized.StartsWith(c.Trigger + " ", StringComparison.OrdinalIgnoreCase));
}

static void WriteMockCommandStream(string input, List<CommandSpec> commands, List<string> streamLog)
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
        RenderComposerFrame(string.Empty, commands, streamLog);
        Thread.Sleep(110);
    }
}

static void AppendLog(List<string> streamLog, string line)
{
    streamLog.Add(line);
}

internal sealed record CommandSpec(string Signature, string Description, string Trigger);
