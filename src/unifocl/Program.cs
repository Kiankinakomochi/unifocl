using Spectre.Console;
using System.Text;

var launchArgs = Environment.GetCommandLineArgs().Skip(1).ToArray();
if (DaemonControlService.TryParseDaemonServiceArgs(launchArgs, out var serviceOptions, out var daemonParseError))
{
    if (daemonParseError is not null)
    {
        CliTheme.MarkupLine($"[red]{Markup.Escape(daemonParseError)}[/]");
        Environment.ExitCode = 2;
        return;
    }

    await DaemonControlService.RunDaemonServiceAsync(serviceOptions!);
    return;
}

var commands = new List<CommandSpec>
{
    // System & lifecycle
    new("/open <path>", "Open project (starts/attaches daemon, loads project)", "/open"),
    new("/o <path>", "Alias for /open", "/o"),
    new("/close", "Detach from current project and stop attached daemon", "/close"),
    new("/c", "Alias for /close", "/c"),
    new("/quit", "Exit CLI client only (daemon keeps running)", "/quit"),
    new("/q", "Alias for /quit", "/q"),
    new("/daemon <start|stop|restart|ps|attach|detach>", "Manage daemon lifecycle", "/daemon"),
    new("/d <start|stop|restart|ps|attach|detach>", "Alias for /daemon", "/d"),
    new("/config <get|set|list|reset> [theme]", "Manage CLI preferences (theme)", "/config"),
    new("/cfg <get|set|list|reset> [theme]", "Alias for /config", "/cfg"),
    new("/status", "Show daemon/editor/project/session status", "/status"),
    new("/st", "Alias for /status", "/st"),
    new("/help [topic]", "Show help by topic", "/help"),
    new("/?", "Alias for /help", "/?"),

    // Mode switching
    new("/project", "Switch contextual command router to Project mode", "/project"),
    new("/p", "Alias for /project", "/p"),
    new("/hierarchy", "Switch to Hierarchy mode (interactive TUI)", "/hierarchy"),
    new("/h", "Alias for /hierarchy", "/h"),
    new("/inspect <idx|path>", "Switch to Inspector mode and focus target", "/inspect"),
    new("/i <idx|path>", "Alias for /inspect", "/i"),

    // Extended lifecycle (kept for compatibility)
    new("/new <project-name> [unity-version]", "Bootstrap a new Unity project", "/new"),
    new("/clone <git-url>", "Clone repo and set local CLI bridge config", "/clone"),
    new("/recent [n]", "List recently opened projects", "/recent"),
    new("/daemon start [--port 8080] [--unity <path>] [--project <path>] [--headless]", "Start always-warm daemon", "/daemon start"),
    new("/daemon stop", "Stop daemon", "/daemon stop"),
    new("/daemon restart", "Restart daemon", "/daemon restart"),
    new("/daemon ps", "Show instances, ports, uptime, project", "/daemon ps"),
    new("/daemon attach <port>", "Attach CLI to existing daemon", "/daemon attach"),
    new("/daemon detach", "Detach CLI and keep daemon alive", "/daemon detach"),
    new("/init [path-to-project]", "Install editor-side CLI bridge dependencies", "/init"),
    new("/clear", "Clear and redraw boot screen", "/clear"),

    // Legacy stubs (visible but non-authoritative schema)
    new("/doctor", "Run diagnostics for environment and tooling", "/doctor"),
    new("/logs [daemon|unity] [-f]", "Tail or follow daemon/unity logs", "/logs"),
    new("/scan [--root <dir>] [--depth n]", "Find Unity projects under a directory", "/scan"),
    new("/info <path>", "Read project metadata (Unity version/name/paths)", "/info"),
    new("/unity detect", "List installed Unity editors", "/unity detect"),
    new("/unity set <path>", "Set default Unity editor path", "/unity set"),
    new("/install-hook", "Install/validate Unity editor bridge", "/install-hook"),
    new("/examples", "Show common next-step flows", "/examples"),
    new("/keybinds", "Show modal keybinds/shortcuts", "/keybinds"),
    new("/shortcuts", "Alias for keybinds", "/shortcuts"),
    new("/update", "Check for CLI updates", "/update"),
    new("/version", "Show CLI and protocol version", "/version"),
    new("/protocol", "Show supported JSON schema capabilities", "/protocol")
};

var projectCommands = new List<CommandSpec>
{
    new("list", "List entries in active mode", "list"),
    new("ls", "Alias for list", "ls"),
    new("enter <idx>", "Enter selected node/folder/component", "enter"),
    new("cd <idx>", "Alias for enter", "cd"),
    new("up", "Navigate up one level in active mode", "up"),
    new("..", "Alias for up", ".."),
    new("make <type> <name>", "Create item in active mode", "make"),
    new("mk <type> <name>", "Alias for make", "mk"),
    new("load <idx|name>", "Load/open scene or script in project mode", "load"),
    new("remove <idx>", "Remove selected item in active mode", "remove"),
    new("rm <idx>", "Alias for remove", "rm"),
    new("rename <idx> <new-name>", "Rename selected item (mode dependent)", "rename"),
    new("rn <idx> <new-name>", "Alias for rename", "rn"),
    new("set <field> <value...>", "Set field/property in active mode", "set"),
    new("s <field> <value...>", "Alias for set", "s"),
    new("toggle <target>", "Toggle bool/active/enabled in active mode", "toggle"),
    new("t <target>", "Alias for toggle", "t"),
    new("f <query>", "Fuzzy find in active mode", "f"),
    new("ff <query>", "Alias for fuzzy find", "ff"),
    new("move <...>", "Move/reorder item in active mode", "move"),
    new("mv <...>", "Alias for move", "mv")
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
    var rawInput = ReadInput(commands, projectCommands, streamLog, session);
    if (rawInput is null)
    {
        await projectLifecycleService.PerformSafeExitCleanupAsync(
            session,
            daemonControlService,
            daemonRuntime,
            line => AppendLog(streamLog, line));
        CliTheme.MarkupLine("[grey]Input stream closed. Session ended.[/]");
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

        if (handledProjectCommand && session.AutoEnterHierarchyRequested)
        {
            session.AutoEnterHierarchyRequested = false;
            session.ContextMode = CliContextMode.Hierarchy;
            await hierarchyTui.RunAsync(
                session,
                daemonControlService,
                daemonRuntime,
                line => AppendLog(streamLog, line));
            if (session.Mode == CliMode.Project)
            {
                session.ContextMode = session.Inspector is null ? CliContextMode.Project : CliContextMode.Inspector;
            }
        }
        continue;
    }

    if (input == "/")
    {
        continue;
    }

    input = NormalizeSlashCommand(input);
    var matched = MatchCommand(input, commands);
    if (matched is null)
    {
        AppendLog(streamLog, $"[grey]system[/]: unknown command [white]{Markup.Escape(input)}[/]");
        continue;
    }

    if (matched.Trigger == "/quit")
    {
        CliTheme.MarkupLine("[grey]Session closed.[/]");
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
        session.ContextMode = CliContextMode.Hierarchy;
        await hierarchyTui.RunAsync(
            session,
            daemonControlService,
            daemonRuntime,
            line => AppendLog(streamLog, line));
        if (session.Mode == CliMode.Project)
        {
            session.ContextMode = session.Inspector is null ? CliContextMode.Project : CliContextMode.Inspector;
        }
        continue;
    }

    if (matched.Trigger == "/project")
    {
        if (session.Mode != CliMode.Project || string.IsNullOrWhiteSpace(session.CurrentProjectPath))
        {
            AppendLog(streamLog, "[yellow]mode[/]: open a project first with /open");
            continue;
        }

        session.ContextMode = CliContextMode.Project;
        session.Inspector = null;
        await projectCommandRouterService.TryHandleProjectCommandAsync(
            string.Empty,
            session,
            daemonControlService,
            daemonRuntime,
            line => AppendLog(streamLog, line));
        continue;
    }

    if (matched.Trigger == "/inspect")
    {
        if (session.Mode != CliMode.Project || string.IsNullOrWhiteSpace(session.CurrentProjectPath))
        {
            AppendLog(streamLog, "[yellow]mode[/]: open a project first with /open");
            continue;
        }

        var inspectInput = input.Length > "/inspect".Length
            ? $"inspect {input["/inspect".Length..].Trim()}"
            : "inspect";
        await projectCommandRouterService.TryHandleProjectCommandAsync(
            inspectInput,
            session,
            daemonControlService,
            daemonRuntime,
            line => AppendLog(streamLog, line));
        if (session.Inspector is not null)
        {
            session.ContextMode = CliContextMode.Inspector;
        }
        continue;
    }

    if (matched.Trigger is "/keybinds" or "/shortcuts")
    {
        WriteKeybindsHelp(streamLog, session);
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

static string? ReadInput(
    List<CommandSpec> commands,
    List<CommandSpec> projectCommands,
    List<string> streamLog,
    CliSessionState session)
{
    if (Console.IsInputRedirected)
    {
        CliTheme.Markup($"[bold deepskyblue1]{Markup.Escape(BuildPromptLabel(session))}[/] [grey]>[/] ");
        return Console.ReadLine();
    }

    return ReadInteractiveInput(commands, projectCommands, streamLog, session);
}

static string? ReadInteractiveInput(
    List<CommandSpec> commands,
    List<CommandSpec> projectCommands,
    List<string> streamLog,
    CliSessionState session)
{
    var input = new StringBuilder();
    var selectedIntellisenseCandidateIndex = 0;
    var intellisenseDismissed = false;
    var renderedLines = RenderComposerFrame(
        input.ToString(),
        commands,
        projectCommands,
        session,
        selectedIntellisenseCandidateIndex,
        intellisenseDismissed);

    while (true)
    {
        _ = TryGetComposerIntellisenseCandidates(input.ToString(), commands, projectCommands, session, out var allCandidates);
        var candidates = intellisenseDismissed ? [] : allCandidates;
        if (candidates.Count == 0)
        {
            selectedIntellisenseCandidateIndex = 0;
        }
        else
        {
            selectedIntellisenseCandidateIndex = Math.Clamp(selectedIntellisenseCandidateIndex, 0, candidates.Count - 1);
        }

        var key = Console.ReadKey(intercept: true);

        switch (key.Key)
        {
            case ConsoleKey.Enter:
                Console.WriteLine();
                if (candidates.Count > 0
                    && selectedIntellisenseCandidateIndex >= 0
                    && selectedIntellisenseCandidateIndex < candidates.Count
                    && !string.IsNullOrWhiteSpace(candidates[selectedIntellisenseCandidateIndex].CommitCommand))
                {
                    return candidates[selectedIntellisenseCandidateIndex].CommitCommand!;
                }

                return input.ToString();
            case ConsoleKey.Backspace:
                if (input.Length > 0)
                {
                    input.Remove(input.Length - 1, 1);
                }

                intellisenseDismissed = false;
                break;
            case ConsoleKey.Escape:
                if (!intellisenseDismissed && allCandidates.Count > 0)
                {
                    intellisenseDismissed = true;
                }
                else
                {
                    input.Clear();
                    intellisenseDismissed = false;
                }

                selectedIntellisenseCandidateIndex = 0;
                break;
            case ConsoleKey.UpArrow:
                if (intellisenseDismissed && allCandidates.Count > 0)
                {
                    intellisenseDismissed = false;
                    candidates = allCandidates;
                }

                if (candidates.Count > 0)
                {
                    selectedIntellisenseCandidateIndex = selectedIntellisenseCandidateIndex <= 0
                        ? candidates.Count - 1
                        : selectedIntellisenseCandidateIndex - 1;
                }
                break;
            case ConsoleKey.DownArrow:
                if (intellisenseDismissed && allCandidates.Count > 0)
                {
                    intellisenseDismissed = false;
                    candidates = allCandidates;
                }

                if (candidates.Count > 0)
                {
                    selectedIntellisenseCandidateIndex = selectedIntellisenseCandidateIndex >= candidates.Count - 1
                        ? 0
                        : selectedIntellisenseCandidateIndex + 1;
                }
                break;
            case ConsoleKey.F7:
                if (input.Length == 0
                    && session.ContextMode == CliContextMode.Project
                    && !string.IsNullOrWhiteSpace(session.CurrentProjectPath))
                {
                    Console.WriteLine();
                    return ":focus-project";
                }
                break;
            case ConsoleKey.F8:
                if (input.Length == 0
                    && session.ContextMode == CliContextMode.Inspector
                    && session.Inspector is not null)
                {
                    Console.WriteLine();
                    return ":focus-inspector";
                }
                break;
            default:
                if (!char.IsControl(key.KeyChar))
                {
                    input.Append(key.KeyChar);
                    intellisenseDismissed = false;
                }
                break;
        }

        ClearComposerFrame(renderedLines);
        renderedLines = RenderComposerFrame(
            input.ToString(),
            commands,
            projectCommands,
            session,
            selectedIntellisenseCandidateIndex,
            intellisenseDismissed);
    }
}

static int RenderComposerFrame(
    string input,
    List<CommandSpec> commands,
    List<CommandSpec> projectCommands,
    CliSessionState session,
    int selectedFuzzyCandidateIndex,
    bool suppressIntellisense)
{
    var lines = new List<string>
    {
        "[grey]Input[/]",
        $"[bold deepskyblue1]{Markup.Escape(BuildPromptLabel(session))}[/] [grey]>[/] [bold white]{Markup.Escape(input)}[/]"
    };

    if (suppressIntellisense)
    {
        lines.Add("[dim]intellisense dismissed (Esc). Type or use ↑/↓ to reopen suggestions.[/]");
    }
    else if (input.StartsWith('/'))
    {
        lines.Add(string.Empty);
        lines.AddRange(GetSuggestionLines(input, commands, selectedFuzzyCandidateIndex));
    }
    else if (TryGetFuzzyQueryIntellisenseLines(input, session, selectedFuzzyCandidateIndex, out var fuzzyLines))
    {
        lines.Add(string.Empty);
        lines.AddRange(fuzzyLines);
    }
    else if (!string.IsNullOrWhiteSpace(input))
    {
        lines.Add(string.Empty);
        lines.AddRange(GetSuggestionLines(input, projectCommands, selectedFuzzyCandidateIndex));
    }
    else if (session.Inspector is not null)
    {
        lines.Add("[dim]Inspector mode: list, enter <idx>, up, set <field> <value>, toggle <field|idx>, scroll [body|stream] <up|down> [n], F8 focus nav[/]");
    }
    else if (session.ContextMode == CliContextMode.Project && !string.IsNullOrWhiteSpace(session.CurrentProjectPath))
    {
        lines.Add("[dim]Project mode: list, enter <idx>, up, f <query>, make script <name>, load <idx|name>, rename <idx> <new>, remove <idx>, move <...>, F7 focus nav[/]");
    }
    else
    {
        lines.Add("[dim]Type / to open command palette. Use your mouse wheel to scroll log history.[/]");
    }

    foreach (var line in lines)
    {
        CliTheme.MarkupLine(line);
    }

    return lines.Count;
}

static string BuildPromptLabel(CliSessionState session)
{
    var context = session.Inspector;
    if (context is not null)
    {
        return context.PromptLabel;
    }

    if (session.ContextMode == CliContextMode.Project && !string.IsNullOrWhiteSpace(session.CurrentProjectPath))
    {
        return "unifocl:project";
    }

    return "unifocl";
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
        CliTheme.MarkupLine(line);
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
        streamLog.Add($"[{CliTheme.Brand}]{Markup.Escape(line)}[/]");
    }

    streamLog.Add(string.Empty);
    streamLog.Add("[grey]No project attached.[/]");
    streamLog.Add(string.Empty);
}

static void WriteKeybindsHelp(List<string> streamLog, CliSessionState session)
{
    AppendLog(streamLog, "[bold deepskyblue1]unifocl[/] [grey]>[/] [white]/keybinds[/]");
    AppendLog(streamLog, "[grey]keybinds[/]: global");
    AppendLog(streamLog, "[grey]keybinds[/]: [white]F6[/] enter/exit hierarchy focus mode (inside /hierarchy)");
    AppendLog(streamLog, "[grey]keybinds[/]: [white]F7[/] enter/exit project focus mode (project context)");
    AppendLog(streamLog, "[grey]keybinds[/]: [white]F8[/] enter/exit inspector focus mode (inspector context)");
    AppendLog(streamLog, "[grey]keybinds[/]: [white]Esc[/] dismiss intellisense (or clear input if already dismissed)");
    AppendLog(streamLog, "[grey]keybinds[/]: [white]↑/↓[/] fuzzy candidate selection in composer");
    AppendLog(streamLog, "[grey]keybinds[/]: [white]Enter[/] commit command/input");

    AppendLog(streamLog, "[grey]keybinds[/]: hierarchy focus");
    AppendLog(streamLog, "[grey]keybinds[/]: [white]↑/↓[/] move highlighted GameObject");
    AppendLog(streamLog, "[grey]keybinds[/]: [white]Tab[/] expand selected node");
    AppendLog(streamLog, "[grey]keybinds[/]: [white]Shift+Tab[/] collapse selected node");
    AppendLog(streamLog, "[grey]keybinds[/]: [white]Esc/F6[/] exit focus mode");

    AppendLog(streamLog, "[grey]keybinds[/]: project focus");
    AppendLog(streamLog, "[grey]keybinds[/]: [white]↑/↓[/] move highlighted file/folder");
    AppendLog(streamLog, "[grey]keybinds[/]: [white]Tab[/] reveal/open selected entry");
    AppendLog(streamLog, "[grey]keybinds[/]: [white]Shift+Tab[/] move to parent folder");
    AppendLog(streamLog, "[grey]keybinds[/]: [white]Esc/F7[/] exit focus mode");

    AppendLog(streamLog, "[grey]keybinds[/]: inspector focus");
    AppendLog(streamLog, "[grey]keybinds[/]: [white]↑/↓[/] move highlighted component/field");
    AppendLog(streamLog, "[grey]keybinds[/]: [white]Tab[/] inspect selected component");
    AppendLog(streamLog, "[grey]keybinds[/]: [white]Shift+Tab[/] back to component list");
    AppendLog(streamLog, "[grey]keybinds[/]: [white]Esc/F8[/] exit focus mode");

    if (session.ContextMode == CliContextMode.Project && session.Inspector is null)
    {
        AppendLog(streamLog, "[grey]keybinds[/]: current context -> project (F7 available now)");
    }
    else if (session.ContextMode == CliContextMode.Inspector && session.Inspector is not null)
    {
        AppendLog(streamLog, "[grey]keybinds[/]: current context -> inspector (F8 available now)");
    }
    else if (session.ContextMode == CliContextMode.Hierarchy)
    {
        AppendLog(streamLog, "[grey]keybinds[/]: current context -> hierarchy (F6 available now)");
    }
    else
    {
        AppendLog(streamLog, "[grey]keybinds[/]: current context -> boot/general");
    }

    AppendLog(streamLog, "[yellow]note[/]: if your terminal does not emit [white]Shift+Tab[/] distinctly, use typed command [white]up[/] as fallback.");
}

static bool TryGetComposerIntellisenseCandidates(
    string input,
    List<CommandSpec> commands,
    List<CommandSpec> projectCommands,
    CliSessionState session,
    out List<(string Label, string? CommitCommand)> candidates)
{
    candidates = [];

    if (TryGetFuzzyComposerCandidates(input, session, out var fuzzyCandidates, out _, out _))
    {
        candidates = fuzzyCandidates
            .Select(candidate => (candidate.Path, candidate.CommitCommand))
            .ToList();
        return true;
    }

    if (input.StartsWith('/'))
    {
        candidates = GetSuggestionMatches(input, commands)
            .Select(match => (match.Signature, (string?)match.Trigger))
            .ToList();
        return true;
    }

    if (!string.IsNullOrWhiteSpace(input))
    {
        candidates = GetSuggestionMatches(input, projectCommands)
            .Select(match => (match.Signature, (string?)match.Trigger))
            .ToList();
        return true;
    }

    return false;
}

static IEnumerable<string> GetSuggestionLines(string query, List<CommandSpec> commands, int selectedSuggestionIndex)
{
    var matches = GetSuggestionMatches(query, commands);
    if (matches.Count == 0)
    {
        return new[]
        {
            "[grey]intellisense[/]: command suggestions [dim](up/down + enter)[/]",
            $"[dim]no matches for {Markup.Escape(query)}[/]"
        };
    }

    var selected = Math.Clamp(selectedSuggestionIndex, 0, matches.Count - 1);
    var lines = new List<string>
    {
        "[grey]intellisense[/]: command suggestions [dim](up/down + enter)[/]"
    };
    for (var i = 0; i < matches.Count; i++)
    {
        var match = matches[i];
        var selectedLine = i == selected;
        var prefix = selectedLine
            ? $"[{CliTheme.CursorForeground} on {CliTheme.CursorBackground}]>[/]"
            : "[grey] [/]";
        var escapedSignature = Markup.Escape(match.Signature);
        var escapedDescription = Markup.Escape(match.Description);
        lines.Add(selectedLine
            ? $"{prefix} [{CliTheme.CursorForeground} on {CliTheme.CursorBackground}]{escapedSignature}[/] [dim]- {escapedDescription}[/]"
            : $"{prefix} [grey]{escapedSignature}[/] [dim]- {escapedDescription}[/]");
    }

    return lines;
}

static List<CommandSpec> GetSuggestionMatches(string query, List<CommandSpec> commands)
{
    var normalized = query.Trim().ToLowerInvariant();
    return commands
        .Where(c => c.Signature.Contains(normalized, StringComparison.OrdinalIgnoreCase)
                    || c.Description.Contains(normalized, StringComparison.OrdinalIgnoreCase)
                    || c.Trigger.StartsWith(normalized, StringComparison.OrdinalIgnoreCase)
                    || normalized.StartsWith(c.Trigger, StringComparison.OrdinalIgnoreCase))
        .Take(14)
        .ToList();
}

static bool TryGetFuzzyQueryIntellisenseLines(string input, CliSessionState session, int selectedFuzzyCandidateIndex, out List<string> lines)
{
    lines = [];
    if (!TryGetFuzzyComposerCandidates(input, session, out var candidates, out var modeLabel, out var emptyLabel))
    {
        return false;
    }

    lines.Add($"[grey]fuzzy[/]: {Markup.Escape(modeLabel)} [dim](up/down + enter)[/]");
    if (candidates.Count == 0)
    {
        lines.Add($"[dim]{Markup.Escape(emptyLabel)}[/]");
        return true;
    }

    var selected = Math.Clamp(selectedFuzzyCandidateIndex, 0, candidates.Count - 1);
    for (var i = 0; i < candidates.Count && i < 10; i++)
    {
        var candidate = candidates[i];
        var selectedLine = i == selected;
        var prefix = selectedLine
            ? $"[{CliTheme.CursorForeground} on {CliTheme.CursorBackground}]>[/]"
            : "[grey] [/]";
        var escapedPath = Markup.Escape(candidate.Path);
        lines.Add(selectedLine
            ? $"{prefix} [{CliTheme.CursorForeground} on {CliTheme.CursorBackground}]{i}[/] [{CliTheme.CursorForeground} on {CliTheme.CursorBackground}]{escapedPath}[/]"
            : $"{prefix} [deepskyblue1]{i}[/] {escapedPath}");
    }

    return true;
}

static bool TryGetFuzzyComposerCandidates(
    string input,
    CliSessionState session,
    out List<(string Path, string? CommitCommand)> candidates,
    out string modeLabel,
    out string emptyLabel)
{
    candidates = [];
    modeLabel = string.Empty;
    emptyLabel = string.Empty;
    if (!TryParseFuzzyQueryInput(input, out var query))
    {
        return false;
    }

    if (session.Inspector is not null)
    {
        modeLabel = "inspector query";
        candidates = GetInspectorFuzzyCandidates(session.Inspector, query);
        emptyLabel = "no inspector matches";
        return true;
    }

    if (session.ContextMode != CliContextMode.Project)
    {
        modeLabel = "available in project/inspector contexts";
        emptyLabel = "switch context to use fuzzy selection";
        return true;
    }

    modeLabel = "project query";
    candidates = GetProjectFuzzyCandidates(session.ProjectView, query);
    emptyLabel = session.ProjectView.AssetPathByInstanceId.Count == 0
        ? "asset index is cold; press Enter once to sync"
        : "no project matches";
    return true;
}

static bool TryParseFuzzyQueryInput(string input, out string query)
{
    query = string.Empty;
    if (string.IsNullOrWhiteSpace(input))
    {
        return false;
    }

    var trimmed = input.TrimStart();
    var firstSpace = trimmed.IndexOf(' ');
    var command = firstSpace < 0 ? trimmed : trimmed[..firstSpace];
    if (!command.Equals("f", StringComparison.OrdinalIgnoreCase)
        && !command.Equals("ff", StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    query = firstSpace < 0 ? string.Empty : trimmed[(firstSpace + 1)..].Trim();
    return true;
}

static List<(string Path, string? CommitCommand)> GetProjectFuzzyCandidates(ProjectViewState state, string query)
{
    var (typeFilter, term) = ParseProjectFuzzyQuery(query);
    var entries = state.AssetPathByInstanceId.Count > 0
        ? state.AssetPathByInstanceId.Values
        : state.VisibleEntries.Select(entry => entry.RelativePath);
    var matches = new List<ProjectFuzzyMatch>();
    foreach (var path in entries)
    {
        if (!PassesProjectTypeFilter(path, typeFilter))
        {
            continue;
        }

        var score = 1d;
        var matched = string.IsNullOrWhiteSpace(term) || FuzzyMatcher.TryScore(term, path, out score);
        if (!matched)
        {
            continue;
        }

        matches.Add(new ProjectFuzzyMatch(0, 0, path, score));
    }

    return matches
        .OrderByDescending(match => match.Score)
        .ThenBy(match => match.Path, StringComparer.OrdinalIgnoreCase)
        .Take(10)
        .Select(match => (match.Path, (string?)$"load \"{match.Path}\""))
        .ToList();
}

static List<(string Path, string? CommitCommand)> GetInspectorFuzzyCandidates(InspectorContext context, string query)
{
    var matches = new List<InspectorSearchResultDto>();
    if (context.Depth == InspectorDepth.ComponentList)
    {
        foreach (var component in context.Components)
        {
            if (FuzzyMatcher.TryScore(query, component.Name, out var score))
            {
                matches.Add(new InspectorSearchResultDto("component", component.Index, component.Name, component.Name, score));
            }
        }
    }
    else
    {
        foreach (var field in context.Fields)
        {
            var path = $"{context.SelectedComponentName}.{field.Name}";
            if (FuzzyMatcher.TryScore(query, path, out var score) || FuzzyMatcher.TryScore(query, field.Name, out score))
            {
                matches.Add(new InspectorSearchResultDto("field", context.SelectedComponentIndex, field.Name, path, score));
            }
        }
    }

    return matches
        .OrderByDescending(match => match.Score)
        .ThenBy(match => match.Path, StringComparer.OrdinalIgnoreCase)
        .Take(10)
        .Select(match =>
        {
            var commit = match.Scope.Equals("component", StringComparison.OrdinalIgnoreCase) && match.ComponentIndex is int componentIndex
                ? $"inspect {componentIndex}"
                : null;
            return (match.Path, commit);
        })
        .ToList();
}

static (string? TypeFilter, string Query) ParseProjectFuzzyQuery(string query)
{
    if (string.IsNullOrWhiteSpace(query))
    {
        return (null, string.Empty);
    }

    var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    string? typeFilter = null;
    var remaining = new List<string>();
    foreach (var token in tokens)
    {
        if (token.StartsWith("t:", StringComparison.OrdinalIgnoreCase))
        {
            typeFilter = token[2..];
            continue;
        }

        remaining.Add(token);
    }

    return (typeFilter, remaining.Count == 0 ? string.Empty : string.Join(' ', remaining));
}

static bool PassesProjectTypeFilter(string path, string? typeFilter)
{
    if (string.IsNullOrWhiteSpace(typeFilter))
    {
        return true;
    }

    var ext = Path.GetExtension(path).ToLowerInvariant();
    return typeFilter.ToLowerInvariant() switch
    {
        "script" => ext == ".cs",
        "scene" => ext == ".unity",
        "prefab" => ext == ".prefab",
        "material" => ext == ".mat",
        "animation" => ext is ".anim" or ".controller",
        _ => path.Contains(typeFilter, StringComparison.OrdinalIgnoreCase)
    };
}

static string NormalizeSlashCommand(string input)
{
    if (!input.StartsWith('/'))
    {
        return input;
    }

    var trimmed = input.Trim();
    var commandEnd = trimmed.IndexOf(' ');
    var commandToken = commandEnd >= 0 ? trimmed[..commandEnd] : trimmed;
    var rest = commandEnd >= 0 ? trimmed[commandEnd..] : string.Empty;

    var normalized = commandToken.ToLowerInvariant() switch
    {
        "/o" => "/open",
        "/c" => "/close",
        "/q" => "/quit",
        "/d" => "/daemon",
        "/cfg" => "/config",
        "/st" => "/status",
        "/?" => "/help",
        "/p" => "/project",
        "/h" => "/hierarchy",
        "/i" => "/inspect",
        "/exit" => "/quit",
        _ => commandToken
    };

    return normalized + rest;
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
    CliTheme.MarkupLine(line);
}
