internal static class CliCommandCatalog
{
    public static List<CommandSpec> CreateRootCommands()
    {
        return
        [
            // System & lifecycle
            new("/open <path> [--allow-unsafe]", "Open project (starts/attaches daemon, loads project)", "/open"),
            new("/o <path>", "Alias for /open", "/o"),
            new("/close", "Detach from current project and stop attached daemon", "/close"),
            new("/c", "Alias for /close", "/c"),
            new("/quit", "Exit CLI client only (daemon keeps running)", "/quit"),
            new("/q", "Alias for /quit", "/q"),
            new("/daemon <start|stop|restart|ps|attach|detach>", "Manage daemon lifecycle", "/daemon"),
            new("/d <start|stop|restart|ps|attach|detach>", "Alias for /daemon", "/d"),
            new("/config <get|set|list|reset> [theme|recent.staleDays]", "Manage CLI preferences (theme, recent prune stale days)", "/config"),
            new("/cfg <get|set|list|reset> [theme|recent.staleDays]", "Alias for /config", "/cfg"),
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
            new("/recent [idx|prune] [--allow-unsafe] [--prune]", "List recent projects, open by index, or prune missing/stale entries", "/recent"),
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
            new("/upm update <id> [version]", "Update package to latest or specified version", "/upm update"),
            new("/upm u <id> [version]", "Alias for /upm update", "/upm u"),
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
        ];
    }

    public static List<CommandSpec> CreateProjectCommands()
    {
        return
        [
            new("list", "List entries in active mode", "list"),
            new("ls", "Alias for list", "ls"),
            new("enter <idx>", "Enter selected node/folder/component", "enter"),
            new("cd <idx>", "Alias for enter", "cd"),
            new("up", "Navigate up one level in active mode", "up"),
            new("..", "Alias for up", ".."),
            new("make --type <type> [--count <count>] [--name <name>] [--parent <idx|name>]", "Create typed asset(s) in project mode", "make"),
            new("mk <type> [count] [--name <name>|-n <name>] [--parent <idx|name>]", "Alias for make", "mk"),
            new("load <idx|name>", "Load/open scene, prefab, or script in project mode", "load"),
            new("remove <idx>", "Remove selected item in active mode", "remove"),
            new("rm <idx>", "Alias for remove", "rm"),
            new("rename <idx> <new-name>", "Rename selected item (mode dependent)", "rename"),
            new("rn <idx> <new-name>", "Alias for rename", "rn"),
            new("set <field> <value...>", "Set field/property in active mode", "set"),
            new("s <field> <value...>", "Alias for set", "s"),
            new("toggle <target>", "Toggle bool/active/enabled in active mode", "toggle"),
            new("t <target>", "Alias for toggle", "t"),
            new("f [--type <type>|t:<type>] <query>", "Fuzzy find in active mode", "f"),
            new("ff [--type <type>|t:<type>] <query>", "Alias for fuzzy find", "ff"),
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
            new("upm update <id> [version]", "Update package to latest or specified version", "upm update"),
            new("upm u <id> [version]", "Alias for upm update", "upm u"),
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
        ];
    }

    public static List<CommandSpec> CreateInspectorCommands()
    {
        return
        [
            new("inspect [idx|path]", "Enter inspector root target (default: current focus path)", "inspect"),
            new("list", "Refresh and list inspector items at current depth", "list"),
            new("ls", "Alias for list", "ls"),
            new("enter <idx>", "Inspect component by index", "enter"),
            new("cd <idx>", "Alias for enter", "cd"),
            new("up", "Step up (fields -> components, components -> project)", "up"),
            new("..", "Alias for up", ".."),
            new(":i", "Alias for up", ":i"),
            new("set <field> <value...>", "Set selected component field in inspector", "set"),
            new("set <field> --search <query> [--scene|--project]", "Search and list ObjectReference candidates for indexed assignment", "set"),
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
        ];
    }
}
