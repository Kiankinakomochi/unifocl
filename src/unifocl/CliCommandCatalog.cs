internal static class CliCommandCatalog
{
    public static List<CommandSpec> CreateRootCommands()
    {
        return
        [
            // ── core ──────────────────────────────────────────────────────
            new("/open <path> [--allow-unsafe] [--timeout <seconds>]", "Open project (starts/attaches daemon, loads project)", "/open"),
            new("/o <path>", "Alias for /open", "/o"),
            new("/close", "Detach from current project and stop attached daemon", "/close"),
            new("/c", "Alias for /close", "/c"),
            new("/quit", "Exit CLI client only (daemon keeps running)", "/quit"),
            new("/q", "Alias for /quit", "/q"),
            new("/daemon <stop|ps|attach|detach>", "Manage daemon lifecycle (startup is handled by /open)", "/daemon"),
            new("/d <stop|ps|attach|detach>", "Alias for /daemon", "/d"),
            new("/daemon stop", "Stop daemon", "/daemon stop"),
            new("/daemon ps", "Show instances, ports, uptime, project", "/daemon ps"),
            new("/daemon attach <port>", "Attach CLI to existing daemon", "/daemon attach"),
            new("/daemon detach", "Detach CLI and keep daemon alive", "/daemon detach"),
            new("/config <get|set|list|reset> [theme|recent.staleDays]", "Manage CLI preferences (theme, recent prune stale days)", "/config"),
            new("/cfg <get|set|list|reset> [theme|recent.staleDays]", "Alias for /config", "/cfg"),
            new("/status", "Show daemon/mode/editor/project/session status", "/status"),
            new("/st", "Alias for /status", "/st"),
            new("/help [topic]", "Show help by topic", "/help"),
            new("/?", "Alias for /help", "/?"),
            new("/project", "Switch contextual command router to Project mode", "/project"),
            new("/p", "Alias for /project", "/p"),
            new("/hierarchy", "Switch to Hierarchy mode (interactive TUI)", "/hierarchy"),
            new("/h", "Alias for /hierarchy", "/h"),
            new("/inspect <idx|path>", "Switch to Inspector mode and focus target", "/inspect"),
            new("/i <idx|path>", "Alias for /inspect", "/i"),
            new("/dump <hierarchy|project|inspector> [--format json|yaml] [--compact] [--depth n] [--limit n]", "Dump deterministic mode state for agentic workflows", "/dump"),
            new("/mutate (--dry-run) (--continue-on-error) <json-array>", "Batch scene mutations from a JSON op array. Context (hierarchy/inspector) is inferred per-op — no mode switching needed. Ops: create|rename|remove|move|toggle_active|add_component|remove_component|set_field|toggle_field|toggle_component", "/mutate"),
            new("/version", "Show CLI and protocol version", "/version"),
            new("/protocol", "Show supported JSON schema capabilities", "/protocol"),
            new("/clear", "Clear and redraw boot screen", "/clear"),

            // ── setup ─────────────────────────────────────────────────────
            new("/new <project-name> [unity-version] [--allow-unsafe]", "Bootstrap a new Unity project", "/new", "setup"),
            new("/clone <git-url> [--allow-unsafe]", "Clone repo and set local CLI bridge config", "/clone", "setup"),
            new("/recent [idx|prune] [--allow-unsafe] [--prune]", "List recent projects, open by index, or prune missing/stale entries", "/recent", "setup"),
            new("/init [path-to-project]", "Generate local bridge config and install editor-side CLI bridge dependencies", "/init", "setup"),
            new("/doctor", "Run diagnostics for environment and tooling", "/doctor", "setup"),
            new("/scan [--root <dir>] [--depth n]", "Find Unity projects under a directory", "/scan", "setup"),
            new("/info <path>", "Read project metadata (Unity version/name/paths)", "/info", "setup"),
            new("/unity detect", "List installed Unity editors", "/unity detect", "setup"),
            new("/unity set <path>", "Set default Unity editor path", "/unity set", "setup"),
            new("/install-hook", "Install/validate Bridge mode integration", "/install-hook", "setup"),
            new("/agent install <codex|claude> [--workspace <path>] [--server-name <name>] [--config-root <path>] [--dry-run]", "Install/update agent MCP integration for Codex or Claude", "/agent install", "setup"),
            new("/examples", "Show common next-step flows", "/examples", "setup"),
            new("/keybinds", "Show modal keybinds/shortcuts", "/keybinds", "setup"),
            new("/shortcuts", "Alias for keybinds", "/shortcuts", "setup"),
            new("/update", "Download/install latest CLI binary for current platform", "/update", "setup"),
            new("/logs [daemon|unity] [-f]", "Tail or follow daemon/unity logs", "/logs", "setup"),

            // ── build ─────────────────────────────────────────────────────
            new("/build <run|exec|scenes|addressables|cancel|targets>", "Build pipeline commands", "/build", "build"),
            new("/build run [target] [--dev] [--debug] [--clean] [--path <output-path>]", "Run Unity build for target (prompts when omitted)", "/build run", "build"),
            new("/build exec <Method>", "Execute static build method (e.g., CI.Builder.BuildAndroidProd)", "/build exec", "build"),
            new("/build scenes", "Open interactive scene build-settings TUI", "/build scenes", "build"),
            new("/build addressables [--clean] [--update]", "Build Addressables content", "/build addressables", "build"),
            new("/build cancel", "Request cancellation of an ongoing build", "/build cancel", "build"),
            new("/build targets", "List installed Unity build support targets", "/build targets", "build"),
            new("/build logs", "Open restartable build log tail viewer", "/build logs", "build"),
            new("/b [target] [--dev] [--debug] [--clean] [--path <output-path>]", "Alias for /build run", "/b", "build"),
            new("/bx <Method>", "Alias for /build exec", "/bx", "build"),
            new("/ba [--clean] [--update]", "Alias for /build addressables", "/ba", "build"),
            new("/build snapshot-packages", "Snapshot current package manifest to .unifocl-runtime/snapshots/", "/build snapshot-packages", "build"),
            new("/build preflight", "Run preflight validation (scene-list, build-settings, packages)", "/build preflight", "build"),
            new("/build artifact-metadata", "Read last build artifact metadata from cached build report", "/build artifact-metadata", "build"),
            new("/build failure-classify", "Classify failures from last build report", "/build failure-classify", "build"),
            new("/build report", "Full build report: preflight + artifacts + failure classification", "/build report", "build"),
            new("/build scenes set <json-array>", "Set the build scene list programmatically from a JSON array of paths", "/build scenes set", "build"),

            // ── validate ──────────────────────────────────────────────────
            new("/validate <scene-list|missing-scripts|packages|build-settings|asmdef|asset-refs|addressables|scripts|all>", "Run project validation checks", "/validate", "validate"),
            new("/validate scene-list", "Check that all build-settings scene paths exist", "/validate scene-list", "validate"),
            new("/validate missing-scripts", "Scan scenes and prefabs for missing MonoBehaviour scripts", "/validate missing-scripts", "validate"),
            new("/validate packages", "Validate manifest.json vs packages-lock.json consistency", "/validate packages", "validate"),
            new("/validate build-settings", "Check PlayerSettings/build configuration sanity", "/validate build-settings", "validate"),
            new("/validate asmdef", "Validate .asmdef files for duplicates, undefined refs, and cycles", "/validate asmdef", "validate"),
            new("/validate asset-refs", "Scan asset files for broken GUID references", "/validate asset-refs", "validate"),
            new("/validate addressables", "Validate Addressables configuration if installed", "/validate addressables", "validate"),
            new("/validate scripts", "Offline Roslyn compile check for project C# scripts (no running editor required)", "/validate scripts", "validate"),
            new("/validate all", "Run all validators", "/validate all", "validate"),
            new("/val <subcommand>", "Alias for /validate", "/val", "validate"),

            // ── diag ──────────────────────────────────────────────────────
            new("/diag <script-defines|compile-errors|assembly-graph|scene-deps|prefab-deps|asset-size|import-hotspots|all>", "Run project diagnostics", "/diag", "diag"),
            new("/diag script-defines", "Show scripting define symbols per build target group", "/diag script-defines", "diag"),
            new("/diag compile-errors", "Show compiler messages from last compilation pass", "/diag compile-errors", "diag"),
            new("/diag assembly-graph", "Show assembly dependency graph (asmdef references)", "/diag assembly-graph", "diag"),
            new("/diag scene-deps", "Show asset dependencies per enabled scene", "/diag scene-deps", "diag"),
            new("/diag prefab-deps", "Show asset dependencies per prefab (capped at 100)", "/diag prefab-deps", "diag"),
            new("/diag all", "Run all diagnostics", "/diag all", "diag"),
            new("/diag asset-size", "List all project assets sorted by file size, with dependency counts", "/diag asset-size", "diag"),
            new("/diag import-hotspots", "Show most-frequently-re-imported assets from recorded import history", "/diag import-hotspots", "diag"),

            // ── test ──────────────────────────────────────────────────────
            new("/test <list|run|flaky-report> [editmode|playmode] [--timeout <seconds>]", "Test orchestration commands (launches Unity subprocess)", "/test", "test"),
            new("/test list", "List all tests (EditMode) using Unity -listTests", "/test list", "test"),
            new("/test run editmode [--timeout <seconds>]", "Run EditMode tests, capture NUnit XML results", "/test run editmode", "test"),
            new("/test run playmode [--timeout <seconds>]", "Run PlayMode tests, capture NUnit XML results", "/test run playmode", "test"),
            new("/test flaky-report", "Show tests with mixed Pass/Fail outcomes across run history", "/test flaky-report", "test"),

            // ── upm ───────────────────────────────────────────────────────
            new("/upm", "Unity Package Manager commands", "/upm", "upm"),
            new("/upm list [--outdated] [--builtin] [--git]", "List installed Unity packages (UPM)", "/upm list", "upm"),
            new("/upm ls [--outdated] [--builtin] [--git]", "Alias for /upm list", "/upm ls", "upm"),
            new("/upm install <target>", "Install Unity package by registry ID, Git URL, or file: path", "/upm install", "upm"),
            new("/upm add <target>", "Alias for /upm install", "/upm add", "upm"),
            new("/upm i <target>", "Alias for /upm install", "/upm i", "upm"),
            new("/upm remove <id>", "Remove Unity package by package ID", "/upm remove", "upm"),
            new("/upm rm <id>", "Alias for /upm remove", "/upm rm", "upm"),
            new("/upm uninstall <id>", "Alias for /upm remove", "/upm uninstall", "upm"),
            new("/upm update <id> [version]", "Update package to latest or specified version", "/upm update", "upm"),
            new("/upm u <id> [version]", "Alias for /upm update", "/upm u", "upm"),

            // ── addressable ───────────────────────────────────────────────
            new("/addressable init", "Create Addressables settings and default groups if missing", "/addressable init", "addressable"),
            new("/addressable profile list", "List Addressables profiles with evaluated variables", "/addressable profile list", "addressable"),
            new("/addressable profile set <name>", "Set active Addressables profile", "/addressable profile set", "addressable"),
            new("/addressable group list", "List Addressables groups, packing mode, and compression", "/addressable group list", "addressable"),
            new("/addressable group create <name> [--default]", "Create an Addressables group", "/addressable group create", "addressable"),
            new("/addressable group remove <name>", "Remove an Addressables group and unmark entries", "/addressable group remove", "addressable"),
            new("/addressable entry add <asset-path> <group-name>", "Mark asset as Addressable and assign group", "/addressable entry add", "addressable"),
            new("/addressable entry remove <asset-path>", "Unmark an Addressable asset entry", "/addressable entry remove", "addressable"),
            new("/addressable entry rename <asset-path> <new-address>", "Set Addressable key for asset entry", "/addressable entry rename", "addressable"),
            new("/addressable entry label <asset-path> <label> [--remove]", "Add/remove Addressables label on entry", "/addressable entry label", "addressable"),
            new("/addressable bulk add --folder <path> --group <name> [--type <T>]", "Bulk add folder assets to an Addressables group", "/addressable bulk add", "addressable"),
            new("/addressable bulk label --folder <path> --label <name> [--type <T>] [--remove]", "Bulk apply/remove Addressables label over folder assets", "/addressable bulk label", "addressable"),
            new("/addressable analyze [--duplicate]", "Analyze Addressables layout or duplicate dependencies", "/addressable analyze", "addressable"),

            // ── tag ───────────────────────────────────────────────────────
            new("/tag <list|add|remove>", "Manage Unity project tags (built-in and custom)", "/tag", "tag"),
            new("/tag list", "List all tags (built-in and custom)", "/tag list", "tag"),
            new("/tag ls", "Alias for /tag list", "/tag ls", "tag"),
            new("/tag add <name>", "Add a new custom tag. Fails if it already exists", "/tag add", "tag"),
            new("/tag a <name>", "Alias for /tag add", "/tag a", "tag"),
            new("/tag remove <name>", "Remove a custom tag. Fails if the tag is built-in", "/tag remove", "tag"),
            new("/tag rm <name>", "Alias for /tag remove", "/tag rm", "tag"),

            // ── layer ─────────────────────────────────────────────────────
            new("/layer <list|add|rename|remove>", "Manage Unity project layers (indices 0-31)", "/layer", "layer"),
            new("/layer list", "List all layers showing index and name", "/layer list", "layer"),
            new("/layer ls", "Alias for /layer list", "/layer ls", "layer"),
            new("/layer add <name> [--index <idx>]", "Add a layer. Finds first empty user slot (8-31) unless --index is given", "/layer add", "layer"),
            new("/layer a <name> [--index <idx>]", "Alias for /layer add", "/layer a", "layer"),
            new("/layer rename <old-name|index> <new-name>", "Rename a user layer. Fails for built-in layers (0-7)", "/layer rename", "layer"),
            new("/layer rn <old-name|index> <new-name>", "Alias for /layer rename", "/layer rn", "layer"),
            new("/layer remove <name|index>", "Clear a user layer slot. Fails for built-in layers (0-7)", "/layer remove", "layer"),
            new("/layer rm <name|index>", "Alias for /layer remove", "/layer rm", "layer"),

            // ── asset ─────────────────────────────────────────────────────
            new("/asset rename <path> <new-name>", "Rename an asset at the given path (DestructiveWrite, requires approval)", "/asset rename", "asset"),
            new("/asset remove <path>", "Delete an asset at the given path (DestructiveWrite, requires approval)", "/asset remove", "asset"),
            new("/asset create <type> <path>", "Create a new asset of the given type at path", "/asset create", "asset"),
            new("/asset create-script <name> <path>", "Create a new C# script at path", "/asset create-script", "asset"),
            new("/asset describe <path> [--engine blip|clip]", "Describe asset visually using local BLIP/CLIP model (SafeRead)", "/asset describe", "asset"),

            // ── animator ──────────────────────────────────────────────────
            new("/animator param add <asset-path> <name> <type>", "Add a parameter to an AnimatorController (type: float|int|bool|trigger)", "/animator param add", "animation"),
            new("/animator param remove <asset-path> <name>", "Remove a parameter from an AnimatorController by name", "/animator param remove", "animation"),
            new("/animator state add <asset-path> <name> [--layer <n>]", "Add a new state to the target layer's root state machine (layer 0 by default)", "/animator state add", "animation"),
            new("/animator transition add <asset-path> <from-state> <to-state> [--layer <n>]", "Create a transition between two states; use AnyState as <from-state> to route from the Any state", "/animator transition add", "animation"),

            // ── clip ──────────────────────────────────────────────────────
            new("/clip config <asset-path> [--loop-time <bool>] [--loop-pose <bool>]", "Modify loop settings of an AnimationClip", "/clip config", "animation"),
            new("/clip event add <asset-path> <time> <function-name> [--string <val>|--float <val>|--int <val>]", "Insert an AnimationEvent at the specified time (seconds)", "/clip event add", "animation"),
            new("/clip event clear <asset-path>", "Remove all animation events from a clip (DestructiveWrite)", "/clip event clear", "animation"),
            new("/clip curve clear <asset-path>", "Remove all property curves/keyframes from a clip (DestructiveWrite)", "/clip curve clear", "animation"),

            // ── scene ─────────────────────────────────────────────────────
            new("/scene load <path>", "Load a scene by path (replaces current)", "/scene load", "scene"),
            new("/scene add <path>", "Additively load a scene by path", "/scene add", "scene"),
            new("/scene unload <path>", "Unload an additively-loaded scene", "/scene unload", "scene"),
            new("/scene remove <path>", "Remove a scene from the loaded set", "/scene remove", "scene"),
            new("/hierarchy snapshot", "Dump the current scene hierarchy as structured data (same as /dump hierarchy)", "/hierarchy snapshot", "scene"),

            // ── time ─────────────────────────────────────────────────────
            new("/time scale <float>", "Set Time.timeScale (e.g., 0.1 for slow motion, 2.0 for fast-forward)", "/time scale", "time"),

            // ── compile ───────────────────────────────────────────────────
            new("/compile request", "Trigger a Unity script recompilation (Bridge mode only — returns unsupported route in Host/batch mode)", "/compile request", "compile"),
            new("/compile status", "Check the result of the last compilation pass (Bridge mode only — returns unsupported route in Host/batch mode)", "/compile status", "compile"),
            new("/console clear", "Clear the Unity console log", "/console clear", "compile"),

            // ── eval ──────────────────────────────────────────────────────
            new("/eval '<code>' [--declarations '<decl>'] [--timeout <ms>] [--dry-run] [--json]", "Evaluate C# in the Unity Editor context (PrivilegedExec)", "/eval", "eval"),
            new("/ev '<code>'", "Alias for /eval", "/ev", "eval"),

            // ── profiling ─────────────────────────────────────────────────
            new("/profiler inspect", "Show profiler state: enabled, deep profiling, frame range, memory stats", "/profiler inspect", "profiling"),
            new("/profiler start [--deep] [--editor] [--keep-frames]", "Start profiler recording", "/profiler start", "profiling"),
            new("/profiler stop", "Stop profiler recording, return frame range summary", "/profiler stop", "profiling"),
            new("/profiler load <path> [--keep-existing]", "Load a profiler capture (.data) into the editor session", "/profiler load", "profiling"),
            new("/profiler save <path>", "Save current editor profiler session to disk", "/profiler save", "profiling"),
            new("/profiler snapshot <path>", "Take a memory snapshot (.snap)", "/profiler snapshot", "profiling"),
            new("/profiler frames --from <a> --to <b>", "Frame range statistics (CPU/GPU/FPS avg/p50/p95/max)", "/profiler frames", "profiling"),
            new("/profiler counters --from <a> --to <b> [--names <list>]", "Extract counter series for a frame range", "/profiler counters", "profiling"),
            new("/profiler threads --frame <n>", "Enumerate profiler threads for a frame", "/profiler threads", "profiling"),
            new("/profiler markers --frame <n>", "Top markers by time for a single frame", "/profiler markers", "profiling"),
            new("/profiler markers --from <a> --to <b>", "Aggregated marker hotspot table for a frame range", "/profiler markers", "profiling"),
            new("/profiler sample --frame <n> --thread <idx>", "Raw sample details for a frame/thread", "/profiler sample", "profiling"),
            new("/profiler gc-alloc --from <a> --to <b>", "GC allocation analysis for a frame range", "/profiler gc-alloc", "profiling"),
            new("/profiler compare <baseline> <candidate>", "Compare two captures or frame ranges", "/profiler compare", "profiling"),
            new("/profiler budget-check <expressions...>", "Check performance budgets (CI-friendly pass/fail)", "/profiler budget-check", "profiling"),
            new("/profiler export-summary <path>", "Export analysis summary to file", "/profiler export-summary", "profiling"),
            new("/profiler live start [--counters <list>] [--duration <seconds>]", "Start low-overhead live counter recording", "/profiler live start", "profiling"),
            new("/profiler live stop", "Stop live counter recording and return stats", "/profiler live stop", "profiling"),
            new("/profiler recorders", "List available profiler recorders/counters", "/profiler recorders", "profiling"),
            new("/profiler frame-timing", "CPU/GPU frame timing from FrameTimingManager", "/profiler frame-timing", "profiling"),
            new("/profiler binary-log start <path>", "Start raw binary log (.raw) via Profiler.logFile", "/profiler binary-log start", "profiling"),
            new("/profiler binary-log stop", "Stop raw binary logging", "/profiler binary-log stop", "profiling"),
            new("/profiler annotate session <json>", "Emit session metadata into the profiler stream", "/profiler annotate session", "profiling"),
            new("/profiler annotate frame <json>", "Emit frame metadata into the profiler stream", "/profiler annotate frame", "profiling")
        ];
    }

    public static List<CommandSpec> CreateProjectCommands()
    {
        return
        [
            // ── core (navigation / basic operations) ──────────────────────
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
            new("inspect", "Enter inspector for focused object", "inspect"),
            new("asset find <query>", "Fuzzy find assets in project mode", "asset find"),
            new("asset duplicate <idx|name> [new-path]", "Duplicate an asset in project mode", "asset duplicate"),

            // ── build ─────────────────────────────────────────────────────
            new("build run [target] [--dev] [--debug] [--clean] [--path <output-path>]", "Run Unity build for target", "build run", "build"),
            new("build exec <Method>", "Execute static build method", "build exec", "build"),
            new("build scenes", "Open interactive scene build-settings TUI", "build scenes", "build"),
            new("build addressables [--clean] [--update]", "Build Addressables content", "build addressables", "build"),
            new("build cancel", "Request cancellation of an ongoing build", "build cancel", "build"),
            new("build targets", "List installed Unity build support targets", "build targets", "build"),
            new("build logs", "Open restartable build log tail viewer", "build logs", "build"),
            new("build snapshot-packages", "Snapshot current package manifest", "build snapshot-packages", "build"),
            new("build preflight", "Run preflight validation checks", "build preflight", "build"),
            new("build artifact-metadata", "Read last build artifact metadata", "build artifact-metadata", "build"),
            new("build failure-classify", "Classify failures from last build report", "build failure-classify", "build"),
            new("build report", "Full build report: preflight + artifacts + failures", "build report", "build"),
            new("b [target] [--dev] [--debug] [--clean] [--path <output-path>]", "Alias for build run", "b", "build"),
            new("bx <Method>", "Alias for build exec", "bx", "build"),
            new("ba [--clean] [--update]", "Alias for build addressables", "ba", "build"),

            // ── test ──────────────────────────────────────────────────────
            new("test list", "List all tests (EditMode) using Unity -listTests", "test list", "test"),
            new("test run editmode [--timeout <seconds>]", "Run EditMode tests, capture NUnit XML results", "test run editmode", "test"),
            new("test run playmode [--timeout <seconds>]", "Run PlayMode tests (may trigger player build)", "test run playmode", "test"),

            // ── upm ───────────────────────────────────────────────────────
            new("upm list [--outdated] [--builtin] [--git]", "List installed Unity packages (UPM)", "upm list", "upm"),
            new("upm ls [--outdated] [--builtin] [--git]", "Alias for upm list", "upm ls", "upm"),
            new("upm install <target>", "Install Unity package by registry ID, Git URL, or file: path", "upm install", "upm"),
            new("upm add <target>", "Alias for upm install", "upm add", "upm"),
            new("upm i <target>", "Alias for upm install", "upm i", "upm"),
            new("upm remove <id>", "Remove Unity package by package ID", "upm remove", "upm"),
            new("upm rm <id>", "Alias for upm remove", "upm rm", "upm"),
            new("upm uninstall <id>", "Alias for upm remove", "upm uninstall", "upm"),
            new("upm update <id> [version]", "Update package to latest or specified version", "upm update", "upm"),
            new("upm u <id> [version]", "Alias for upm update", "upm u", "upm"),

            // ── addressable ───────────────────────────────────────────────
            new("addressable init", "Create Addressables settings and default groups if missing", "addressable init", "addressable"),
            new("addressable profile list", "List Addressables profiles with evaluated variables", "addressable profile list", "addressable"),
            new("addressable profile set <name>", "Set active Addressables profile", "addressable profile set", "addressable"),
            new("addressable group list", "List Addressables groups, packing mode, and compression", "addressable group list", "addressable"),
            new("addressable group create <name> [--default]", "Create an Addressables group", "addressable group create", "addressable"),
            new("addressable group remove <name>", "Remove an Addressables group and unmark entries", "addressable group remove", "addressable"),
            new("addressable entry add <asset-path> <group-name>", "Mark asset as Addressable and assign group", "addressable entry add", "addressable"),
            new("addressable entry remove <asset-path>", "Unmark an Addressable asset entry", "addressable entry remove", "addressable"),
            new("addressable entry rename <asset-path> <new-address>", "Set Addressable key for asset entry", "addressable entry rename", "addressable"),
            new("addressable entry label <asset-path> <label> [--remove]", "Add/remove Addressables label on entry", "addressable entry label", "addressable"),
            new("addressable bulk add --folder <path> --group <name> [--type <T>]", "Bulk add folder assets to an Addressables group", "addressable bulk add", "addressable"),
            new("addressable bulk label --folder <path> --label <name> [--type <T>] [--remove]", "Bulk apply/remove Addressables label over folder assets", "addressable bulk label", "addressable"),
            new("addressable analyze [--duplicate]", "Analyze Addressables layout or duplicate dependencies", "addressable analyze", "addressable"),

            // ── tag ───────────────────────────────────────────────────────
            new("tag list", "List all tags (built-in and custom)", "tag list", "tag"),
            new("tag ls", "Alias for tag list", "tag ls", "tag"),
            new("tag add <name>", "Add a new custom tag", "tag add", "tag"),
            new("tag a <name>", "Alias for tag add", "tag a", "tag"),
            new("tag remove <name>", "Remove a custom tag", "tag remove", "tag"),
            new("tag rm <name>", "Alias for tag remove", "tag rm", "tag"),

            // ── layer ─────────────────────────────────────────────────────
            new("layer list", "List all layers showing index and name", "layer list", "layer"),
            new("layer ls", "Alias for layer list", "layer ls", "layer"),
            new("layer add <name> [--index <idx>]", "Add a layer at first empty user slot (8-31) or specified index", "layer add", "layer"),
            new("layer a <name> [--index <idx>]", "Alias for layer add", "layer a", "layer"),
            new("layer rename <old-name|index> <new-name>", "Rename a user layer (fails for built-in layers 0-7)", "layer rename", "layer"),
            new("layer rn <old-name|index> <new-name>", "Alias for layer rename", "layer rn", "layer"),
            new("layer remove <name|index>", "Clear a user layer slot (fails for built-in layers 0-7)", "layer remove", "layer"),
            new("layer rm <name|index>", "Alias for layer remove", "layer rm", "layer"),

            // ── asset ─────────────────────────────────────────────────────
            new("asset rename <path> <new-name>", "Rename an asset at the given path", "asset rename", "asset"),
            new("asset remove <path>", "Delete an asset at the given path", "asset remove", "asset"),
            new("asset create <type> <path>", "Create a new asset of the given type at path", "asset create", "asset"),
            new("asset create-script <name> <path>", "Create a new C# script at path", "asset create-script", "asset"),
            new("asset describe <path> [--engine blip|clip]", "Describe asset visually using local BLIP/CLIP model", "asset describe", "asset"),

            // ── time ─────────────────────────────────────────────────────
            new("time scale <float>", "Set Time.timeScale (e.g., 0.1 for slow motion)", "time scale", "time"),

            // ── compile ───────────────────────────────────────────────────
            new("compile request", "Trigger a Unity script recompilation (Bridge mode only — returns unsupported route in Host/batch mode)", "compile request", "compile"),
            new("compile status", "Check the result of the last compilation pass (Bridge mode only — returns unsupported route in Host/batch mode)", "compile status", "compile"),
            new("console clear", "Clear the Unity console log", "console clear", "compile"),

            // ── scene ─────────────────────────────────────────────────────
            new("scene load <path>", "Load a scene by path (replaces current)", "scene load", "scene"),
            new("scene add <path>", "Additively load a scene by path", "scene add", "scene"),
            new("scene unload <path>", "Unload an additively-loaded scene", "scene unload", "scene"),
            new("scene remove <path>", "Remove a scene from the loaded set", "scene remove", "scene"),
            new("hierarchy snapshot", "Dump the current scene hierarchy as structured data", "hierarchy snapshot", "scene"),

            // ── animator ──────────────────────────────────────────────────
            new("animator param add <asset-path> <name> <type>", "Add a parameter to an AnimatorController (type: float|int|bool|trigger)", "animator param add", "animation"),
            new("animator param remove <asset-path> <name>", "Remove a parameter from an AnimatorController by name", "animator param remove", "animation"),
            new("animator state add <asset-path> <name> [--layer <n>]", "Add a new state to the target layer's root state machine (layer 0 by default)", "animator state add", "animation"),
            new("animator transition add <asset-path> <from-state> <to-state> [--layer <n>]", "Create a transition between two states; use AnyState as <from-state> to route from the Any state", "animator transition add", "animation"),

            // ── clip ──────────────────────────────────────────────────────
            new("clip config <asset-path> [--loop-time <bool>] [--loop-pose <bool>]", "Modify loop settings of an AnimationClip", "clip config", "animation"),
            new("clip event add <asset-path> <time> <function-name> [--string <val>|--float <val>|--int <val>]", "Insert an AnimationEvent at the specified time (seconds)", "clip event add", "animation"),
            new("clip event clear <asset-path>", "Remove all animation events from a clip", "clip event clear", "animation"),
            new("clip curve clear <asset-path>", "Remove all property curves/keyframes from a clip", "clip curve clear", "animation"),

            // ── prefab ────────────────────────────────────────────────────
            new("prefab create <idx|name> <asset-path>", "Convert scene GameObject to new Prefab Asset on disk", "prefab create", "prefab"),
            new("prefab apply <idx>", "Push instance overrides back to source Prefab Asset", "prefab apply", "prefab"),
            new("prefab revert <idx>", "Discard local overrides, revert to source Prefab Asset", "prefab revert", "prefab"),
            new("prefab unpack <idx> [--completely]", "Break prefab connection, turn into regular GameObject", "prefab unpack", "prefab"),
            new("prefab variant <source-path> <new-path>", "Create Prefab Variant inheriting from base prefab", "prefab variant", "prefab")
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
            new("component find <query>", "Find components on inspected target", "component find"),
            new("component duplicate <index|name>", "Duplicate a component on inspected target", "component duplicate"),
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
