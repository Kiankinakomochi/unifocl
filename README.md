# unifocl
![unifocl logo](https://github.com/user-attachments/assets/1bae1b33-b120-4ba2-bb34-77aa1250e7f7)

A dual-purpose operations layer for Unity development, engineered for both human developers and autonomous AI agents. **unifocl** provides a structured, deterministic way to interact with, navigate, and mutate your Unity projects without relying on the graphical Editor.

It serves as a programmable bridge to Unity—offering a focused CLI/TUI (Terminal User Interface) for developers who prefer keyboard-driven workflows, alongside a robust, schema-driven execution path for LLMs, automations, and autonomous tooling.

At its core, unifocl is designed to be lightweight and highly adaptable. It provides a reliable foundation of essential editing tools out of the box, while making it incredibly easy for developers to create custom commands. Instead of bundling a massive, one-size-fits-all toolset, unifocl empowers you to build exactly what your unique project needs. This streamlined approach keeps your workflow clean, prevents unnecessary LLM context consumption, and ensures your MCP server schemas stay lightning-fast and token-efficient.

unifocl is an independent project and is not associated with, affiliated with, or endorsed by Unity Technologies.

## Features

Built to provide a unified operational model for both humans and machines, unifocl offers:

- **Dual-Interface Navigation:** Context-aware environments (Hierarchy, Project, Inspector) accessible via a clean Spectre.Console TUI for humans, or as structured state dumps for agent context windows.
- **Deterministic Manipulation:** Command-driven file and object operations guarded by transactional safety and dry-run capabilities, ensuring predictability for both manual inputs and machine execution.
- **Native Agentic Tooling:** Built-in MCP (Model Context Protocol) server mode, strict JSON/YAML response envelopes, and concurrent worktree orchestration designed for multi-agent workflows.
- **Lean & Token-Efficient:** By keeping the core API surface streamlined and focused on essential project operations, unifocl preserves precious LLM context windows. This minimizes MCP server token consumption, ensuring your AI agents remain fast, highly focused, and cost-effective.
- **Highly Customizable Tools:** Every Unity project is unique, so unifocl is built to be easily extended. Developers can seamlessly expose their own C# editor methods as live MCP tools simply by adding the [UnifoclCommand] attribute. This allows you to effortlessly tailor the toolset to exactly match your team's needs. Custom tools are dynamically discovered via `get_categories` / `load_category` / `unload_category` and support the full dry-run sandbox automatically. See [`docs/custom-commands.md`](docs/custom-commands.md).
- **Zero-Touch Compilation:** After deploying editor scripts, unifocl automatically triggers Unity recompilation without requiring manual window focus. OS-level window activation and `CompilationPipeline.RequestScriptCompilation()` are combined so new tools are available immediately. Configurable for CI/headless runners. See [`docs/editor-compilation.md`](docs/editor-compilation.md).

## Installation

### macOS (Apple Silicon) — shell installer

```sh
curl -fsSL https://raw.githubusercontent.com/Kiankinakomochi/unifocl/main/scripts/install.sh | sh
```

Installs the binary to `/usr/local/bin/unifocl`. Prompts for `sudo` only if that directory is not writable.

### macOS — Homebrew (Intel & Apple Silicon)

```sh
brew tap Kiankinakomochi/unifocl
brew install unifocl
```

### Windows (x64) — PowerShell installer

```powershell
iwr -useb https://raw.githubusercontent.com/Kiankinakomochi/unifocl/main/scripts/install.ps1 | iex
```

Installs to `%LOCALAPPDATA%\unifocl\bin` and adds it to your user `PATH` automatically.

### Windows — Winget

Winget submission is in progress. After approval:

```
winget install Kiankinakomochi.unifocl
```

### Manual download

Download pre-built archives from the [latest GitHub release](https://github.com/Kiankinakomochi/unifocl/releases/latest) and place the binary anywhere in your `PATH`.

### Claude Code Plugin

`@unifocl/claude-plugin` is an npm package that integrates unifocl natively into Claude Code. Installing it does two things automatically:

1. **Registers the MCP server** — adds `unifocl --mcp-server` to your Claude Code MCP config so the `ListCommands`, `LookupCommand`, `GetMutateSchema`, `GetCategories`, `LoadCategory`, and `GetAgentWorkflowGuide` tools are available in every session without manual JSON editing.
2. **Installs slash commands** — adds five workflow prompts directly into Claude Code:
   - `/init` — initialize the unifocl bridge in a Unity project
   - `/context` — hydrate full scene state (hierarchy, project, inspector)
   - `/mutate` — guided mutation with schema validation and mandatory dry-run
   - `/status` — check daemon, project, and editor status
   - `/workflow` — full agentic workflow reference for multi-step sessions

**Responsibility split:** The plugin handles the Claude Code install experience and narrative workflow prompts. The MCP server (`unifocl --mcp-server`) handles the low-level programmatic Unity bridge. They run as separate processes and have no overlapping responsibilities.

**Prerequisites:** `unifocl` must be installed and on your `PATH` before the plugin's MCP server entry can start.

```sh
# Install via Claude Code
claude mcp add @unifocl/claude-plugin

# Or install the npm package globally and restart Claude Code
npm install -g @unifocl/claude-plugin
```

After installation, confirm the MCP server is registered:

```sh
claude mcp list
# unifocl   unifocl --mcp-server
```

Then use `/init <path-to-unity-project>` in Claude Code to get started.

## Command & Feature Guide

unifocl operates through a unified command set. Humans can launch the interactive shell (boot screen) to use **slash commands** (e.g., `/open`) for lifecycle operations and **standard commands** (e.g., `ls`, `cd`) for contextual actions. AI agents access these exact same commands via the stateless `exec` pathway or the built-in MCP server.

### 1. System & Lifecycle Commands

These commands manage your session, project loading, and configuration. In the interactive shell, they are prefixed with a slash (`/`).

| **Command** | **Alias** | **Description** |
| --- | --- | --- |
| `/open <path> [--allow-unsafe]` | `/o` | Open a Unity project. Starts/attaches to the daemon and loads metadata. |
| `/close` | `/c` | Detach from the current project and stop the attached daemon. |
| `/quit` | `/q`, `/exit` | Exit the CLI client (leaves the daemon running). |
| `/daemon <start&#124;stop&#124;restart&#124;ps&#124;attach&#124;detach>` | `/d` | Manage daemon lifecycle commands. |
| `/new <name> [version]` |  | Bootstrap a new Unity project. |
| `/clone <git-url>` |  | Clone a repository and set up local CLI bridge-mode config. |
| `/recent [idx]` |  | List recent projects or open one by index. |
| `/config <get/set/list/reset>` | `/cfg` | Manage CLI preferences (e.g., themes). |
| `/status` | `/st` | Show daemon, mode, editor, project, and session status summary. |
| `/doctor` |  | Run environment and tooling diagnostics. |
| `/scan [--root <dir>] [--depth <n>]` |  | Scan directories for Unity projects. |
| `/info <path?>` |  | Inspect Unity project metadata and protocol details. |
| `/logs [daemon&#124;unity] [-f]` |  | Show daemon runtime summary or follow logs. |
| `/examples` |  | Show common operational command flows. |
| `/update` |  | Show installed CLI version and update guidance. |
| `/install-hook` |  | Run bridge dependency install flow (`/init`) against current/open project. |
| `/unity detect` |  | List installed Unity editors. |
| `/unity set <path>` |  | Set default Unity editor path. |
| `/build run [target] [--dev] [--debug] [--clean] [--path <output-path>]` | `/b` | Trigger a Unity player build. If target is omitted, choose from an interactive target selector. |
| `/build exec <Method>` | `/bx` | Execute a static build method (e.g., `CI.Builder.BuildAndroidProd`). |
| `/build scenes` |  | Open an interactive TUI to view, toggle, and reorder build scenes. |
| `/build addressables [--clean] [--update]` | `/ba` | Trigger an Addressables content build (full or update mode). |
| `/build cancel` |  | Request cancellation for the active build process via daemon. |
| `/build targets` |  | List platform build support currently available in this Unity Editor. |
| `/build logs` |  | Reopen live build log tail (restartable, with error filtering). |
| `/upm` |  | Show Unity Package Manager command usage and options. |
| `/upm list [--outdated] [--builtin] [--git]` | `/upm ls` | List installed Unity packages (with optional outdated/builtin/git filters). |
| `/upm install <target>` | `/upm add`, `/upm i` | Install a package by package ID, Git URL, or `file:` target. |
| `/upm remove <id>` | `/upm rm`, `/upm uninstall` | Remove a package by package ID. |
| `/upm update <id> [version]` | `/upm u` | Update a package to latest or a specified version. |
| `/prefab create <idx\|name> <asset-path>` |  | Convert a scene GameObject into a new Prefab Asset on disk. |
| `/prefab apply <idx>` |  | Push instance overrides back to the source Prefab Asset. |
| `/prefab revert <idx>` |  | Discard local overrides, revert to the source Prefab Asset. |
| `/prefab unpack <idx> [--completely]` |  | Break the prefab connection, turning the instance into a regular GameObject. |
| `/prefab variant <source-path> <new-path>` |  | Create a Prefab Variant inheriting from a base prefab. |
| `/init [path]` |  | Generate bridge-mode config and install editor-side dependencies. |
| `/keybinds` | `/shortcuts` | Show modal keybinds and shortcuts. |
| `/version` |  | Show CLI and protocol version. |
| `/protocol` |  | Show supported JSON schema capabilities. |
| `/dump <hierarchy&#124;project&#124;inspector> [--format json&#124;yaml] [--compact] [--depth n] [--limit n]` |  | Dump deterministic mode state for agentic workflows. |
| `/eval '<code>' [--declarations '<decl>'] [--timeout <ms>] [--dry-run]` | `/ev` | Evaluate arbitrary C# in the Unity Editor context (PrivilegedExec). |
| `/clear` |  | Clear and redraw the boot screen and log. |
| `/help [topic]` | `/?` | Show help by topic (`root`, `project`, `inspector`, `build`, `upm`, `daemon`). |

**Behavior Notes & Protocol Hardening:**

- `/daemon` without a subcommand returns usage plus process summary.
- Unsupported slash-command routes return explicit `unsupported route` messaging.
- **Host-mode hierarchy fallback** is available when no GUI bridge is attached:
    - `HIERARCHY_GET` returns an `Assets`root snapshot.
    - `HIERARCHY_FIND` fuzzy-searches node names/paths.
    - `HIERARCHY_CMD` supports `mk`, `rm`, `rename`, `mv`, `toggle` with guardrails.
    - *Host-mode fallback safety constraints:* All mutations are constrained within `Assets`; move/rename path-escape is rejected; moving a directory into itself/descendants is rejected; `mk` validates names and supports typed placeholders (`Empty`, `EmptyChild`, `EmptyParent`, `Text/TMP`, `Sprite`, default prefab).
- Durable project mutations are supported (`submit -> status -> result`) so mutation outcomes remain queryable even if Unity refresh/compile/domain reload interrupts an in-flight HTTP response.
- Durable mutations use native daemon HTTP endpoints by default (`submit -> status -> result`) and no longer require the external Unity-MCP package/runtime dependencies.
- Built-in MCP server mode is available for automation tooling: start with `unifocl --mcp-server` (stdio transport, .NET MCP SDK).
- MCP command lookup tools are exposed by the built-in server so agents can discover usage without reading full docs:
    - `ListCommands(scope, query, limit)`
    - `LookupCommand(command, scope)`
- **Custom tool category tools** allow agents to discover and load user-defined `[UnifoclCommand]` methods on demand:
    - `get_categories()` — list available tool categories from the project manifest
    - `load_category(name)` — register a category's tools as live MCP tools (`tools/list_changed` is fired)
    - `unload_category(name)` — remove a category's tools from the active list
    - Full guide: [`docs/custom-commands.md`](docs/custom-commands.md)
- MCP server architecture + agent JSON configuration guide:
    - `docs/mcp-server-architecture.md`
    - Quick multi-client setup helper: `scripts/setup-mcp-agents.sh`
- **Durable HTTP fallback endpoints:** `POST /project/mutation/submit`, `GET /project/mutation/status?requestId=<id>`, `GET /project/mutation/result?requestId=<id>`, `POST /project/mutation/cancel?requestId=<id>`

### 2. Daemon Management

The daemon acts as the persistent backend coordinator for both human operators and agentic workflows. Manage it using the `/daemon` (or `/d`) command suite.

| **Subcommand** | **Description** |
| --- | --- |
| `start` | Start a daemon. Accepts flags: `--port`, `--unity <path>`, `--project <path>`, `--headless` (Host mode), `--allow-unsafe`, `--unsafe-http` (enable HTTP listener in addition to UDS). |
| `stop` | Stop the daemon instance controlled by this CLI. |
| `restart` | Restart the currently attached daemon. |
| `ps` | List running daemon instances, ports, uptimes, and associated projects. |
| `attach <port>` | Attach the CLI to an existing daemon at the specified port. |
| `detach` | Detach the CLI but keep the daemon alive in the background. |

**Concurrent Autonomous Agents Notes:** For concurrent autonomous agents, provision isolated git worktrees and run daemon boot per worktree with dynamic port mapping.

- Bash workflow: `src/unifocl/scripts/agent-worktree.sh`
- PowerShell workflow: `src/unifocl/scripts/agent-worktree.ps1`
- Provision a dedicated branch worktree from `origin/main`; do not share mutable worktrees across agents.
- Copy warmed Unity `Library` cache before daemon boot when needed.
- Allocate daemon ports dynamically. The unifocl daemon communicates over UDS by default; pass `--unsafe-http` at daemon start to also expose a localhost HTTP listener. Validate Unity Editor bridge readiness via the CLIDaemon `/ping` endpoint (`http://127.0.0.1:<dynamic-port>/ping`).
- Keep all mutations scoped to each provisioned worktree.
- Teardown after completion: stop daemon, then `git worktree remove --force <path>` and `git worktree prune`.
- Operating boundaries: no cross-worktree edits, no shared mutable daemon state, no daemon port reuse assumptions.
- Milestone tracking stream: `Worktree Isolation and Multi-Agent Daemon Safety`.
- Smoke project default: `setup-smoke-project` seeds `Packages/manifest.json` with `com.unity.modules.imageconversion`.

### 3. Context & Mode Switching

Switch the active operational context for your session or agent execution.

| **Command** | **Alias** | **Description** |
| --- | --- | --- |
| `/project` | `/p` | Switch to Project mode (asset structure navigation). |
| `/hierarchy` | `/h` | Switch to Hierarchy mode (scene structure TUI/tree). |
| `/inspect <idx/path>` | `/i` | Switch to Inspector mode and focus a target. |

### 4. Contextual Operations (Non-Slash Commands)

Interact directly with the active environment. Mutating operations are safely routed through Bridge mode when available, or Host mode fallback when applicable, ensuring deterministic behavior for both humans and AI.

| **Command** | **Alias** | **Description** |
| --- | --- | --- |
| `list` | `ls` | List entries in the current active context. |
| `enter <idx>` | `cd` | Enter the selected node, folder, or component by index. |
| `up` | `..` | Navigate up one level to the parent. |
| `make <type> <name>` | `mk` | Create an item (e.g., `mk script Player`, `mk gameobject`). |
| `load <idx/name>` |  | Load/open a scene, prefab, or script. |
| `remove <idx>` | `rm` | Remove the selected item. |
| `rename <idx> <new>` | `rn` | Rename the selected item. |
| `set <field> <val>` | `s` | Set a field or property value. |
| `toggle <target>` | `t` | Toggle boolean/active/enabled flags. |
| `move <...>` | `mv` | Move, reparent, or reorder an item. |
| `f [--type <type>&#124;t:<type>] <query>` | `ff` | Run fuzzy find in the active mode. |
| `inspect [idx&#124;path]` |  | Enter inspector root target from inspector context. |
| `edit <field> <value...>` | `e` | Edit serialized field value for the selected component (inspector). |
| `component add <type>` | `comp add <type>` | Add a component to the inspected object. |
| `component remove <index&#124;name>` | `comp remove <index&#124;name>` | Remove a component from the inspected object. |
| `scroll [body&#124;stream] <up&#124;down> [count]` |  | Scroll inspector body or command stream. |
| `upm list [--outdated] [--builtin] [--git]` | `upm ls` | List installed Unity packages in project mode. |
| `upm install <target>` | `upm add`, `upm i` | Install package by ID, Git URL, or `file:` target in project mode. |
| `upm remove <id>` | `upm rm`, `upm uninstall` | Remove package by package ID in project mode. |
| `upm update <id> [version]` | `upm u` | Update package to latest or specified version in project mode. |
| `build run [target] [--dev] [--debug] [--clean] [--path <output-path>]` | `b` | Run Unity build in project mode. |
| `build exec <Method>` | `bx` | Execute static build method in project mode. |
| `build scenes` |  | Open scene build-settings TUI in project mode. |
| `build addressables [--clean] [--update]` | `ba` | Build Addressables content in project mode. |
| `build cancel` |  | Request cancellation for active build in project mode. |
| `build targets` |  | List Unity build support targets in project mode. |
| `build logs` |  | Open restartable build log tail in project mode. |
| `prefab create <idx\|name> <asset-path>` |  | Convert scene GameObject to new Prefab Asset on disk in project mode. |
| `prefab apply <idx>` |  | Push instance overrides back to source Prefab Asset in project mode. |
| `prefab revert <idx>` |  | Discard local overrides, revert to source Prefab Asset in project mode. |
| `prefab unpack <idx> [--completely]` |  | Break prefab connection in project mode. |
| `prefab variant <source-path> <new-path>` |  | Create Prefab Variant from base prefab in project mode. |

### 5. Safe Mutation: Dry-Run Previews

Both human operators and AI agents can validate mutations safely before execution. `-dry-run` is supported for mutation commands in all interactive and agentic modes:

- `Hierarchy` mutations (`mk`, `toggle`, `rm`, `rename`, `mv`)
- `Inspector` mutations (`set`, `toggle`, `component add/remove`, `make`, `remove`, `rename`, `move`)
- `Project` filesystem mutations (`mk-script`, `rename-asset`, `remove-asset`, `prefab-create`, `prefab-apply`, `prefab-revert`, `prefab-unpack`, `prefab-variant`)
- **Custom `[UnifoclCommand]` tools** — pass `dryRun: true` in the tool arguments; unifocl wraps the call in a Unity Undo group and reverts all in-memory and AssetDatabase changes automatically (see [`docs/custom-commands.md`](docs/custom-commands.md))
- **`/eval` dynamic C# execution** — pass `--dry-run` to execute code inside the Undo sandbox; all Unity-tracked changes are reverted after execution completes

Behavior:

- **Hierarchy / Inspector (memory layer):** unifocl captures pre/post state snapshots, executes inside an Undo group, immediately reverts, and returns a structured diff preview.
- **Project (filesystem layer):** unifocl returns proposed path/meta changes without performing file I/O.
- **TUI/Agentic rendering:** When `dry-run` is appended, unified diff lines are shown in the transcript output for humans, or nested in the `diff` payload for agents.

Examples:

```
# hierarchy mode
mk Cube --dry-run
rename 12 NewName --dry-run

# inspector mode
set speed 5 --dry-run
component add Rigidbody --dry-run

# project mode
rename 3 PlayerController --dry-run
rm 7 --dry-run
```


### 6. Dynamic C# Eval

The `/eval` command compiles and executes arbitrary C# code directly in the Unity Editor context. It provides a fast, interactive way for both developers and agents to run queries, introspect scene state, and execute one-off editor utilities — all without creating script files.

```
/eval '<code>' [--declarations '<decl>'] [--timeout <ms>] [--dry-run] [--json]
```

**Flags:**

| Flag | Description |
| --- | --- |
| `--declarations '<decl>'` | Additional C# declarations (classes, using directives) injected before the entry point. |
| `--timeout <ms>` | Maximum execution time in milliseconds (default: 10000). |
| `--dry-run` | Execute inside a Unity Undo sandbox; all Undo-tracked changes are reverted after execution. |
| `--json` | Request JSON-formatted output. |

**Examples:**

```sh
# Simple read query
unifocl eval 'return Application.productName;'

# Void side-effect
unifocl eval 'Debug.Log("hello from eval");'

# Async code with cancellation support
unifocl eval 'await Task.Delay(10, cancellationToken); return "done";'

# Timeout protection
unifocl eval 'while(true){}' --timeout 200

# Dry-run: execute and revert all Unity Undo-tracked changes
unifocl eval 'Undo.RecordObject(Camera.main, "t"); Camera.main.name = "CHANGED";' --dry-run

# Custom declarations
unifocl eval 'return new Msg().text;' --declarations 'public class Msg { public string text = "hi"; }'

# Return a UnityEngine.Object (serialized via EditorJsonUtility)
unifocl eval 'return Camera.main;'
```

**Compilation:**

Code is compiled through Unity's own `AssemblyBuilder` pipeline rather than the legacy `CSharpCodeProvider`. This means eval code targets the same C# language version your project uses — modern pattern matching, nullable reference types, records, and other recent C# features work out of the box.

- The entry point is always `async Task<object>`, so `await` works naturally without special flags or detection heuristics.
- A `CancellationToken cancellationToken` parameter is available inside eval code, wired to the `--timeout` value.
- Default usings cover the most common scenarios: `System`, `System.IO`, `System.Linq`, `System.Collections.Generic`, `System.Text.RegularExpressions`, `System.Threading.Tasks`, `UnityEngine`, and `UnityEditor`.
- All assemblies loaded in the current editor session are available as references (project scripts, packages, plugins). Temporary eval artefacts are automatically excluded.

**Result serialization:**

unifocl uses a three-tier serialization strategy to produce the most informative output for each return type:

| Return type | Serialization strategy |
| --- | --- |
| `null` / void | `"null"` |
| `string` | Raw string value |
| Primitives (`int`, `float`, `bool`, ...) | Literal value with full numeric precision (`float` G9, `double` G17) |
| `UnityEngine.Object` | Full editor serialization via `EditorJsonUtility.ToJson` |
| `[Serializable]` types | Unity's fast `JsonUtility.ToJson` path |
| `IDictionary` | JSON object with string keys |
| `IEnumerable` (arrays, lists, sets, ...) | JSON array |
| Structured objects | Depth-limited reflection walk over public fields and readable properties |
| Other | `obj.ToString()` |

The reflection serializer is depth-limited (max 8 levels) to safely handle cyclic or deeply nested object graphs without risking stack overflows.

**Safety and approval:**

- `eval.run` is classified as `PrivilegedExec` in the ExecV2 API. Like `build.run` and `build.exec`, it requires two-step approval before execution — agents cannot silently evaluate code without explicit confirmation.
- `--dry-run` wraps execution in the same Undo-group sandbox used by custom `[UnifoclCommand]` tools. All Unity Undo-tracked changes (component edits, hierarchy modifications, scene state) are captured in an Undo group and reverted immediately after execution. `System.IO` writes are **not** reverted — this is a documented and intentional limitation shared with all dry-run paths in unifocl.
- The `--timeout` flag provides a hard cancellation boundary. If eval code exceeds the timeout, the `CancellationToken` is triggered and execution is interrupted.
- The Unity main thread is blocked during eval; the editor Update loop pauses for the duration. This is consistent with how Unity processes all editor commands and ensures serialization safety.

## Human Interface: TUI & Keybindings

For developers using the interactive CLI, unifocl features a composer with Intellisense and keyboard-driven navigation.

- Type `/` to open the slash-command suggestion palette.
- Type any standard text to receive project-mode suggestions.
- **Fuzzy Finding:** Use the `f` or `ff` command to trigger fuzzy search (e.g., `f --type script PlayerController`).

**Global Keybinds**

- **`F7`**: Toggle focus for Hierarchy TUI, Project navigator, Recent projects list, and Inspector.
- **`Esc`**: Dismiss Intellisense, or clear input if already dismissed.
- **`↑` / `↓`**: Navigate fuzzy/Intellisense candidates.
- **`Enter`**: Insert selected suggestion or commit input.

**Context-Specific Focus Navigation**

Once focused (`F7`), the arrow keys and tab behave contextually:

| **Action** | **Hierarchy Focus** | **Project Focus** | **Inspector Focus** |
| --- | --- | --- | --- |
| **`↑` / `↓`** | Move highlighted GameObject | Move highlighted file/folder | Move highlighted component/field |
| **`Tab`** | Expand selected node | Reveal/open selected entry | Inspect selected component |
| **`Shift+Tab`** | Collapse selected node | Move to parent folder | Back to component list |
| **Exit Focus** | `Esc` or `F7` | `Esc` or `F7` | `Esc` or `F7` |

## Agentic & Autonomous Workflows

unifocl treats machine execution as a first-class citizen. It provides an **agentic execution path** specifically designed for LLMs, automations, and tool wrappers that require deterministic I/O instead of interactive TUI behavior.

Core principles:

- Structured response envelope for every command.
- No Spectre/TUI rendering in agentic one-shot mode.
- Standardized error taxonomy and process exit codes.
- Explicit state serialization commands for context hydration.

### 1. One-Shot CLI for Agents

Use `exec` to run a single command and exit, suppressing all human UI:

```
unifocl exec "<command>" [--agentic] [--format json|yaml] [--project <path>] [--mode <project|hierarchy|inspector>] [--attach-port <port>] [--request-id <id>]
```

Examples:

```
unifocl exec "/version" --agentic --format json
unifocl exec "/protocol" --agentic --format yaml
unifocl exec "/dump project --format json --depth 2 --limit 5000" --agentic --project /path/to/UnityProject
unifocl exec "upm list --outdated" --agentic --project /path/to/UnityProject --mode project
```

Notes:

- `agentic` enables machine output (single response payload).
- `format` controls payload encoding (`json` or `yaml`).
- `project`, `mode`, and `attach-port` seed runtime context so commands can execute without interactive setup.

Agentic best-practice profile (native bridge + built-in MCP server):

- Use native durable daemon HTTP mutation lifecycle for writes (`submit -> status -> result`).
- Use `unifocl --mcp-server` when automation needs compact command lookup/context tools over stdio.
- For project mutations, prefer durable lifecycle calls (`submit -> status -> result`) instead of relying on a single long HTTP response.
- Reuse one `-session-seed` and one daemon attach target per workflow chain to avoid context rehydration churn.
- For deterministic edits, prefer path-based targeting and perform grouped verification (`/dump hierarchy` + `/dump inspector`) after each mutation batch.
- For concurrent agents, use one worktree and one daemon port per agent; do not run multiple mutating agents in the same worktree.

### 2. Unified Agentic Envelope

- `agentic` responses adhere to a strict machine-readable schema:

```
{
  "status": "success|error",
  "requestId": "string",
  "mode": "project|hierarchy|inspector|none",
  "action": "string",
  "data": {},
  "errors": [{ "code": "E_*", "message": "string", "hint": "string|null" }],
  "warnings": [{ "code": "W_*", "message": "string" }],
  "diff": {
    "format": "unified",
    "summary": "string|null",
    "lines": ["--- before", "+++ after", "..."]
  },
  "meta": {
    "schemaVersion": "agentic.v1",
    "protocol": "v3",
    "exitCode": 0,
    "timestampUtc": "ISO-8601 UTC",
    "extra": {}
  }
}
```

Field semantics:

- `status`: high-level outcome (`success` or `error`).
- `requestId`: caller-supplied correlation id (or generated if omitted).
- `mode`: effective runtime context after command execution.
- `action`: normalized command family (e.g. `version`, `dump`, `upm`).
- `data`: command payload (shape varies by action).
- `errors`: deterministic machine errors (empty on success).
- `warnings`: non-fatal issues.
- `diff`: optional dry-run diff payload (present when `dry-run` preview is returned).
- `meta`: schema/protocol/exit metadata plus optional command-specific extras.

Agentic VCS setup guard:

- Agentic project mutations short-circuit with `E_VCS_SETUP_REQUIRED` when UVCS is detected but project VCS setup is incomplete.
- Non-mutation agentic commands continue to run.

### 3. Agentic Exit Codes

| **Exit Code** | **Meaning** |
| --- | --- |
| `0` | Success |
| `2` | Validation / parse / context-state error |
| `3` | Daemon/bridge availability or timeout class failure |
| `4` | Internal execution error |
| `6` | Escalation required (likely sandbox/network restriction prevented execution) |

`E_VCS_SETUP_REQUIRED` is classified under exit code `2`. `E_ESCALATION_REQUIRED` is classified under exit code `6`.

### 4. `/dump` State Serialization

`/dump` is uniquely designed for agent context-window transfer and deterministic snapshots:

```
/dump <hierarchy|project|inspector> [--format json|yaml] [--compact] [--depth n] [--limit n]
```

Current behavior:

- `hierarchy`: fetches hierarchy snapshot from attached daemon.
- `project`: serializes deterministic `Assets` tree entries.
- `inspector`: serializes inspector components/fields from attached bridge path.

Context handling:

- If required runtime state is missing (for example no attached daemon for `hierarchy`), response returns `E_MODE_INVALID` with a corrective hint.
- Unsupported category returns `E_VALIDATION`.

### 5. Daemon ExecV2 Endpoints

Daemon service mode exposes structured agent endpoints over **UDS** by default (socket at `~/.unifocl-runtime/daemon-{port}.sock`). HTTP access requires `--unsafe-http` at daemon start and the `X-Unifocl-Token` request header.

Endpoint list:

- `POST /agent/exec` — structured ExecV2 command dispatch (see schema below)
- `GET /agent/capabilities` — returns supported operations and risk levels
- `GET /agent/status?requestId=<id>` — poll approval or execution status by request ID
- `GET /agent/dump/{hierarchy|project|inspector}?format=json|yaml` — deterministic state dump

**ExecV2 request schema** (`POST /agent/exec`):

```json
{
  "operation": "asset.rename",
  "requestId": "req-001",
  "args": {
    "assetPath": "Assets/Scripts/OldName.cs",
    "newAssetPath": "Assets/Scripts/NewName.cs"
  }
}
```

**ExecV2 response schema:**

```json
{
  "status": "Completed | Failed | Rejected | ApprovalRequired",
  "requestId": "req-001",
  "result": {},
  "error": "string (on failure)",
  "pendingApprovalToken": "string (when ApprovalRequired)"
}
```

**Supported operations:**

| Operation | Risk | Required args |
| --- | --- | --- |
| `asset.rename` | DestructiveWrite | `assetPath`, `newAssetPath` |
| `asset.remove` | DestructiveWrite | `assetPath` |
| `asset.create` | SafeWrite | `assetPath`, `content?` |
| `asset.create_script` | SafeWrite | `assetPath`, `content?` |
| `build.run` | PrivilegedExec | _(none)_ |
| `build.exec` | PrivilegedExec | `method` |
| `build.scenes.set` | SafeWrite | `scenes` (array of paths) |
| `upm.remove` | DestructiveWrite | `packageId` |
| `prefab.create` | SafeWrite | `nodeSelector`, `assetPath` |
| `prefab.apply` | SafeWrite | `nodeSelector` |
| `prefab.revert` | SafeWrite | `nodeSelector` |
| `prefab.unpack` | DestructiveWrite | `nodeSelector`, `completely?` |
| `prefab.variant` | SafeWrite | `sourcePath`, `newPath` |
| `eval.run` | PrivilegedExec | `code`, `declarations?`, `timeoutMs?` |
| `hierarchy.snapshot` | SafeRead | _(none)_ |
| `session.open` | SafeRead | _(none)_ |
| `session.close` | SafeRead | _(none)_ |
| `session.status` | SafeRead | _(none)_ |

`DestructiveWrite` and `PrivilegedExec` operations return `ApprovalRequired` on first call. Re-send the same request with `"intent": {"approvalToken": "<token>"}` to confirm execution.

**Approval confirmation example:**

```json
{
  "operation": "asset.rename",
  "requestId": "req-001",
  "args": { "assetPath": "Assets/Old.cs", "newAssetPath": "Assets/New.cs" },
  "intent": { "approvalToken": "<token-from-ApprovalRequired-response>" }
}
```

The daemon-side agent endpoint routes through the typed ExecV2 operation router — free-form `commandText` execution has been removed.

### 6. Error Taxonomy

| **Error Code** | **Meaning** |
| --- | --- |
| `E_PARSE` | Command parse/payload syntax failure |
| `E_MODE_INVALID` | Command cannot run in current context |
| `E_NOT_FOUND` | Requested object/asset/component not found |
| `E_TIMEOUT` | Operation timed out |
| `E_UNITY_API` | Daemon/bridge Unity execution path failure |
| `E_VCS_SETUP_REQUIRED` | Mutation blocked until interactive UVCS setup is completed |
| `E_ESCALATION_REQUIRED` | Command likely blocked by sandbox/network and needs elevated rerun |
| `E_VALIDATION` | Semantic validation failed |
| `E_INTERNAL` | Unhandled runtime error |

### 7. Capability Discovery and OpenAPI

Runtime capability discovery:

```
unifocl exec "/protocol" --agentic --format json
# HTTP (requires --unsafe-http daemon flag and token header):
curl -H "X-Unifocl-Token: $(cat ~/.unifocl-runtime/http-token-8080.txt)" \
  "http://127.0.0.1:8080/agent/capabilities"
```

Static OpenAPI contract:

- `docs/openapi-agentic.yaml`

### 8. Concurrent Worktree Integration (Parallel Agents)

Agentic mode is designed to run safely across multiple autonomous agents by isolating each agent in its own worktree and daemon port.

Use the built-in orchestration scripts:

- Bash: `src/unifocl/scripts/agent-worktree.sh`
- PowerShell: `src/unifocl/scripts/agent-worktree.ps1`

Recommended flow (bash example):

```
# 1) Provision isolated worktree + branch from origin/main
src/unifocl/scripts/agent-worktree.sh provision \
  --repo-root . \
  --worktree-path ../unifocl-agent-a \
  --branch codex/agent-a

# 2) Scaffold a minimal Unity project for agentic smoke tests
src/unifocl/scripts/agent-worktree.sh setup-smoke-project \
  --worktree-path ../unifocl-agent-a \
  --project-path .local/agentic-smoke-project

# 3) Run bridge init via one-shot agentic execution (no interactive shell)
src/unifocl/scripts/agent-worktree.sh init-smoke-agentic \
  --worktree-path ../unifocl-agent-a \
  --project-path .local/agentic-smoke-project \
  --format json

# 4) Open project (provisions/attaches daemon via /open)
dotnet run --project src/unifocl/unifocl.csproj -- \
  exec "/open $(pwd)/../unifocl-agent-a/.local/agentic-smoke-project" \
  --agentic --project "$(pwd)/../unifocl-agent-a/.local/agentic-smoke-project" --mode project

# 5) Execute deterministic machine command in that isolated workspace
cd ../unifocl-agent-a
dotnet run --project src/unifocl/unifocl.csproj -- \
  exec "/dump project --format json --depth 2 --limit 2000" \
  --agentic --project "$(pwd)/.local/agentic-smoke-project" --mode project
```

Concurrency safeguards:

- one agent = one branch + one worktree.
- one worktree = one daemon port.
- never let multiple agents mutate the same worktree concurrently.
- tear down completed worktrees via script (`teardown`) or `git worktree remove --force`.

## Architecture & Core Systems

### Application Architecture

unifocl is a .NET console application built for cross-platform compatibility (Windows, macOS, Linux). The architecture seamlessly supports both the human CLI and the agentic machine interfaces. It is divided into four primary layers:

1. **CLI Layer:** Handles commands, Spectre.Console human interactions, and stateless agentic routing.
2. **Mode System:** Manages the context-aware environments (Hierarchy, Project, Inspector).
3. **Daemon Layer:** A persistent background coordinator that tracks project state and serves as the backend API.
4. **Bridge Mode Channel:** The communication interface between the daemon and an active Unity Editor/runtime.

### The unifocl Daemon

The daemon is a localhost control process, not a kernel/OS-level file mutation service.

Current implementation summary:

- **External transport (CLI ↔ unifocl daemon):** The CLI and agents communicate with the unifocl daemon over a **Unix Domain Socket** (`~/.unifocl-runtime/daemon-{port}.sock`, chmod 0600) by default. An HTTP listener on `127.0.0.1:<port>` is only started when the daemon is launched with `--unsafe-http`, and requires the `X-Unifocl-Token` header (token written to `~/.unifocl-runtime/http-token-{port}.txt` at startup, chmod 0600). Request body size is capped at 1 MB on the HTTP path.
- **Internal transport (unifocl daemon ↔ Unity Editor):** The unifocl daemon communicates internally with the Unity Editor-side bridge (`CLIDaemon`) over a separate localhost HTTP channel — this is always local-only and not exposed to agents.
- The daemon keeps a project-scoped session warm so commands do not need to cold-start Unity every time.
- Mode selection is runtime-based:
    - **Host mode:** If no suitable GUI editor bridge is attached, unifocl starts Unity in batch/no-graphics mode (`headless`) and serves commands through that Unity process.
    - **Bridge mode:** If a GUI Unity editor for the same project is already active and attachable, unifocl routes commands to that live editor bridge endpoint.
- Project operations are executed by Unity-side services/contracts (`CLIDaemon`/`DaemonProjectService`), then reported back to the CLI/Agent as typed ExecV2 responses.
- If an endpoint is reachable but unhealthy (for example ping works but project commands do not), unifocl restarts and re-attaches the managed daemon path.
- Daemon state is tracked per project (deterministic port + local `.unifocl` config/session metadata).

What this means in practice:

- unifocl does not bypass Unity with privileged OS hooks.
- It either executes through a Host-mode Unity runtime or through a Bridge-mode attached editor runtime, depending on what is available.

### Persistence Safety Contract

unifocl enforces a mutation safety contract across `hierarchy`, `inspector`, and `project` modes, crucial for safe autonomous agent execution and human user error prevention. The implementation is split into four layers.

### 1. Transactional Envelope (Daemon Core)

All mutating requests carry a required `MutationIntent` envelope before Unity API or filesystem execution.

Current envelope fields:

- `transactionId`
- `target`
- `property`
- `oldValue`
- `newValue`
- `flags.dryRun`
- `flags.requireRollback` (must be `true`)
- `flags.vcsMode` (optional: `uvcs_all` or `uvcs_hybrid_gitignore`)
- `flags.vcsOwnedPaths[]` (optional per-path owner metadata used for checkout policy)

Daemon-side validation is centralized in `DaemonMutationTransactionCoordinator` and rejects mutation requests that are missing or invalid. Valid intents are routed to a deterministic safety handler by mode:

- `hierarchy` / `inspector` -> `memory`
- `project` -> `filesystem`

Each mutation entrypoint returns a unified transaction decision envelope (`success|error`) before command execution continues.

### 2. Memory Layer Safety (Hierarchy & Inspector)

Inspector and hierarchy property writes are routed through Unity serialized APIs and guarded for idempotency:

- Mutations use `SerializedObject` / `SerializedProperty`.
- Read-before-write checks skip no-op writes.
- `Undo.RecordObject(...)` + `ApplyModifiedProperties()` execute only when values actually change.

Lifecycle and multi-step memory mutations are wrapped in Undo boundaries:

- Creates use `Undo.RegisterCreatedObjectUndo(...)`.
- Deletes use `Undo.DestroyObjectImmediate(...)`.
- Multi-step operations use grouped Undo with `Undo.CollapseUndoOperations(groupId)` on success.
- Failures revert via `Undo.RevertAllDownToGroup(groupId)`.

Persistence hooks for scene/prefab integrity:

- Prefab instances are tracked with `PrefabUtility.RecordPrefabInstancePropertyModifications(...)`.
- Successful scene mutations mark and save through `EditorSceneManager.MarkSceneDirty(...)` and scene persistence services.
- Dry-run mode suppresses durable scene writes.

### 3. Filesystem Layer Safety (Project Mode)

Project-mode mutations that bypass Unity Undo are protected with transactional stashing and VCS-aware preflight:

- Before execution, UVCS-owned paths are preflighted for checkout (checkout-first policy; mutation fails if checkout is unavailable).
- Ownership mode is resolved per project:
    - `uvcs_all`: all mutation targets are treated as UVCS-owned.
    - `uvcs_hybrid_gitignore`: ownership is resolved from `.gitignore` rules at path level.
- Before execution, target assets and matching `.meta` files are shadow-copied into runtime stash storage under `$(UNIFOCL_PROJECT_STASH_ROOT || <temp>/unifocl-stash)/<project-hash>/...`.
- On success, stash contents are removed (commit path).
- On failure or exception, the stash is restored and cleanup targets are removed, then `AssetDatabase.Refresh(ForceUpdate)` is called to re-sync Unity state.

Unity Version Control (formerly Plastic SCM) behavior:

- UVCS uses checkout semantics, so writable filesystem state alone is not treated as authority for safe mutation.
- unifocl resolves ownership per target path, then performs checkout preflight before any file mutation is attempted.
- Paths classified as UVCS-owned must pass checkout preflight first; otherwise the mutation is rejected before file I/O begins.
- In `uvcs_hybrid_gitignore` mode, `.gitignore` is used as a pragmatic ownership split so UVCS checkout is enforced only for paths considered UVCS-owned.
- Dry-run includes ownership and checkout hints so automation can validate mutation viability before execution.

Interactive setup guard:

- When UVCS is auto-detected but unconfigured, the first project mutation prompts for one-time VCS setup and stores `.unifocl/vcs-config.json`.
- If setup is declined, mutation is aborted with actionable guidance.

Critical filesystem mutation sections are serialized with `SemaphoreSlim` to avoid concurrent race conditions during stash/restore and mutation execution.

### 4. Dry-Run & Preview Mechanics

Dry-run behavior is wired end-to-end from CLI parsing to daemon execution and agentic responses.

Memory dry-run (`hierarchy` / `inspector`):

- Snapshot pre-mutation state with `EditorJsonUtility.ToJson(...)`.
- Execute mutation inside an Undo group.
- Snapshot post-mutation state.
- Immediately revert with Undo.
- Return a structured unified diff payload.

Filesystem dry-run (`project`):

- No `System.IO` mutation occurs.
- Daemon returns proposed path and metadata changes (including `.meta` side effects), plus ownership/checkout hints for each path change.

CLI / agentic integration:

- Interactive outputs append unified dry-run diff lines in Spectre command logs.
- `agentic.v1` envelopes include optional `diff` payloads (`format`, `summary`, `lines`) for machine consumers.

## Development & Contributing

### Local Compatcheck Bootstrap

When you need to run Unity editor compatibility checks locally (especially after bridge/editor code changes), use:

```
./scripts/setup-compatcheck-local.sh
```

What this command does:

- Detects a local Unity editor install.
- Creates/bootstraps a benchmark Unity project under `.local/compatcheck-benchmark`.
- Writes local path settings to `local.config.json`.
- Runs: `dotnet build src/unifocl.unity.compatcheck/unifocl.unity.compatcheck.csproj --disable-build-servers -v minimal`

Local artifacts are intentionally uncommitted (`local.config.json`, `.local/`).

### Contributing & License

External contributions are accepted for version 0.3.0 and later.

Unless explicitly stated otherwise, any Contribution intentionally submitted for inclusion in version 0.3.0 and later is licensed under the Apache License 2.0.

Apache License 2.0 applies to version 0.3.0 and all later versions.

All content before version 0.3.0 is proprietary and all rights reserved.
