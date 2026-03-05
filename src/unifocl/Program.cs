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
    new("/open <path> [--allow-unsafe]", "Open project (starts/attaches daemon, loads project)", "/open"),
    new("/o <path>", "Alias for /open", "/o"),
    new("/close", "Detach from current project and stop attached daemon", "/close"),
    new("/c", "Alias for /close", "/c"),
    new("/quit", "Exit CLI client only (daemon keeps running)", "/quit"),
    new("/q", "Alias for /quit", "/q"),
    new("/daemon <start|stop|restart|ps|attach|detach>", "Manage daemon lifecycle", "/daemon"),
    new("/d <start|stop|restart|ps|attach|detach>", "Alias for /daemon", "/d"),
    new("/config <get|set|list|reset> [theme]", "Manage CLI preferences (theme)", "/config"),
    new("/cfg <get|set|list|reset> [theme]", "Alias for /config", "/cfg"),
    new("/status", "Show daemon/mode/editor/project/session status", "/status"),
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
    new("/new <project-name> [unity-version] [--allow-unsafe]", "Bootstrap a new Unity project", "/new"),
    new("/clone <git-url> [--allow-unsafe]", "Clone repo and set local CLI bridge config", "/clone"),
    new("/recent [idx] [--allow-unsafe]", "List recent projects (or open by index)", "/recent"),
    new("/daemon start [--port 8080] [--unity <path>] [--project <path>] [--headless] [--allow-unsafe]", "Start always-warm daemon (--headless = Host mode)", "/daemon start"),
    new("/daemon stop", "Stop daemon", "/daemon stop"),
    new("/daemon restart", "Restart daemon", "/daemon restart"),
    new("/daemon ps", "Show instances, ports, uptime, project", "/daemon ps"),
    new("/daemon attach <port>", "Attach CLI to existing daemon", "/daemon attach"),
    new("/daemon detach", "Detach CLI and keep daemon alive", "/daemon detach"),
    new("/init [path-to-project]", "Generate local bridge config and install editor-side CLI bridge dependencies", "/init"),
    new("/clear", "Clear and redraw boot screen", "/clear"),

    // Legacy compatibility commands (not all implemented yet)
    new("/doctor", "Run diagnostics for environment and tooling", "/doctor"),
    new("/logs [daemon|unity] [-f]", "Tail or follow daemon/unity logs", "/logs"),
    new("/scan [--root <dir>] [--depth n]", "Find Unity projects under a directory", "/scan"),
    new("/info <path>", "Read project metadata (Unity version/name/paths)", "/info"),
    new("/unity detect", "List installed Unity editors", "/unity detect"),
    new("/unity set <path>", "Set default Unity editor path", "/unity set"),
    new("/install-hook", "Install/validate Bridge mode integration", "/install-hook"),
    new("/examples", "Show common next-step flows", "/examples"),
    new("/keybinds", "Show modal keybinds/shortcuts", "/keybinds"),
    new("/shortcuts", "Alias for keybinds", "/shortcuts"),
    new("/update", "Check for CLI updates", "/update"),
    new("/version", "Show CLI and protocol version", "/version"),
    new("/protocol", "Show supported JSON schema capabilities", "/protocol"),
    new("/upm list [--outdated] [--builtin] [--git]", "List installed Unity packages (UPM)", "/upm list"),
    new("/upm ls [--outdated] [--builtin] [--git]", "Alias for /upm list", "/upm ls"),
    new("/upm", "Unity Package Manager commands", "/upm")
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
    new("mv <...>", "Alias for move", "mv"),
    new("upm list [--outdated] [--builtin] [--git]", "List installed Unity packages (UPM)", "upm list"),
    new("upm ls [--outdated] [--builtin] [--git]", "Alias for upm list", "upm ls")
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
    string? rawInput;
    try
    {
        rawInput = ReadInput(commands, projectCommands, streamLog, session);
    }
    catch (Exception ex)
    {
        LogUnhandledException(streamLog, ex, "input");
        continue;
    }

    if (rawInput is null)
    {
        try
        {
            await projectLifecycleService.PerformSafeExitCleanupAsync(
                session,
                daemonControlService,
                daemonRuntime,
                line => AppendLog(streamLog, line));
        }
        catch (Exception ex)
        {
            LogUnhandledException(streamLog, ex, "shutdown");
        }

        CliTheme.MarkupLine("[grey]Input stream closed. Session ended.[/]");
        return;
    }

    try
    {
        var input = rawInput.Trim();
        if (string.IsNullOrWhiteSpace(input))
        {
            continue;
        }

        if (input.Equals(":focus-recent", StringComparison.OrdinalIgnoreCase))
        {
            await projectLifecycleService.TryHandleRecentSelectionToggleAsync(
                session,
                daemonControlService,
                daemonRuntime,
                line => AppendLog(streamLog, line));
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

        if (matched.Trigger.StartsWith("/upm", StringComparison.Ordinal))
        {
            if (session.Mode != CliMode.Project || string.IsNullOrWhiteSpace(session.CurrentProjectPath))
            {
                AppendLog(streamLog, "[yellow]mode[/]: open a project first with /open");
                continue;
            }

            var upmInput = input.Length > "/upm".Length
                ? $"upm {input["/upm".Length..].Trim()}"
                : "upm";
            var handled = await projectCommandRouterService.TryHandleProjectCommandAsync(
                upmInput,
                session,
                daemonControlService,
                daemonRuntime,
                line => AppendLog(streamLog, line));
            if (!handled)
            {
                AppendLog(streamLog, "[yellow]upm[/]: unsupported /upm command");
            }

            continue;
        }

        if (matched.Trigger is "/keybinds" or "/shortcuts")
        {
            WriteKeybindsHelp(streamLog, session);
            continue;
        }

        if (matched.Trigger == "/version")
        {
            var processPath = Environment.ProcessPath ?? AppContext.BaseDirectory;
            AppendLog(streamLog, $"[grey]version[/]: cli [white]{Markup.Escape(CliVersion.SemVer)}[/], protocol [white]{Markup.Escape(CliVersion.Protocol)}[/]");
            AppendLog(streamLog, $"[grey]binary[/]: [white]{Markup.Escape(processPath)}[/]");
            continue;
        }

        if (matched.Trigger == "/protocol")
        {
            AppendLog(streamLog, $"[grey]protocol[/]: [white]{Markup.Escape(CliVersion.Protocol)}[/]");
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
            if ((matched.Trigger == "/open" || matched.Trigger == "/new" || matched.Trigger == "/clone" || matched.Trigger == "/recent")
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

        AppendLog(streamLog, $"[yellow]command[/]: not implemented yet -> {Markup.Escape(matched.Signature)}");
        AppendLog(streamLog, "[grey]hint[/]: run /help for implemented commands and mode-specific workflows");
    }
    catch (Exception ex)
    {
        LogUnhandledException(streamLog, ex, "command");
    }
}

static string? ReadInput(
    List<CommandSpec> commands,
    List<CommandSpec> projectCommands,
    List<string> streamLog,
    CliSessionState session)
{
    if (Console.IsInputRedirected)
    {
        CliTheme.Markup($"{BuildPromptLabelMarkup(session)} [grey]>[/] ");
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
    var intellisenseSelectionArmed = false;
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
                if (intellisenseSelectionArmed
                    && candidates.Count > 0
                    && selectedIntellisenseCandidateIndex >= 0
                    && selectedIntellisenseCandidateIndex < candidates.Count
                    && !string.IsNullOrWhiteSpace(candidates[selectedIntellisenseCandidateIndex].CommitCommand))
                {
                    input.Clear();
                    input.Append(candidates[selectedIntellisenseCandidateIndex].CommitCommand!);
                    intellisenseDismissed = false;
                    intellisenseSelectionArmed = false;
                    break;
                }

                Console.WriteLine();
                return input.ToString();
            case ConsoleKey.Backspace:
                if (input.Length > 0)
                {
                    input.Remove(input.Length - 1, 1);
                }

                intellisenseDismissed = false;
                intellisenseSelectionArmed = false;
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
                intellisenseSelectionArmed = false;
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
                    intellisenseSelectionArmed = true;
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
                    intellisenseSelectionArmed = true;
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

                if (input.Length == 0
                    && (session.Mode != CliMode.Project || string.IsNullOrWhiteSpace(session.CurrentProjectPath))
                    && session.RecentProjectEntries.Count > 0)
                {
                    Console.WriteLine();
                    return ":focus-recent";
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
                    intellisenseSelectionArmed = false;
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
    var lines = new List<string>();
    lines.AddRange(BuildComposerUnityLogPane(session));
    lines.AddRange(new[]
    {
        "[grey]Input[/]",
        $"{BuildPromptLabelMarkup(session)} [grey]>[/] [bold white]{Markup.Escape(input)}[/]"
    });

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

static IEnumerable<string> BuildComposerUnityLogPane(CliSessionState session)
{
    if (session.UnityLogPane.Count == 0)
    {
        return [];
    }

    const int maxLogRows = 6;
    const int fallbackPaneWidth = 78;
    var paneWidth = Math.Max(30, (Console.IsOutputRedirected ? fallbackPaneWidth : Console.WindowWidth) - 2);
    var border = new string('─', paneWidth);
    var lines = new List<string>
    {
        $"┌{border}┐",
        $"│{FitForPane(" UNITY LOG ", paneWidth)}│"
    };

    var visible = session.UnityLogPane.Skip(Math.Max(0, session.UnityLogPane.Count - maxLogRows)).ToList();

    for (var i = 0; i < maxLogRows; i++)
    {
        var content = i < visible.Count ? visible[i] : string.Empty;
        lines.Add($"│{FitForPane(content, paneWidth)}│");
    }

    lines.Add($"└{border}┘");
    return lines;
}

static string FitForPane(string text, int width)
{
    if (string.IsNullOrEmpty(text))
    {
        return new string(' ', width);
    }

    var normalized = text.Replace(Environment.NewLine, " ").Replace("\n", " ").Replace("\r", " ");
    if (normalized.Length > width)
    {
        normalized = normalized[..Math.Max(0, width - 1)] + "…";
    }

    if (normalized.Length < width)
    {
        normalized = normalized.PadRight(width);
    }

    return Markup.Escape(normalized);
}

static string BuildPromptLabelMarkup(CliSessionState session)
{
    var context = session.Inspector;
    if (context is not null)
    {
        var escapedPromptPath = Markup.Escape(context.PromptPath);
        return context.Depth == InspectorDepth.ComponentList
            ? $"[bold deepskyblue1]UnityCLI[/][grey]:[/][{CliTheme.Info}]{escapedPromptPath}[/] [grey][inspect][/]"
            : $"[bold deepskyblue1]UnityCLI[/][grey]:[/][{CliTheme.Info}]{escapedPromptPath}[/]";
    }

    if (session.ContextMode == CliContextMode.Project && !string.IsNullOrWhiteSpace(session.CurrentProjectPath))
    {
        var safeLabel = session.SafeModeEnabled ? " [yellow][safe][/]" : string.Empty;
        return $"[bold deepskyblue1]unifocl[/][grey]:[/][{CliTheme.Info}]{Markup.Escape(session.CurrentProjectPath)}[/]{safeLabel}";
    }

    return "[bold deepskyblue1]unifocl[/]";
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
    const int logoWidth = 72;
    const int logoHeight = 18;

    EnsureLogoViewport(streamLog, logoWidth, logoHeight);

    streamLog.Add("[bold deepskyblue1]unifocl[/]");
    streamLog.Add("[bold green]Welcome to unifocl[/]");
    streamLog.Add($"[grey]cli[/]: [white]{Markup.Escape(CliVersion.SemVer)}[/] ([white]{Markup.Escape(CliVersion.Protocol)}[/])");
    streamLog.Add(string.Empty);

    var logo = """
                           ███      ███                            ████
                          █████   █████                            ████
                          █████   █████                            ████
                           ███   ██████                            ████
                                 █████                             ████
████   ████  ████ ████     ███  ██████     ██████         ██████   ████
████   ████  ██████████   █████ ███████  █████████       ████████  ████
████   ████  ███████████  █████ ███████  ██████████     ██████████ ████
████   ████  ███████████  █████ ███████ ████████████   ██████████  ████
████   ████  █████  ████  █████  ████   ████   █████   █████  ██   ████
████   ████  ████   ████  █████  ████  █████    █████ █████        ████
████   ████  ████   ████  █████  ████  █████    █████ █████        ████
████   ████  ████   ████  █████  ████  █████    █████ █████        ████
████   ████  ████   ████  █████  ████  █████    ████   █████       ████
███████████  ████   ████  █████  ████   ████████████   █████████   ████
███████████  ████   ████  █████  ████   ███████████     █████████  ████
 █████████   ████   ████  █████  ████    ██████████     █████████  ████
  ███████    ████   ████  █████  ████      ██████         ██████   ████                                                                                                                                                    
""";

    foreach (var line in logo.Split('\n'))
    {
        streamLog.Add($"[{CliTheme.Brand}]{Markup.Escape(line)}[/]");
    }

    streamLog.Add(string.Empty);
    streamLog.Add("[grey]No project attached.[/]");
    streamLog.Add(string.Empty);
}

static void EnsureLogoViewport(List<string> streamLog, int minimumWidth, int minimumHeight)
{
    if (Console.IsOutputRedirected)
    {
        return;
    }

    try
    {
        var currentWindowWidth = Console.WindowWidth;
        var currentWindowHeight = Console.WindowHeight;
        var maxWindowWidth = Console.LargestWindowWidth;
        var maxWindowHeight = Console.LargestWindowHeight;

        var targetWindowWidth = Math.Min(Math.Max(currentWindowWidth, minimumWidth), maxWindowWidth);
        var targetWindowHeight = Math.Min(Math.Max(currentWindowHeight, minimumHeight), maxWindowHeight);

        if (targetWindowWidth > 0 && targetWindowHeight > 0)
        {
            try
            {
                var targetBufferWidth = Math.Max(Console.BufferWidth, targetWindowWidth);
                var targetBufferHeight = Math.Max(Console.BufferHeight, targetWindowHeight);
                if (targetBufferWidth != Console.BufferWidth || targetBufferHeight != Console.BufferHeight)
                {
                    Console.SetBufferSize(targetBufferWidth, targetBufferHeight);
                }
            }
            catch
            {
                // Some terminals do not support buffer resizing.
            }

            if (targetWindowWidth != currentWindowWidth || targetWindowHeight != currentWindowHeight)
            {
                Console.SetWindowSize(targetWindowWidth, targetWindowHeight);
            }
        }
    }
    catch
    {
        // Non-fatal: keep startup resilient on terminals that disallow resize APIs.
    }

    try
    {
        if (Console.WindowWidth < minimumWidth || Console.WindowHeight < minimumHeight)
        {
            streamLog.Add($"[yellow]note[/]: logo expects terminal >= {minimumWidth}x{minimumHeight}; current is {Console.WindowWidth}x{Console.WindowHeight}.");
        }
    }
    catch
    {
        // Ignore size probe failures.
    }
}

static void WriteKeybindsHelp(List<string> streamLog, CliSessionState session)
{
    AppendLog(streamLog, "[bold deepskyblue1]unifocl[/] [grey]>[/] [white]/keybinds[/]");
    AppendLog(streamLog, "[grey]keybinds[/]: global");
    AppendLog(streamLog, "[grey]keybinds[/]: [white]F7[/] enter/exit hierarchy focus mode (inside /hierarchy)");
    AppendLog(streamLog, "[grey]keybinds[/]: [white]F7[/] enter/exit project focus mode (project context), or recent selection mode after /recent");
    AppendLog(streamLog, "[grey]keybinds[/]: [white]F8[/] enter/exit inspector focus mode (inspector context)");
    AppendLog(streamLog, "[grey]keybinds[/]: [white]Esc[/] dismiss intellisense (or clear input if already dismissed)");
    AppendLog(streamLog, "[grey]keybinds[/]: [white]↑/↓[/] fuzzy candidate selection in composer");
    AppendLog(streamLog, "[grey]keybinds[/]: [white]Enter[/] insert selected intellisense suggestion, or commit input when none selected");

    AppendLog(streamLog, "[grey]keybinds[/]: hierarchy focus");
    AppendLog(streamLog, "[grey]keybinds[/]: [white]↑/↓[/] move highlighted GameObject");
    AppendLog(streamLog, "[grey]keybinds[/]: [white]Tab[/] expand selected node");
    AppendLog(streamLog, "[grey]keybinds[/]: [white]Shift+Tab[/] collapse selected node");
    AppendLog(streamLog, "[grey]keybinds[/]: [white]Esc/F7[/] exit focus mode");

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
        AppendLog(streamLog, "[grey]keybinds[/]: current context -> hierarchy (F7 available now)");
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

    if (TryGetUpmComposerCandidates(input, session, out var upmCandidates))
    {
        candidates = upmCandidates;
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

static bool TryGetUpmComposerCandidates(
    string input,
    CliSessionState session,
    out List<(string Label, string? CommitCommand)> candidates)
{
    candidates = [];
    var trimmed = input.TrimStart();
    var isSlash = trimmed.StartsWith("/upm", StringComparison.OrdinalIgnoreCase);
    var isProjectCommand = trimmed.StartsWith("upm", StringComparison.OrdinalIgnoreCase);
    if (!isSlash && !isProjectCommand)
    {
        return false;
    }

    var prefix = isSlash ? "/upm" : "upm";
    var suffix = trimmed.Length > prefix.Length
        ? trimmed[prefix.Length..].TrimStart()
        : string.Empty;

    if (string.IsNullOrWhiteSpace(suffix))
    {
        candidates.Add((isSlash
            ? "/upm list [--outdated] [--builtin] [--git]"
            : "upm list [--outdated] [--builtin] [--git]", isSlash ? "/upm list" : "upm list"));
        candidates.Add((isSlash ? "/upm ls" : "upm ls", isSlash ? "/upm ls" : "upm ls"));
        return true;
    }

    var upmSuggestions = new List<(string Label, string? CommitCommand)>
    {
        (isSlash ? "/upm list [--outdated] [--builtin] [--git]" : "upm list [--outdated] [--builtin] [--git]", isSlash ? "/upm list" : "upm list"),
        (isSlash ? "/upm ls [--outdated] [--builtin] [--git]" : "upm ls [--outdated] [--builtin] [--git]", isSlash ? "/upm ls" : "upm ls")
    };

    var suffixLower = suffix.ToLowerInvariant();
    candidates = upmSuggestions
        .Where(candidate => candidate.Label.Contains(suffixLower, StringComparison.OrdinalIgnoreCase)
                            || candidate.CommitCommand?.Contains(suffixLower, StringComparison.OrdinalIgnoreCase) == true)
        .Take(10)
        .ToList();

    if (suffixLower.StartsWith("list", StringComparison.OrdinalIgnoreCase)
        || suffixLower.StartsWith("ls", StringComparison.OrdinalIgnoreCase))
    {
        var commandHead = isSlash
            ? (suffixLower.StartsWith("ls", StringComparison.OrdinalIgnoreCase) ? "/upm ls" : "/upm list")
            : (suffixLower.StartsWith("ls", StringComparison.OrdinalIgnoreCase) ? "upm ls" : "upm list");
        var flags = new[] { "--outdated", "--builtin", "--git" };
        foreach (var flag in flags)
        {
            candidates.Add(($"{commandHead} {flag}", $"{commandHead} {flag}"));
        }
    }

    var packageRefs = session.ProjectView.LastUpmPackages.Take(5).ToList();
    if (packageRefs.Count > 0)
    {
        foreach (var package in packageRefs)
        {
            var label = $"[{package.Index}] {package.DisplayName} ({package.PackageId})";
            candidates.Add((label, null));
        }
    }

    candidates = candidates.DistinctBy(x => x.Label).Take(10).ToList();
    return true;
}

static IEnumerable<string> GetSuggestionLines(string query, List<CommandSpec> commands, int selectedSuggestionIndex)
{
    var matches = GetSuggestionMatches(query, commands);
    if (matches.Count == 0)
    {
        return new[]
        {
            "[grey]intellisense[/]: command suggestions [dim](up/down + enter to insert)[/]",
            $"[dim]no matches for {Markup.Escape(query)}[/]"
        };
    }

    var selected = Math.Clamp(selectedSuggestionIndex, 0, matches.Count - 1);
    var lines = new List<string>
    {
        "[grey]intellisense[/]: command suggestions [dim](up/down + enter to insert)[/]"
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
        .Where(c => !c.Description.StartsWith("Alias for", StringComparison.OrdinalIgnoreCase))
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

    lines.Add($"[grey]fuzzy[/]: {Markup.Escape(modeLabel)} [dim](up/down + enter to insert)[/]");
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

static void AppendLog(List<string> streamLog, string line)
{
    streamLog.Add(line);
    CliTheme.MarkupLine(line);
}

static void LogUnhandledException(List<string> streamLog, Exception ex, string phase)
{
    var typeName = ex.GetType().Name;
    AppendLog(streamLog, $"[red]error[/]: unhandled {Markup.Escape(phase)} exception <{Markup.Escape(typeName)}>");
    AppendLog(streamLog, $"[red]error[/]: {Markup.Escape(ex.Message)}");
    AppendLog(streamLog, "[yellow]system[/]: recovered and continuing session");
}
