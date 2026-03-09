using Spectre.Console;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;

var launchArgs = Environment.GetCommandLineArgs().Skip(1).ToArray();
if (BuildLogTailService.TryRunFromArgs(launchArgs))
{
    return;
}

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
    new("/dump <hierarchy|project|inspector> [--format json|yaml] [--compact] [--depth n] [--limit n]", "Dump deterministic mode state for agentic workflows", "/dump"),
    new("/upm list [--outdated] [--builtin] [--git]", "List installed Unity packages (UPM)", "/upm list"),
    new("/upm ls [--outdated] [--builtin] [--git]", "Alias for /upm list", "/upm ls"),
    new("/upm install <target>", "Install Unity package by registry ID, Git URL, or file: path", "/upm install"),
    new("/upm add <target>", "Alias for /upm install", "/upm add"),
    new("/upm i <target>", "Alias for /upm install", "/upm i"),
    new("/upm remove <id>", "Remove Unity package by package ID", "/upm remove"),
    new("/upm rm <id>", "Alias for /upm remove", "/upm rm"),
    new("/upm uninstall <id>", "Alias for /upm remove", "/upm uninstall"),
    new("/upm update [id]", "Update one package or all outdated packages", "/upm update"),
    new("/upm u [id]", "Alias for /upm update", "/upm u"),
    new("/upm", "Unity Package Manager commands", "/upm"),
    new("/build <run|exec|scenes|addressables|cancel|targets>", "Build pipeline commands", "/build"),
    new("/build run [target] [--dev] [--debug] [--clean] [--path <output-path>]", "Run Unity build for target (prompts when omitted)", "/build run"),
    new("/build exec <Method>", "Execute static build method (e.g., CI.Builder.BuildAndroidProd)", "/build exec"),
    new("/build scenes", "Open interactive scene build-settings TUI", "/build scenes"),
    new("/build addressables [--clean] [--update]", "Build Addressables content", "/build addressables"),
    new("/build cancel", "Request cancellation of an ongoing build", "/build cancel"),
    new("/build targets", "List installed Unity build support targets", "/build targets"),
    new("/build logs", "Open restartable build log tail viewer", "/build logs"),
    new("/b [target] [--dev] [--debug] [--clean] [--path <output-path>]", "Alias for /build run", "/b"),
    new("/bx <Method>", "Alias for /build exec", "/bx"),
    new("/ba [--clean] [--update]", "Alias for /build addressables", "/ba")
};

var projectCommands = new List<CommandSpec>
{
    new("list", "List entries in active mode", "list"),
    new("ls", "Alias for list", "ls"),
    new("enter <idx>", "Enter selected node/folder/component", "enter"),
    new("cd <idx>", "Alias for enter", "cd"),
    new("up", "Navigate up one level in active mode", "up"),
    new("..", "Alias for up", ".."),
    new("make --type <type> [--count <count>] [--name <name>] [--parent <idx|name>]", "Create typed asset(s) in project mode", "make"),
    new("mk <type> [count] [--name <name>|-n <name>] [--parent <idx|name>]", "Alias for make", "mk"),
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
    new("upm ls [--outdated] [--builtin] [--git]", "Alias for upm list", "upm ls"),
    new("upm install <target>", "Install Unity package by registry ID, Git URL, or file: path", "upm install"),
    new("upm add <target>", "Alias for upm install", "upm add"),
    new("upm i <target>", "Alias for upm install", "upm i"),
    new("upm remove <id>", "Remove Unity package by package ID", "upm remove"),
    new("upm rm <id>", "Alias for upm remove", "upm rm"),
    new("upm uninstall <id>", "Alias for upm remove", "upm uninstall"),
    new("upm update [id]", "Update one package or all outdated packages", "upm update"),
    new("upm u [id]", "Alias for upm update", "upm u"),
    new("build run [target] [--dev] [--debug] [--clean] [--path <output-path>]", "Run Unity build for target", "build run"),
    new("build exec <Method>", "Execute static build method", "build exec"),
    new("build scenes", "Open interactive scene build-settings TUI", "build scenes"),
    new("build addressables [--clean] [--update]", "Build Addressables content", "build addressables"),
    new("build cancel", "Request cancellation of an ongoing build", "build cancel"),
    new("build targets", "List installed Unity build support targets", "build targets"),
    new("build logs", "Open restartable build log tail viewer", "build logs"),
    new("b [target] [--dev] [--debug] [--clean] [--path <output-path>]", "Alias for build run", "b"),
    new("bx <Method>", "Alias for build exec", "bx"),
    new("ba [--clean] [--update]", "Alias for build addressables", "ba")
};

var inspectorCommands = new List<CommandSpec>
{
    new("inspect [idx|path]", "Enter inspector root target (default: current focus path)", "inspect"),
    new("list", "Refresh and list inspector items at current depth", "list"),
    new("ls", "Alias for list", "ls"),
    new("enter <idx>", "Inspect component by index", "enter"),
    new("cd <idx>", "Alias for enter", "cd"),
    new("up", "Step up (fields -> components, components -> project)", "up"),
    new("..", "Alias for up", ".."),
    new(":i", "Alias for up", ":i"),
    new("set <field> <value...>", "Set selected component field in inspector", "set"),
    new("s <field> <value...>", "Alias for set", "s"),
    new("set <Component>.<field> <value...>", "Set a field directly from inspector root", "set"),
    new("edit <field> <value...>", "Edit serialized field value for selected component", "edit"),
    new("e <field> <value...>", "Alias for edit", "e"),
    new("toggle <component-index|field>", "Toggle component enabled state or bool field", "toggle"),
    new("t <component-index|field>", "Alias for toggle", "t"),
    new("component add <type>", "Add a component from catalog to inspected target", "component add"),
    new("component remove <index|name>", "Remove a component from inspected target", "component remove"),
    new("comp <add|remove> <...>", "Alias for component", "comp"),
    new("f <query>", "Fuzzy find in inspector context", "f"),
    new("ff <query>", "Alias for fuzzy find", "ff"),
    new("scroll [body|stream] <up|down> [count]", "Scroll inspector body or command stream", "scroll"),
    new("make --type <type> [--count <count>]", "Create typed object(s) under inspected target", "make"),
    new("mk <type> [count] [--name <name>|-n <name>]", "Create typed object(s) under inspected target", "mk"),
    new("remove|rm", "Remove inspected target object", "remove"),
    new("rename|rn <new-name>", "Rename inspected target object", "rename"),
    new("move|mv </path|..|/>", "Move inspected target under another parent path", "move")
};

var streamLog = new List<string>();
var runtimePath = Path.Combine(Environment.CurrentDirectory, ".unifocl-runtime");
var daemonRuntime = new DaemonRuntime(runtimePath);
var session = new CliSessionState();
var daemonControlService = new DaemonControlService();
var projectLifecycleService = new ProjectLifecycleService();
var projectCommandRouterService = new ProjectCommandRouterService();
var hierarchyTui = new HierarchyTui();
var buildCommandService = new BuildCommandService();
if (TryParseExecLaunchOptions(launchArgs, out var execOptions, out var execError))
{
    if (!string.IsNullOrWhiteSpace(execError))
    {
        CliTheme.MarkupLine($"[red]{Markup.Escape(execError)}[/]");
        Environment.ExitCode = 2;
        return;
    }

    CliRuntimeState.SuppressConsoleOutput = execOptions!.Agentic;
    var envelope = await ExecuteOneShotCommandAsync(
        execOptions,
        commands,
        streamLog,
        session,
        daemonControlService,
        daemonRuntime,
        projectLifecycleService,
        projectCommandRouterService,
        hierarchyTui,
        buildCommandService);
    var payload = AgenticFormatter.SerializeEnvelope(envelope, execOptions.Format);
    Console.Out.WriteLine(payload);
    Environment.ExitCode = envelope.Meta.ExitCode;
    return;
}

SeedBootLog(streamLog);
RenderInitialLog(streamLog);

while (true)
{
    string? rawInput;
    try
    {
        rawInput = ReadInput(commands, projectCommands, inspectorCommands, streamLog, session);
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
            if (TryNormalizeProjectBuildCommand(input, out var normalizedBuildInput))
            {
                await buildCommandService.HandleBuildCommandAsync(
                    normalizedBuildInput,
                    session,
                    daemonControlService,
                    daemonRuntime,
                    line => AppendLog(streamLog, line));
                continue;
            }

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
                    line => AppendLog(streamLog, line),
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
                            line => AppendLog(streamLog, line));
                        if (session.Inspector is null)
                        {
                            return false;
                        }

                        session.ContextMode = CliContextMode.Inspector;
                        return true;
                    });
                if (session.Mode == CliMode.Project)
                {
                    session.ContextMode = session.Inspector is null ? CliContextMode.Project : CliContextMode.Inspector;
                    if (session.ContextMode == CliContextMode.Project
                        && !string.IsNullOrWhiteSpace(session.CurrentProjectPath))
                    {
                        await projectCommandRouterService.TryHandleProjectCommandAsync(
                            string.Empty,
                            session,
                            daemonControlService,
                            daemonRuntime,
                            line => AppendLog(streamLog, line));
                    }
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
                line => AppendLog(streamLog, line),
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
                        line => AppendLog(streamLog, line));
                    if (session.Inspector is null)
                    {
                        return false;
                    }

                    session.ContextMode = CliContextMode.Inspector;
                    return true;
                });
            if (session.Mode == CliMode.Project)
            {
                session.ContextMode = session.Inspector is null ? CliContextMode.Project : CliContextMode.Inspector;
                if (session.ContextMode == CliContextMode.Project
                    && !string.IsNullOrWhiteSpace(session.CurrentProjectPath))
                {
                    await projectCommandRouterService.TryHandleProjectCommandAsync(
                        string.Empty,
                        session,
                        daemonControlService,
                        daemonRuntime,
                        line => AppendLog(streamLog, line));
                }
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

        if (matched.Trigger.StartsWith("/build", StringComparison.Ordinal))
        {
            await buildCommandService.HandleBuildCommandAsync(
                input,
                session,
                daemonControlService,
                daemonRuntime,
                line => AppendLog(streamLog, line));
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
            AppendLog(streamLog, "[grey]agentic[/]: schema v1, formats=json|yaml, endpoints=/agent/exec,/agent/capabilities,/agent/status,/agent/dump/{hierarchy|project|inspector}");
            AppendLog(streamLog, "[grey]agentic[/]: exit-codes 0=success, 2=validation, 3=daemon-unavailable, 4=execution-error");
            continue;
        }

        if (matched.Trigger == "/dump")
        {
            await HandleDumpCommandAsync(input, session, streamLog);
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
            if (matched.Trigger == "/daemon attach")
            {
                await buildCommandService.NotifyAttachedBuildIfAnyAsync(
                    session,
                    line => AppendLog(streamLog, line));
            }
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
    List<CommandSpec> inspectorCommands,
    List<string> streamLog,
    CliSessionState session)
{
    if (Console.IsInputRedirected)
    {
        CliTheme.Markup($"{BuildPromptLabelMarkup(session)} [grey]>[/] ");
        return Console.ReadLine();
    }

    return ReadInteractiveInput(commands, projectCommands, inspectorCommands, streamLog, session);
}

static string? ReadInteractiveInput(
    List<CommandSpec> commands,
    List<CommandSpec> projectCommands,
    List<CommandSpec> inspectorCommands,
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
        inspectorCommands,
        session,
        selectedIntellisenseCandidateIndex,
        intellisenseDismissed);

    while (true)
    {
        _ = TryGetComposerIntellisenseCandidates(input.ToString(), commands, projectCommands, inspectorCommands, session, out var allCandidates);
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
                if ((intellisenseSelectionArmed
                     || ShouldCommitSuggestionOnEnter(input.ToString(), session, candidates))
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
                    && session.ContextMode == CliContextMode.Inspector
                    && session.Inspector is not null)
                {
                    Console.WriteLine();
                    return ":focus-inspector";
                }

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
            inspectorCommands,
            session,
            selectedIntellisenseCandidateIndex,
            intellisenseDismissed);
    }
}

static bool ShouldCommitSuggestionOnEnter(
    string input,
    CliSessionState session,
    IReadOnlyList<(string Label, string? CommitCommand)> candidates)
{
    if (candidates.Count == 0)
    {
        return false;
    }

    return IsMkTypeSelectionPhase(input, session)
        || IsInspectorComponentAddSelectionPhase(input, session);
}

static bool IsMkTypeSelectionPhase(string input, CliSessionState session)
{
    if (session.Inspector is not null
        || session.Mode != CliMode.Project
        || string.IsNullOrWhiteSpace(session.CurrentProjectPath))
    {
        return false;
    }

    var tokens = TokenizeComposerInput(input.TrimStart());
    if (tokens.Count == 0)
    {
        return false;
    }

    if (tokens[0].Equals("mk", StringComparison.OrdinalIgnoreCase))
    {
        return tokens.Count <= 1;
    }

    if (tokens[0].Equals("make", StringComparison.OrdinalIgnoreCase))
    {
        return !HasConcreteMakeType(tokens);
    }

    return false;
}

static bool IsInspectorComponentAddSelectionPhase(string input, CliSessionState session)
{
    var context = session.Inspector;
    if (context is null || context.Depth != InspectorDepth.ComponentList)
    {
        return false;
    }

    var tokens = TokenizeComposerInput(input.TrimStart());
    if (tokens.Count == 0)
    {
        return false;
    }

    var command = tokens[0].ToLowerInvariant();
    if (command is not ("component" or "comp"))
    {
        return false;
    }

    if (tokens.Count == 1)
    {
        return true;
    }

    return tokens[1].Equals("add", StringComparison.OrdinalIgnoreCase);
}

static int RenderComposerFrame(
    string input,
    List<CommandSpec> commands,
    List<CommandSpec> projectCommands,
    List<CommandSpec> inspectorCommands,
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
    else if (TryGetProjectMkTypeIntellisenseLines(input, session, selectedFuzzyCandidateIndex, out var mkTypeLines))
    {
        lines.Add(string.Empty);
        lines.AddRange(mkTypeLines);
    }
    else if (TryGetInspectorComponentIntellisenseLines(input, session, selectedFuzzyCandidateIndex, out var componentLines))
    {
        lines.Add(string.Empty);
        lines.AddRange(componentLines);
    }
    else if (TryGetFuzzyQueryIntellisenseLines(input, session, selectedFuzzyCandidateIndex, out var fuzzyLines))
    {
        lines.Add(string.Empty);
        lines.AddRange(fuzzyLines);
    }
    else if (!string.IsNullOrWhiteSpace(input))
    {
        var contextualCommands = session.Inspector is not null
            ? inspectorCommands
            : projectCommands;
        lines.Add(string.Empty);
        lines.AddRange(GetSuggestionLines(input, contextualCommands, selectedFuzzyCandidateIndex));
    }
    else if (session.Inspector is not null)
    {
        // Keep inspector idle composer minimal; avoid verbose default helper line.
        lines.Add(string.Empty);
    }
    else if (session.ContextMode == CliContextMode.Project && !string.IsNullOrWhiteSpace(session.CurrentProjectPath))
    {
        lines.Add("[dim]Project mode: list, enter <idx>, up, f <query>, mk <type> [count] [--name], load <idx|name>, rename <idx> <new>, remove <idx>, move <...>, F7 focus nav[/]");
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
            ? $"[bold deepskyblue1]UnityCLI[/][grey]:[/][{CliTheme.Info}]{escapedPromptPath}[/] [grey][[inspect]][/]"
            : $"[bold deepskyblue1]UnityCLI[/][grey]:[/][{CliTheme.Info}]{escapedPromptPath}[/]";
    }

    if (session.ContextMode == CliContextMode.Project && !string.IsNullOrWhiteSpace(session.CurrentProjectPath))
    {
        var safeLabel = session.SafeModeEnabled ? " [yellow][[safe]][/]" : string.Empty;
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
    AppendLog(streamLog, "[grey]keybinds[/]: [white]F7[/] enter/exit inspector focus mode (inspector context)");
    AppendLog(streamLog, "[grey]keybinds[/]: [white]Esc[/] dismiss intellisense (or clear input if already dismissed)");
    AppendLog(streamLog, "[grey]keybinds[/]: [white]↑/↓[/] fuzzy candidate selection in composer");
    AppendLog(streamLog, "[grey]keybinds[/]: [white]Enter[/] insert selected intellisense suggestion, or commit input when none selected");

    AppendLog(streamLog, "[grey]keybinds[/]: hierarchy focus");
    AppendLog(streamLog, "[grey]keybinds[/]: [white]↑/↓[/] move highlighted GameObject");
    AppendLog(streamLog, "[grey]keybinds[/]: [white]Tab[/] expand selected node");
    AppendLog(streamLog, "[grey]keybinds[/]: [white]Enter[/] enter inspector for selected node");
    AppendLog(streamLog, "[grey]keybinds[/]: [white]Shift+Tab[/] collapse selected node");
    AppendLog(streamLog, "[grey]keybinds[/]: [white]Esc/F7[/] exit focus mode");

    AppendLog(streamLog, "[grey]keybinds[/]: project focus");
    AppendLog(streamLog, "[grey]keybinds[/]: [white]↑/↓[/] move highlighted file/folder");
    AppendLog(streamLog, "[grey]keybinds[/]: [white]Tab[/] reveal/open selected entry");
    AppendLog(streamLog, "[grey]keybinds[/]: [white]Shift+Tab[/] move to parent folder");
    AppendLog(streamLog, "[grey]keybinds[/]: [white]Esc/F7[/] exit focus mode");

    AppendLog(streamLog, "[grey]keybinds[/]: inspector focus");
    AppendLog(streamLog, "[grey]keybinds[/]: [white]↑/↓[/] move highlighted component/field (auto-scrolls long lists)");
    AppendLog(streamLog, "[grey]keybinds[/]: [white]Tab[/] inspect selected component");
    AppendLog(streamLog, "[grey]keybinds[/]: [white]Enter[/] edit selected field (component inspection)");
    AppendLog(streamLog, "[grey]keybinds[/]: edit mode -> [white]Tab[/] next vector component / enum-bool option, [white]←/→[/] adjust/cycle, number keys edit vector/color component, [white]Enter[/] apply, [white]Esc[/] cancel");
    AppendLog(streamLog, "[grey]keybinds[/]: numeric edit -> type value directly, [white]Backspace[/] delete, [white]Enter[/] apply");
    AppendLog(streamLog, "[grey]keybinds[/]: text edit -> full-value overlay with cursor, [white]←/→[/] move cursor, [white]Backspace/Delete[/] edit");
    AppendLog(streamLog, "[grey]keybinds[/]: [white]Shift+Tab[/] back to component list");
    AppendLog(streamLog, "[grey]keybinds[/]: [white]F7[/] toggle between inspector interactive selection and command input (component inspection)");
    AppendLog(streamLog, "[grey]keybinds[/]: [white]Esc[/] fields -> component list, component list -> hierarchy");
    AppendLog(streamLog, "[grey]keybinds[/]: [white]F7[/] exit inspector focus mode");

    if (session.ContextMode == CliContextMode.Project && session.Inspector is null)
    {
        AppendLog(streamLog, "[grey]keybinds[/]: current context -> project (F7 available now)");
    }
    else if (session.ContextMode == CliContextMode.Inspector && session.Inspector is not null)
    {
        AppendLog(streamLog, "[grey]keybinds[/]: current context -> inspector (F7 available now)");
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
    List<CommandSpec> inspectorCommands,
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

    if (TryGetProjectMkTypeComposerCandidates(input, session, out var mkTypeCandidates))
    {
        candidates = mkTypeCandidates;
        return true;
    }

    if (TryGetInspectorComponentComposerCandidates(input, session, out var componentCandidates))
    {
        candidates = componentCandidates;
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
        var contextualCommands = session.Inspector is not null
            ? inspectorCommands
            : projectCommands;
        candidates = GetSuggestionMatches(input, contextualCommands)
            .Select(match => (match.Signature, (string?)match.Trigger))
            .ToList();
        return true;
    }

    return false;
}

static bool TryGetProjectMkTypeIntellisenseLines(
    string input,
    CliSessionState session,
    int selectedSuggestionIndex,
    out List<string> lines)
{
    lines = [];
    if (!TryGetProjectMkTypeComposerCandidates(input, session, out var candidates))
    {
        return false;
    }

    lines.Add("[grey]mk[/]: type suggestions [dim](fuzzy, up/down + enter to insert)[/]");
    if (candidates.Count == 0)
    {
        lines.Add("[dim]no mk type matches[/]");
        return true;
    }

    var selected = Math.Clamp(selectedSuggestionIndex, 0, candidates.Count - 1);
    for (var i = 0; i < candidates.Count; i++)
    {
        var candidate = candidates[i];
        var selectedLine = i == selected;
        var prefix = selectedLine
            ? $"[{CliTheme.CursorForeground} on {CliTheme.CursorBackground}]>[/]"
            : "[grey] [/]";
        var escaped = Markup.Escape(candidate.Label);
        lines.Add(selectedLine
            ? $"{prefix} [{CliTheme.CursorForeground} on {CliTheme.CursorBackground}]{escaped}[/]"
            : $"{prefix} [grey]{escaped}[/]");
    }

    if (TryBuildMkUsageHint(input, candidates, selected, out var usageHint))
    {
        lines.Add(string.Empty);
        lines.Add($"[dim]{Markup.Escape(usageHint)}[/]");
    }

    return true;
}

static bool TryGetProjectMkTypeComposerCandidates(
    string input,
    CliSessionState session,
    out List<(string Label, string? CommitCommand)> candidates)
{
    candidates = [];
    if (session.Inspector is not null
        || session.Mode != CliMode.Project
        || string.IsNullOrWhiteSpace(session.CurrentProjectPath))
    {
        return false;
    }

    var trimmed = input.TrimStart();
    if (trimmed.StartsWith('/'))
    {
        return false;
    }

    var tokens = TokenizeComposerInput(trimmed);
    if (tokens.Count == 0)
    {
        return false;
    }

    var isMk = tokens[0].Equals("mk", StringComparison.OrdinalIgnoreCase);
    var isMake = tokens[0].Equals("make", StringComparison.OrdinalIgnoreCase);
    if (!isMk && !isMake)
    {
        return false;
    }

    var mkTypeQuery = ResolveProjectMkTypeQuery(tokens, isMake);
    var matches = new List<(string Type, double Score)>();
    foreach (var type in ProjectMkCatalog.KnownTypes)
    {
        var display = FormatMkTypeDisplay(type);
        if (string.IsNullOrWhiteSpace(mkTypeQuery))
        {
            matches.Add((type, 1d));
            continue;
        }

        if (display.Contains(mkTypeQuery, StringComparison.OrdinalIgnoreCase)
            || type.Contains(mkTypeQuery, StringComparison.OrdinalIgnoreCase))
        {
            matches.Add((type, 0.9d));
            continue;
        }

        if (FuzzyMatcher.TryScore(mkTypeQuery, display, out var displayScore)
            || FuzzyMatcher.TryScore(mkTypeQuery, type, out displayScore))
        {
            matches.Add((type, displayScore));
        }
    }

    var ranked = matches
        .OrderByDescending(match => match.Score)
        .ThenBy(match => match.Type, StringComparer.OrdinalIgnoreCase)
        .ToList();
    if (ranked.Count == 0)
    {
        return true;
    }

    foreach (var match in ranked)
    {
        var display = FormatMkTypeDisplay(match.Type);
        if (isMake)
        {
            candidates.Add((
                $"make --type {display}",
                $"make --type {match.Type} "));
        }
        else
        {
            candidates.Add((
                $"mk {display}",
                $"mk {match.Type} "));
        }
    }

    return true;
}

static bool TryGetInspectorComponentIntellisenseLines(
    string input,
    CliSessionState session,
    int selectedSuggestionIndex,
    out List<string> lines)
{
    lines = [];
    if (!TryGetInspectorComponentComposerCandidates(input, session, out var candidates))
    {
        return false;
    }

    lines.Add("[grey]component[/]: add suggestions [dim](fuzzy, up/down + enter to insert)[/]");
    if (candidates.Count == 0)
    {
        lines.Add("[dim]no component matches[/]");
        return true;
    }

    const int maxSuggestions = 10;
    var visibleCount = Math.Min(maxSuggestions, candidates.Count);
    var selected = Math.Clamp(selectedSuggestionIndex, 0, visibleCount - 1);
    for (var i = 0; i < visibleCount; i++)
    {
        var candidate = candidates[i];
        var selectedLine = i == selected;
        var prefix = selectedLine
            ? $"[{CliTheme.CursorForeground} on {CliTheme.CursorBackground}]>[/]"
            : "[grey] [/]";
        var escaped = Markup.Escape(candidate.Label);
        lines.Add(selectedLine
            ? $"{prefix} [{CliTheme.CursorForeground} on {CliTheme.CursorBackground}]{escaped}[/]"
            : $"{prefix} [grey]{escaped}[/]");
    }

    if (candidates.Count > visibleCount)
    {
        lines.Add($"[dim]showing first {visibleCount}/{candidates.Count} matches[/]");
    }

    lines.Add(string.Empty);
    lines.Add("[dim]Usage: component add <Component Name>[/]");
    return true;
}

static bool TryGetInspectorComponentComposerCandidates(
    string input,
    CliSessionState session,
    out List<(string Label, string? CommitCommand)> candidates)
{
    candidates = [];

    var context = session.Inspector;
    if (context is null || context.Depth != InspectorDepth.ComponentList)
    {
        return false;
    }

    var trimmed = input.TrimStart();
    if (trimmed.StartsWith('/'))
    {
        return false;
    }

    var tokens = TokenizeComposerInput(trimmed);
    if (tokens.Count == 0)
    {
        return false;
    }

    var root = tokens[0].ToLowerInvariant();
    if (root is not ("component" or "comp"))
    {
        return false;
    }

    if (tokens.Count >= 2 && !tokens[1].Equals("add", StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    var query = tokens.Count >= 3
        ? string.Join(' ', tokens.Skip(2))
        : string.Empty;

    var matches = new List<(string DisplayName, double Score)>();
    foreach (var displayName in InspectorComponentCatalog.KnownDisplayNames)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            matches.Add((displayName, 1d));
            continue;
        }

        if (displayName.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            matches.Add((displayName, 0.9d));
            continue;
        }

        if (FuzzyMatcher.TryScore(query, displayName, out var score))
        {
            matches.Add((displayName, score));
        }
    }

    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var match in matches
                 .OrderByDescending(x => x.Score)
                 .ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase))
    {
        if (!seen.Add(match.DisplayName))
        {
            continue;
        }

        candidates.Add((
            $"component add {match.DisplayName}",
            $"component add {match.DisplayName}"));
    }

    return true;
}

static string ResolveProjectMkTypeQuery(IReadOnlyList<string> tokens, bool isMake)
{
    if (!isMake)
    {
        return tokens.Count >= 2 ? tokens[1] : string.Empty;
    }

    for (var i = 1; i < tokens.Count; i++)
    {
        var token = tokens[i];
        if (token.Equals("--type", StringComparison.OrdinalIgnoreCase) || token.Equals("-t", StringComparison.OrdinalIgnoreCase))
        {
            return i + 1 < tokens.Count ? tokens[i + 1] : string.Empty;
        }

        if (token.StartsWith("--type=", StringComparison.OrdinalIgnoreCase))
        {
            return token["--type=".Length..];
        }
    }

    return string.Empty;
}

static bool TryBuildMkUsageHint(
    string input,
    IReadOnlyList<(string Label, string? CommitCommand)> candidates,
    int selectedIndex,
    out string usageHint)
{
    usageHint = string.Empty;
    if (candidates.Count == 0 || selectedIndex < 0 || selectedIndex >= candidates.Count)
    {
        return false;
    }

    var tokens = TokenizeComposerInput(input.TrimStart());
    if (tokens.Count == 0)
    {
        return false;
    }

    var command = tokens[0].ToLowerInvariant();
    var showUsage = command switch
    {
        "mk" => tokens.Count >= 2,
        "make" => HasConcreteMakeType(tokens),
        _ => false
    };
    if (!showUsage)
    {
        return false;
    }

    var label = candidates[selectedIndex].Label;
    if (command == "mk" && label.StartsWith("mk ", StringComparison.OrdinalIgnoreCase))
    {
        var typeDisplay = label[3..].Trim();
        usageHint = $"Usage: mk {typeDisplay} [count] [--name <name>|-n <name>] [--parent <idx|name>]";
        return true;
    }

    if (command == "make" && label.StartsWith("make --type ", StringComparison.OrdinalIgnoreCase))
    {
        var typeDisplay = label["make --type ".Length..].Trim();
        usageHint = $"Usage: make --type {typeDisplay} [--count <count>] [--name <name>|-n <name>] [--parent <idx|name>]";
        return true;
    }

    return false;
}

static bool HasConcreteMakeType(IReadOnlyList<string> tokens)
{
    for (var i = 1; i < tokens.Count; i++)
    {
        var token = tokens[i];
        if (token.StartsWith("--type=", StringComparison.OrdinalIgnoreCase))
        {
            return !string.IsNullOrWhiteSpace(token["--type=".Length..]);
        }

        if (token.Equals("--type", StringComparison.OrdinalIgnoreCase) || token.Equals("-t", StringComparison.OrdinalIgnoreCase))
        {
            return i + 1 < tokens.Count && !tokens[i + 1].StartsWith("-", StringComparison.Ordinal);
        }
    }

    return false;
}

static string FormatMkTypeDisplay(string canonicalType)
{
    if (string.IsNullOrWhiteSpace(canonicalType))
    {
        return canonicalType;
    }

    var builder = new StringBuilder(canonicalType.Length + 4);
    for (var i = 0; i < canonicalType.Length; i++)
    {
        var ch = canonicalType[i];
        var isBoundary = i > 0
                         && char.IsUpper(ch)
                         && (char.IsLower(canonicalType[i - 1]) || char.IsDigit(canonicalType[i - 1]));
        if (isBoundary)
        {
            builder.Append(' ');
        }

        builder.Append(ch);
    }

    return builder.ToString();
}

static List<string> TokenizeComposerInput(string input)
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
        candidates.Add((isSlash ? "/upm install <target>" : "upm install <target>", isSlash ? "/upm install " : "upm install "));
        candidates.Add((isSlash ? "/upm add <target>" : "upm add <target>", isSlash ? "/upm add " : "upm add "));
        candidates.Add((isSlash ? "/upm i <target>" : "upm i <target>", isSlash ? "/upm i " : "upm i "));
        candidates.Add((isSlash ? "/upm remove <id>" : "upm remove <id>", isSlash ? "/upm remove " : "upm remove "));
        candidates.Add((isSlash ? "/upm rm <id>" : "upm rm <id>", isSlash ? "/upm rm " : "upm rm "));
        candidates.Add((isSlash ? "/upm uninstall <id>" : "upm uninstall <id>", isSlash ? "/upm uninstall " : "upm uninstall "));
        candidates.Add((isSlash ? "/upm update [id]" : "upm update [id]", isSlash ? "/upm update " : "upm update "));
        candidates.Add((isSlash ? "/upm u [id]" : "upm u [id]", isSlash ? "/upm u " : "upm u "));
        return true;
    }

    var upmSuggestions = new List<(string Label, string? CommitCommand)>
    {
        (isSlash ? "/upm list [--outdated] [--builtin] [--git]" : "upm list [--outdated] [--builtin] [--git]", isSlash ? "/upm list" : "upm list"),
        (isSlash ? "/upm ls [--outdated] [--builtin] [--git]" : "upm ls [--outdated] [--builtin] [--git]", isSlash ? "/upm ls" : "upm ls"),
        (isSlash ? "/upm install <target>" : "upm install <target>", isSlash ? "/upm install " : "upm install "),
        (isSlash ? "/upm add <target>" : "upm add <target>", isSlash ? "/upm add " : "upm add "),
        (isSlash ? "/upm i <target>" : "upm i <target>", isSlash ? "/upm i " : "upm i "),
        (isSlash ? "/upm remove <id>" : "upm remove <id>", isSlash ? "/upm remove " : "upm remove "),
        (isSlash ? "/upm rm <id>" : "upm rm <id>", isSlash ? "/upm rm " : "upm rm "),
        (isSlash ? "/upm uninstall <id>" : "upm uninstall <id>", isSlash ? "/upm uninstall " : "upm uninstall "),
        (isSlash ? "/upm update [id]" : "upm update [id]", isSlash ? "/upm update " : "upm update "),
        (isSlash ? "/upm u [id]" : "upm u [id]", isSlash ? "/upm u " : "upm u ")
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
    else if (suffixLower.StartsWith("install", StringComparison.OrdinalIgnoreCase)
             || suffixLower.StartsWith("add", StringComparison.OrdinalIgnoreCase)
             || suffixLower.Equals("i", StringComparison.OrdinalIgnoreCase)
             || suffixLower.StartsWith("i ", StringComparison.OrdinalIgnoreCase))
    {
        var commandHead = isSlash
            ? (suffixLower.StartsWith("add", StringComparison.OrdinalIgnoreCase)
                ? "/upm add"
                : (suffixLower.StartsWith("install", StringComparison.OrdinalIgnoreCase)
                    ? "/upm install"
                    : "/upm i"))
            : (suffixLower.StartsWith("add", StringComparison.OrdinalIgnoreCase)
                ? "upm add"
                : (suffixLower.StartsWith("install", StringComparison.OrdinalIgnoreCase)
                    ? "upm install"
                    : "upm i"));

        candidates.Add(($"{commandHead} com.unity.addressables", $"{commandHead} com.unity.addressables"));
        candidates.Add(($"{commandHead} https://github.com/user/repo.git?path=/subfolder#v1.0.0", $"{commandHead} https://github.com/user/repo.git?path=/subfolder#v1.0.0"));
        candidates.Add(($"{commandHead} file:../local-pkg", $"{commandHead} file:../local-pkg"));
    }
    else if (suffixLower.StartsWith("remove", StringComparison.OrdinalIgnoreCase)
             || suffixLower.StartsWith("rm", StringComparison.OrdinalIgnoreCase)
             || suffixLower.StartsWith("uninstall", StringComparison.OrdinalIgnoreCase))
    {
        var commandHead = isSlash
            ? (suffixLower.StartsWith("rm", StringComparison.OrdinalIgnoreCase)
                ? "/upm rm"
                : (suffixLower.StartsWith("uninstall", StringComparison.OrdinalIgnoreCase) ? "/upm uninstall" : "/upm remove"))
            : (suffixLower.StartsWith("rm", StringComparison.OrdinalIgnoreCase)
                ? "upm rm"
                : (suffixLower.StartsWith("uninstall", StringComparison.OrdinalIgnoreCase) ? "upm uninstall" : "upm remove"));
        candidates.Add(($"{commandHead} com.unity.addressables", $"{commandHead} com.unity.addressables"));
    }
    else if (suffixLower.StartsWith("update", StringComparison.OrdinalIgnoreCase)
             || suffixLower.Equals("u", StringComparison.OrdinalIgnoreCase)
             || suffixLower.StartsWith("u ", StringComparison.OrdinalIgnoreCase))
    {
        var commandHead = isSlash
            ? (suffixLower.StartsWith("update", StringComparison.OrdinalIgnoreCase) ? "/upm update" : "/upm u")
            : (suffixLower.StartsWith("update", StringComparison.OrdinalIgnoreCase) ? "upm update" : "upm u");
        candidates.Add(($"{commandHead}", $"{commandHead}"));
        candidates.Add(($"{commandHead} com.unity.addressables", $"{commandHead} com.unity.addressables"));
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

    if (session.Mode != CliMode.Project || string.IsNullOrWhiteSpace(session.CurrentProjectPath))
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

static bool TryParseExecLaunchOptions(string[] args, out ExecLaunchOptions? options, out string? error)
{
    options = null;
    error = null;
    if (args.Length == 0)
    {
        return false;
    }

    var hasAgenticFlag = args.Any(arg => arg.Equals("--agentic", StringComparison.OrdinalIgnoreCase));
    if (!args[0].Equals("exec", StringComparison.OrdinalIgnoreCase))
    {
        if (hasAgenticFlag)
        {
            error = "--agentic is supported with 'exec' only. Use: unifocl exec \"<command>\" --agentic";
            return true;
        }

        return false;
    }

    var agentic = false;
    var format = AgenticOutputFormat.Json;
    string? projectPath = null;
    CliContextMode? contextMode = null;
    int? attachPort = null;
    string? requestId = null;
    var commandTokens = new List<string>();

    for (var i = 1; i < args.Length; i++)
    {
        var token = args[i];
        if (token.Equals("--agentic", StringComparison.OrdinalIgnoreCase))
        {
            agentic = true;
            continue;
        }

        if (token.Equals("--format", StringComparison.OrdinalIgnoreCase))
        {
            if (i + 1 >= args.Length)
            {
                error = "missing value for --format (json|yaml)";
                return true;
            }

            var raw = args[++i].Trim().ToLowerInvariant();
            if (raw == "json")
            {
                format = AgenticOutputFormat.Json;
            }
            else if (raw == "yaml")
            {
                format = AgenticOutputFormat.Yaml;
            }
            else
            {
                error = "invalid --format value (use json|yaml)";
                return true;
            }
            continue;
        }

        if (token.Equals("--project", StringComparison.OrdinalIgnoreCase))
        {
            if (i + 1 >= args.Length)
            {
                error = "missing value for --project";
                return true;
            }

            projectPath = args[++i];
            continue;
        }

        if (token.Equals("--mode", StringComparison.OrdinalIgnoreCase))
        {
            if (i + 1 >= args.Length)
            {
                error = "missing value for --mode";
                return true;
            }

            var rawMode = args[++i].Trim().ToLowerInvariant();
            contextMode = rawMode switch
            {
                "project" => CliContextMode.Project,
                "hierarchy" => CliContextMode.Hierarchy,
                "inspector" => CliContextMode.Inspector,
                _ => CliContextMode.None
            };
            if (contextMode == CliContextMode.None)
            {
                error = "invalid --mode value (use project|hierarchy|inspector)";
                return true;
            }
            continue;
        }

        if (token.Equals("--attach-port", StringComparison.OrdinalIgnoreCase))
        {
            if (i + 1 >= args.Length || !int.TryParse(args[++i], out var parsedPort) || parsedPort is < 1 or > 65535)
            {
                error = "invalid --attach-port value";
                return true;
            }

            attachPort = parsedPort;
            continue;
        }

        if (token.Equals("--request-id", StringComparison.OrdinalIgnoreCase))
        {
            if (i + 1 >= args.Length)
            {
                error = "missing value for --request-id";
                return true;
            }

            requestId = args[++i];
            continue;
        }

        commandTokens.Add(token);
    }

    if (commandTokens.Count == 0)
    {
        error = "exec requires a command text";
        return true;
    }

    options = new ExecLaunchOptions(
        string.Join(' ', commandTokens),
        agentic,
        format,
        projectPath,
        contextMode,
        attachPort,
        requestId);
    return true;
}

static async Task<AgenticResponseEnvelope> ExecuteOneShotCommandAsync(
    ExecLaunchOptions options,
    List<CommandSpec> commands,
    List<string> streamLog,
    CliSessionState session,
    DaemonControlService daemonControlService,
    DaemonRuntime daemonRuntime,
    ProjectLifecycleService projectLifecycleService,
    ProjectCommandRouterService projectCommandRouterService,
    HierarchyTui hierarchyTui,
    BuildCommandService buildCommandService)
{
    var requestId = string.IsNullOrWhiteSpace(options.RequestId) ? Guid.NewGuid().ToString("N") : options.RequestId!;
    var extraMeta = new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["command"] = options.CommandText
    };

    if (!string.IsNullOrWhiteSpace(options.ProjectPath))
    {
        session.CurrentProjectPath = Path.GetFullPath(options.ProjectPath!);
        session.Mode = CliMode.Project;
        session.ContextMode = options.ContextMode ?? CliContextMode.Project;
    }

    if (options.AttachPort is not null)
    {
        session.AttachedPort = options.AttachPort.Value;
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
    try
    {
        var input = options.CommandText.Trim();
        if (string.IsNullOrWhiteSpace(input))
        {
            errors.Add(new AgenticError("E_PARSE", "empty command text", "pass a command after exec"));
        }
        else if (input.StartsWith("/dump", StringComparison.OrdinalIgnoreCase))
        {
            var dump = await ExecuteDumpCommandAsync(input, session);
            if (!dump.Ok)
            {
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
            await ExecuteCommandForOneShotAsync(
                input,
                commands,
                streamLog,
                session,
                daemonControlService,
                daemonRuntime,
                projectLifecycleService,
                projectCommandRouterService,
                hierarchyTui,
                buildCommandService);
            var parsed = ParseAgenticIssuesFromLogs(streamLog);
            errors.AddRange(parsed.Errors);
            warnings.AddRange(parsed.Warnings);
            data = new Dictionary<string, object?>
            {
                ["logs"] = streamLog.Select(AgenticFormatter.StripMarkup).Where(line => !string.IsNullOrWhiteSpace(line)).ToList()
            };
        }
    }
    catch (Exception ex)
    {
        errors.Add(new AgenticError("E_INTERNAL", $"unhandled execution exception: {ex.GetType().Name}: {ex.Message}"));
    }

    var exitCode = ResolveExitCode(errors);
    var modeName = session.ContextMode switch
    {
        CliContextMode.Project => "project",
        CliContextMode.Hierarchy => "hierarchy",
        CliContextMode.Inspector => "inspector",
        _ => "none"
    };
    var action = ExtractActionLabel(options.CommandText);

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
            extraMeta));
}

static async Task ExecuteCommandForOneShotAsync(
    string input,
    List<CommandSpec> commands,
    List<string> streamLog,
    CliSessionState session,
    DaemonControlService daemonControlService,
    DaemonRuntime daemonRuntime,
    ProjectLifecycleService projectLifecycleService,
    ProjectCommandRouterService projectCommandRouterService,
    HierarchyTui hierarchyTui,
    BuildCommandService buildCommandService)
{
    if (input.Equals(":focus-recent", StringComparison.OrdinalIgnoreCase))
    {
        await projectLifecycleService.TryHandleRecentSelectionToggleAsync(
            session,
            daemonControlService,
            daemonRuntime,
            line => AppendLog(streamLog, line));
        return;
    }

    if (!input.StartsWith('/'))
    {
        if (TryNormalizeProjectBuildCommand(input, out var normalizedBuildInput))
        {
            await buildCommandService.HandleBuildCommandAsync(
                normalizedBuildInput,
                session,
                daemonControlService,
                daemonRuntime,
                line => AppendLog(streamLog, line));
            return;
        }

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

        return;
    }

    if (input == "/")
    {
        return;
    }

    input = NormalizeSlashCommand(input);
    var matched = MatchCommand(input, commands);
    if (matched is null)
    {
        AppendLog(streamLog, $"[grey]system[/]: unknown command [white]{Markup.Escape(input)}[/]");
        return;
    }

    if (matched.Trigger == "/quit")
    {
        AppendLog(streamLog, "[grey]Session closed.[/]");
        return;
    }

    if (matched.Trigger == "/clear")
    {
        streamLog.Clear();
        return;
    }

    if (matched.Trigger == "/dump")
    {
        await HandleDumpCommandAsync(input, session, streamLog);
        return;
    }

    if (matched.Trigger == "/project")
    {
        if (session.Mode != CliMode.Project || string.IsNullOrWhiteSpace(session.CurrentProjectPath))
        {
            AppendLog(streamLog, "[yellow]mode[/]: open a project first with /open");
            return;
        }

        session.ContextMode = CliContextMode.Project;
        session.Inspector = null;
        await projectCommandRouterService.TryHandleProjectCommandAsync(
            string.Empty,
            session,
            daemonControlService,
            daemonRuntime,
            line => AppendLog(streamLog, line));
        return;
    }

    if (matched.Trigger == "/inspect")
    {
        if (session.Mode != CliMode.Project || string.IsNullOrWhiteSpace(session.CurrentProjectPath))
        {
            AppendLog(streamLog, "[yellow]mode[/]: open a project first with /open");
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
            line => AppendLog(streamLog, line));
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
            AppendLog(streamLog, "[yellow]mode[/]: open a project first with /open");
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
            line => AppendLog(streamLog, line));
        if (!handled)
        {
            AppendLog(streamLog, "[yellow]upm[/]: unsupported /upm command");
        }

        return;
    }

    if (matched.Trigger.StartsWith("/build", StringComparison.Ordinal))
    {
        await buildCommandService.HandleBuildCommandAsync(
            input,
            session,
            daemonControlService,
            daemonRuntime,
            line => AppendLog(streamLog, line));
        return;
    }

    if (matched.Trigger is "/version")
    {
        var processPath = Environment.ProcessPath ?? AppContext.BaseDirectory;
        AppendLog(streamLog, $"[grey]version[/]: cli [white]{Markup.Escape(CliVersion.SemVer)}[/], protocol [white]{Markup.Escape(CliVersion.Protocol)}[/]");
        AppendLog(streamLog, $"[grey]binary[/]: [white]{Markup.Escape(processPath)}[/]");
        return;
    }

    if (matched.Trigger is "/protocol")
    {
        AppendLog(streamLog, $"[grey]protocol[/]: [white]{Markup.Escape(CliVersion.Protocol)}[/]");
        AppendLog(streamLog, "[grey]agentic[/]: schema v1, formats=json|yaml, endpoints=/agent/exec,/agent/capabilities,/agent/status,/agent/dump/{hierarchy|project|inspector}");
        return;
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
        if (matched.Trigger == "/daemon attach")
        {
            await buildCommandService.NotifyAttachedBuildIfAnyAsync(
                session,
                line => AppendLog(streamLog, line));
        }

        return;
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

        return;
    }

    AppendLog(streamLog, $"[yellow]command[/]: not implemented yet -> {Markup.Escape(matched.Signature)}");
    AppendLog(streamLog, "[grey]hint[/]: run /help for implemented commands and mode-specific workflows");
}

static async Task HandleDumpCommandAsync(string input, CliSessionState session, List<string> streamLog)
{
    var result = await ExecuteDumpCommandAsync(input, session);
    if (!result.Ok)
    {
        AppendLog(streamLog, $"[red]dump[/]: {Markup.Escape(result.Error?.Message ?? "dump failed")}");
        return;
    }

    AppendLog(streamLog, $"[grey]dump[/]: emitted {result.Category} ({result.Format.ToString().ToLowerInvariant()})");
    if (CliRuntimeState.SuppressConsoleOutput)
    {
        return;
    }

    Console.WriteLine(result.PayloadText);
}

static async Task<(bool Ok, AgenticOutputFormat Format, string Category, object? PayloadData, string PayloadText, AgenticError? Error)> ExecuteDumpCommandAsync(string input, CliSessionState session)
{
    var tokens = TokenizeComposerInput(input);
    if (tokens.Count < 2)
    {
        return (false, AgenticOutputFormat.Json, string.Empty, null, string.Empty, new AgenticError("E_PARSE", "usage: /dump <hierarchy|project|inspector> [--format json|yaml] [--compact] [--depth n] [--limit n]"));
    }

    var category = tokens[1].Trim().ToLowerInvariant();
    var format = AgenticOutputFormat.Json;
    var depth = 6;
    var limit = 1000;
    for (var i = 2; i < tokens.Count; i++)
    {
        var token = tokens[i];
        if (token.Equals("--compact", StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        if (token.Equals("--format", StringComparison.OrdinalIgnoreCase) && i + 1 < tokens.Count)
        {
            var raw = tokens[++i].Trim().ToLowerInvariant();
            if (raw == "json")
            {
                format = AgenticOutputFormat.Json;
            }
            else if (raw == "yaml")
            {
                format = AgenticOutputFormat.Yaml;
            }
            else
            {
                return (false, AgenticOutputFormat.Json, string.Empty, null, string.Empty, new AgenticError("E_PARSE", "invalid --format value for /dump (use json|yaml)"));
            }
            continue;
        }

        if (token.Equals("--depth", StringComparison.OrdinalIgnoreCase) && i + 1 < tokens.Count)
        {
            _ = int.TryParse(tokens[++i], out depth);
            depth = Math.Clamp(depth, 1, 20);
            continue;
        }

        if (token.Equals("--limit", StringComparison.OrdinalIgnoreCase) && i + 1 < tokens.Count)
        {
            _ = int.TryParse(tokens[++i], out limit);
            limit = Math.Clamp(limit, 1, 20000);
            continue;
        }
    }

    var recognizedCategory = category is "hierarchy" or "project" or "inspector";
    if (!recognizedCategory)
    {
        return (false, format, category, null, string.Empty, new AgenticError("E_VALIDATION", $"unsupported dump category: {category}", "supported: hierarchy, project, inspector"));
    }

    object? data = category switch
    {
        "hierarchy" => await BuildHierarchyDumpAsync(session),
        "project" => BuildProjectDump(session, depth, limit),
        "inspector" => await BuildInspectorDumpAsync(session),
        _ => null
    };

    if (data is null)
    {
        var hint = category switch
        {
            "project" => "set --project or open a project first",
            "hierarchy" => "attach daemon with --attach-port to fetch hierarchy snapshot",
            "inspector" => "attach daemon and enter inspector context before dumping inspector",
            _ => string.Empty
        };
        return (false, format, category, null, string.Empty, new AgenticError("E_MODE_INVALID", $"dump {category} is unavailable in current session", hint));
    }

    var payload = format == AgenticOutputFormat.Yaml
        ? AgenticFormatter.SerializeYaml(data)
        : JsonSerializer.Serialize(data, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
    return (true, format, category, data, payload, null);
}

static async Task<object?> BuildHierarchyDumpAsync(CliSessionState session)
{
    if (session.AttachedPort is null)
    {
        return null;
    }

    var client = new HierarchyDaemonClient();
    var snapshot = await client.GetSnapshotAsync(session.AttachedPort.Value);
    return snapshot;
}

static object? BuildProjectDump(CliSessionState session, int depth, int limit)
{
    if (string.IsNullOrWhiteSpace(session.CurrentProjectPath))
    {
        return null;
    }

    var root = Path.Combine(session.CurrentProjectPath!, "Assets");
    if (!Directory.Exists(root))
    {
        return new { root = "Assets", entries = Array.Empty<object>() };
    }

    var entries = new List<object>();
    var count = 0;
    var stack = new Stack<(string AbsolutePath, string RelativePath, int Depth)>();
    stack.Push((root, "Assets", 0));

    while (stack.Count > 0 && count < limit)
    {
        var current = stack.Pop();
        if (current.Depth > depth)
        {
            continue;
        }

        var directories = Directory.GetDirectories(current.AbsolutePath)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var files = Directory.GetFiles(current.AbsolutePath)
            .Where(path => !path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var file in files)
        {
            if (count >= limit)
            {
                break;
            }

            var rel = CombineDumpRelative(current.RelativePath, Path.GetFileName(file));
            entries.Add(new { path = rel, kind = "file" });
            count++;
        }

        for (var i = directories.Count - 1; i >= 0; i--)
        {
            if (count >= limit)
            {
                break;
            }

            var dir = directories[i];
            var rel = CombineDumpRelative(current.RelativePath, Path.GetFileName(dir));
            entries.Add(new { path = rel, kind = "directory" });
            count++;
            stack.Push((dir, rel, current.Depth + 1));
        }
    }

    return new { root = "Assets", entries };
}

static string CombineDumpRelative(string basePath, string name)
{
    var normalizedBase = basePath.Replace('\\', '/').TrimEnd('/');
    var normalizedName = name.Replace('\\', '/').Trim('/');
    if (string.IsNullOrWhiteSpace(normalizedBase))
    {
        return normalizedName;
    }

    return string.IsNullOrWhiteSpace(normalizedName)
        ? normalizedBase
        : $"{normalizedBase}/{normalizedName}";
}

static async Task<object?> BuildInspectorDumpAsync(CliSessionState session)
{
    if (session.AttachedPort is null)
    {
        return null;
    }

    var targetPath = session.Inspector?.PromptPath ?? "/";
    using var http = new HttpClient();
    var listPayload = JsonSerializer.Serialize(new
    {
        action = "list-components",
        targetPath,
        componentIndex = -1,
        componentName = "",
        fieldName = "",
        value = "",
        query = ""
    });
    using var listResponse = await http.PostAsync(
        $"http://127.0.0.1:{session.AttachedPort.Value}/inspect",
        new StringContent(listPayload, Encoding.UTF8, "application/json"));
    if (!listResponse.IsSuccessStatusCode)
    {
        return null;
    }

    var listBody = await listResponse.Content.ReadAsStringAsync();
    using var listDoc = JsonDocument.Parse(listBody);
    if (!listDoc.RootElement.TryGetProperty("components", out var components))
    {
        return new { targetPath, components = Array.Empty<object>() };
    }

    var expanded = new List<object>();
    foreach (var component in components.EnumerateArray())
    {
        var index = component.TryGetProperty("index", out var indexProp) ? indexProp.GetInt32() : -1;
        var name = component.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? string.Empty : string.Empty;
        var fieldsPayload = JsonSerializer.Serialize(new
        {
            action = "list-fields",
            targetPath,
            componentIndex = index,
            componentName = name,
            fieldName = "",
            value = "",
            query = ""
        });
        using var fieldResponse = await http.PostAsync(
            $"http://127.0.0.1:{session.AttachedPort.Value}/inspect",
            new StringContent(fieldsPayload, Encoding.UTF8, "application/json"));
        var fields = Array.Empty<object>();
        if (fieldResponse.IsSuccessStatusCode)
        {
            var fieldsBody = await fieldResponse.Content.ReadAsStringAsync();
            using var fieldsDoc = JsonDocument.Parse(fieldsBody);
            if (fieldsDoc.RootElement.TryGetProperty("fields", out var fieldsElement))
            {
                fields = fieldsElement.EnumerateArray().Select(field => new
                {
                    name = field.TryGetProperty("name", out var n) ? n.GetString() : null,
                    value = field.TryGetProperty("value", out var v) ? v.GetString() : null,
                    type = field.TryGetProperty("type", out var t) ? t.GetString() : null,
                    isBoolean = field.TryGetProperty("isBoolean", out var b) && b.GetBoolean()
                }).Cast<object>().ToArray();
            }
        }

        expanded.Add(new
        {
            index,
            name,
            enabled = component.TryGetProperty("enabled", out var enabledProp) && enabledProp.GetBoolean(),
            fields
        });
    }

    return new
    {
        targetPath,
        components = expanded
    };
}

static (List<AgenticError> Errors, List<AgenticWarning> Warnings) ParseAgenticIssuesFromLogs(List<string> streamLog)
{
    var errors = new List<AgenticError>();
    var warnings = new List<AgenticWarning>();
    foreach (var raw in streamLog)
    {
        var line = AgenticFormatter.StripMarkup(raw);
        if (string.IsNullOrWhiteSpace(line))
        {
            continue;
        }

        var lower = line.ToLowerInvariant();
        if (lower.StartsWith("error") || lower.Contains("failed") || lower.StartsWith("x "))
        {
            errors.Add(new AgenticError(GuessErrorCode(lower), line));
            continue;
        }

        if (lower.StartsWith("warning") || lower.StartsWith("note") || lower.Contains("yellow"))
        {
            warnings.Add(new AgenticWarning("W_GENERIC", line));
        }
    }

    return (errors, warnings);
}

static string GuessErrorCode(string normalizedLine)
{
    if (normalizedLine.Contains("usage") || normalizedLine.Contains("invalid"))
    {
        return "E_PARSE";
    }

    if (normalizedLine.Contains("open a project first") || normalizedLine.Contains("mode"))
    {
        return "E_MODE_INVALID";
    }

    if (normalizedLine.Contains("not found"))
    {
        return "E_NOT_FOUND";
    }

    if (normalizedLine.Contains("timeout") || normalizedLine.Contains("unreachable"))
    {
        return "E_TIMEOUT";
    }

    if (normalizedLine.Contains("daemon"))
    {
        return "E_UNITY_API";
    }

    return "E_VALIDATION";
}

static int ResolveExitCode(List<AgenticError> errors)
{
    if (errors.Count == 0)
    {
        return 0;
    }

    if (errors.Any(error => error.Code is "E_PARSE" or "E_VALIDATION" or "E_MODE_INVALID" or "E_NOT_FOUND"))
    {
        return 2;
    }

    if (errors.Any(error => error.Code is "E_TIMEOUT" or "E_UNITY_API"))
    {
        return 3;
    }

    return 4;
}

static string ExtractActionLabel(string commandText)
{
    var tokens = TokenizeComposerInput(commandText);
    if (tokens.Count == 0)
    {
        return "unknown";
    }

    return tokens[0].TrimStart('/').ToLowerInvariant();
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
        "/b" => "/build run",
        "/bx" => "/build exec",
        "/ba" => "/build addressables",
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

static bool TryNormalizeProjectBuildCommand(string input, out string normalizedBuildInput)
{
    normalizedBuildInput = string.Empty;
    var trimmed = input.Trim();
    if (string.IsNullOrWhiteSpace(trimmed))
    {
        return false;
    }

    var commandEnd = trimmed.IndexOf(' ');
    var head = commandEnd >= 0 ? trimmed[..commandEnd] : trimmed;
    var tail = commandEnd >= 0 ? trimmed[commandEnd..] : string.Empty;
    var normalizedHead = head.ToLowerInvariant() switch
    {
        "build" => "/build",
        "b" => "/build run",
        "bx" => "/build exec",
        "ba" => "/build addressables",
        _ => string.Empty
    };

    if (string.IsNullOrWhiteSpace(normalizedHead))
    {
        return false;
    }

    normalizedBuildInput = normalizedHead + tail;
    return true;
}

static void AppendLog(List<string> streamLog, string line)
{
    streamLog.Add(line);
    if (!CliRuntimeState.SuppressConsoleOutput)
    {
        CliTheme.MarkupLine(line);
    }
}

static void LogUnhandledException(List<string> streamLog, Exception ex, string phase)
{
    var typeName = ex.GetType().Name;
    AppendLog(streamLog, $"[red]error[/]: unhandled {Markup.Escape(phase)} exception <{Markup.Escape(typeName)}>");
    AppendLog(streamLog, $"[red]error[/]: {Markup.Escape(ex.Message)}");
    AppendLog(streamLog, "[yellow]system[/]: recovered and continuing session");
}
