# unifocl

A terminal-first Unity development companion. **unifocl** provides a structured way to interact with and navigate your Unity projects directly from the command line.

It is not designed to replace the Unity Editor. Instead, it serves as a supplementary tool for developers who prefer managing project structure, assets, and hierarchies via a CLI or TUI (Terminal User Interface).

## Features

Instead of relying solely on the Unity Editor's graphical interface, unifocl offers:
* **Mode-based navigation:** Context-aware environments for navigating the Hierarchy, Project, and Inspector.
* **Deterministic manipulation:** Command-driven file and object operations.
* **Focused interface:** A clean CLI/TUI experience built with Spectre.Console.

## Architecture


unifocl is a .NET console application built for cross-platform compatibility (Windows, macOS, Linux). The application is divided into four primary layers:

1.  **CLI Layer:** Handles commands and structured user interaction.
2.  **Mode System:** Manages the context-aware environments (Hierarchy, Project, Inspector).
3.  **Daemon Layer:** A persistent background coordinator that tracks project state.
4.  **Bridge Mode Channel:** The communication interface between the daemon and an active Unity Editor/runtime.

## The unifocl Daemon

The daemon is a localhost control process, not a kernel/OS-level file mutation service.

Current implementation summary:

* The CLI talks to a daemon endpoint over local HTTP (`127.0.0.1:<port>`) for lifecycle and project commands.
* The daemon keeps a project-scoped session warm so commands do not need to cold-start Unity every time.
* Mode selection is runtime-based:
  * **Host mode:** If no suitable GUI editor bridge is attached, unifocl starts Unity in batch/no-graphics mode (`--headless`) and serves commands through that Unity process.
  * **Bridge mode:** If a GUI Unity editor for the same project is already active and attachable, unifocl routes commands to that live editor bridge endpoint.
* Project operations are executed by Unity-side services/contracts, then reported back to the CLI as typed responses.
* If an endpoint is reachable but unhealthy (for example ping works but project commands do not), unifocl restarts and re-attaches the managed daemon path.
* Daemon state is tracked per project (deterministic port + local `.unifocl` config/session metadata).

What this means in practice:

* unifocl does not bypass Unity with privileged OS hooks.
* It either executes through a Host-mode Unity runtime or through a Bridge-mode attached editor runtime, depending on what is available.

## Installation

unifocl is currently distributed as source code and requires a modern .NET runtime. Future distribution methods (like a .NET Global Tool, Homebrew, or Winget) are planned but not yet implemented.

### Clone & Build (Debug)

```bash
git clone https://github.com/Kiankinakomochi/unifocl.git
cd unifocl
dotnet build
dotnet run --project src/unifocl
```

`Debug build output is located in:` `src/unifocl/bin/Debug/`

### Release Build

```bash
dotnet build -c Release
```

*Release output is located in:* `src/unifocl/bin/Release/`. You can run the generated binary directly from this directory.

---

## Local Compatcheck Bootstrap

When you need to run Unity editor compatibility checks locally (especially after bridge/editor code changes), use:

```bash
./scripts/setup-compatcheck-local.sh
```

What this command does:
* Detects a local Unity editor install.
* Creates/bootstraps a benchmark Unity project under `.local/compatcheck-benchmark`.
* Writes local path settings to `local.config.json`.
* Runs:
  * `dotnet build src/unifocl.unity.compatcheck/unifocl.unity.compatcheck.csproj --disable-build-servers -v minimal`

Local artifacts are intentionally uncommitted (`local.config.json`, `.local/`).

---

## Agentic Mode (Machine-Oriented Workflows)

unifocl supports an **agentic execution path** for LLMs, automations, and tool wrappers that need deterministic I/O instead of interactive TUI behavior.

Core principles:
* Structured response envelope for every command.
* No Spectre/TUI rendering in agentic one-shot mode.
* Standardized error taxonomy and process exit codes.
* Explicit state serialization commands for context hydration.

### 1. One-Shot CLI for Agents

Use `exec` to run a single command and exit:

```bash
unifocl exec "<command>" [--agentic] [--format json|yaml] [--project <path>] [--mode <project|hierarchy|inspector>] [--attach-port <port>] [--request-id <id>]
```

Examples:

```bash
unifocl exec "/version" --agentic --format json
unifocl exec "/protocol" --agentic --format yaml
unifocl exec "/dump project --format json --depth 2 --limit 5000" --agentic --project /path/to/UnityProject
unifocl exec "upm list --outdated" --agentic --project /path/to/UnityProject --mode project
```

Notes:
* `--agentic` enables machine output (single response payload).
* `--format` controls payload encoding (`json` or `yaml`).
* `--project`, `--mode`, and `--attach-port` seed runtime context so commands can execute without interactive setup.

### 2. Unified Agentic Envelope

`--agentic` responses use one schema:

```json
{
  "status": "success|error",
  "requestId": "string",
  "mode": "project|hierarchy|inspector|none",
  "action": "string",
  "data": {},
  "errors": [{ "code": "E_*", "message": "string", "hint": "string|null" }],
  "warnings": [{ "code": "W_*", "message": "string" }],
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
* `status`: high-level outcome (`success` or `error`).
* `requestId`: caller-supplied correlation id (or generated if omitted).
* `mode`: effective runtime context after command execution.
* `action`: normalized command family (e.g. `version`, `dump`, `upm`).
* `data`: command payload (shape varies by action).
* `errors`: deterministic machine errors (empty on success).
* `warnings`: non-fatal issues.
* `meta`: schema/protocol/exit metadata plus optional command-specific extras.

### 3. Agentic Exit Codes

| Exit Code | Meaning |
| :--- | :--- |
| `0` | Success |
| `2` | Validation / parse / context-state error |
| `3` | Daemon/bridge availability or timeout class failure |
| `4` | Internal execution error |

### 4. `/dump` State Serialization

`/dump` is designed for context-window transfer and deterministic snapshots:

```bash
/dump <hierarchy|project|inspector> [--format json|yaml] [--compact] [--depth n] [--limit n]
```

Current behavior:
* `hierarchy`: fetches hierarchy snapshot from attached daemon.
* `project`: serializes deterministic `Assets` tree entries.
* `inspector`: serializes inspector components/fields from attached bridge path.

Context handling:
* If required runtime state is missing (for example no attached daemon for `hierarchy`), response returns `E_MODE_INVALID` with a corrective hint.
* Unsupported category returns `E_VALIDATION`.

### 5. Daemon Agentic HTTP Endpoints

Daemon service mode exposes agent endpoints on localhost:
* `POST /agent/exec`
* `GET /agent/capabilities`
* `GET /agent/status?requestId=...`
* `GET /agent/dump/{hierarchy|project|inspector}?format=json|yaml`

Example:

```bash
curl -X POST "http://127.0.0.1:8080/agent/exec" \
  -H "Content-Type: application/json" \
  -d '{
    "commandText": "/version",
    "contextMode": "project",
    "sessionSeed": "",
    "outputMode": "json",
    "requestId": "req-001"
  }'
```

The daemon-side agent endpoint delegates to the same `exec --agentic` pathway so CLI and HTTP machine outputs remain contract-consistent.

### 6. Error Taxonomy

| Error Code | Meaning |
| :--- | :--- |
| `E_PARSE` | Command parse/payload syntax failure |
| `E_MODE_INVALID` | Command cannot run in current context |
| `E_NOT_FOUND` | Requested object/asset/component not found |
| `E_TIMEOUT` | Operation timed out |
| `E_UNITY_API` | Daemon/bridge Unity execution path failure |
| `E_VALIDATION` | Semantic validation failed |
| `E_INTERNAL` | Unhandled runtime error |

### 7. Capability Discovery and OpenAPI

Runtime capability discovery:

```bash
unifocl exec "/protocol" --agentic --format json
curl "http://127.0.0.1:8080/agent/capabilities"
```

Static OpenAPI contract:
* `docs/openapi-agentic.yaml`

### 8. Concurrent Worktree Integration (Parallel Agents)

Agentic mode is designed to run safely across multiple autonomous agents by isolating each agent in its own worktree and daemon port.

Use the built-in orchestration scripts:
* Bash: `src/unifocl/scripts/agent-worktree.sh`
* PowerShell: `src/unifocl/scripts/agent-worktree.ps1`

Recommended flow (bash example):

```bash
# 1) Provision isolated worktree + branch from origin/main
src/unifocl/scripts/agent-worktree.sh provision \
  --repo-root . \
  --worktree-path ../unifocl-agent-a \
  --branch codex/agent-a

# 2) Start daemon on dynamically selected open port for that worktree/project
src/unifocl/scripts/agent-worktree.sh start-daemon \
  --worktree-path ../unifocl-agent-a \
  --project-path ../unifocl-agent-a

# 3) Execute deterministic machine command in that isolated workspace
cd ../unifocl-agent-a
dotnet run --project src/unifocl/unifocl.csproj -- \
  exec "/dump project --format json --depth 2 --limit 2000" \
  --agentic --project "$(pwd)" --mode project
```

Concurrency safeguards:
* one agent = one branch + one worktree.
* one worktree = one daemon port.
* never let multiple agents mutate the same worktree concurrently.
* tear down completed worktrees via script (`teardown`) or `git worktree remove --force`.

## Command & Feature Guide

When you launch unifocl, you will be greeted by a boot screen. From here, the CLI operates as an interactive shell using **slash commands** (e.g., `/open`) for system and lifecycle operations, and **standard commands** (e.g., `ls`, `cd`) for contextual project operations.

### 1. System & Lifecycle Commands
These commands manage your session, project loading, and CLI configuration. They are prefixed with a slash (`/`).

| Command | Alias | Description |
| :--- | :--- | :--- |
| `/open <path> [--allow-unsafe]` | `/o` | Open a Unity project. Starts/attaches to the daemon and loads metadata. |
| `/close` | `/c` | Detach from the current project and stop the attached daemon. |
| `/quit` | `/q`, `/exit` | Exit the CLI client (leaves the daemon running). |
| `/new <name> [version]` | | Bootstrap a new Unity project. |
| `/clone <git-url>` | | Clone a repository and set up local CLI bridge-mode config. |
| `/recent [idx]` | | List recent projects or open one by index. |
| `/config <get/set/list/reset>`| `/cfg` | Manage CLI preferences (e.g., themes). |
| `/status` | `/st` | Show daemon, mode, editor, project, and session status summary. |
| `/build run [target] [--dev] [--debug] [--clean] [--path <output-path>]` | `/b` | Trigger a Unity player build. If target is omitted, choose from an interactive target selector. |
| `/build exec <Method>` | `/bx` | Execute a static build method (e.g., `CI.Builder.BuildAndroidProd`). |
| `/build scenes` | | Open an interactive TUI to view, toggle, and reorder build scenes. |
| `/build addressables [--clean] [--update]` | `/ba` | Trigger an Addressables content build (full or update mode). |
| `/build cancel` | | Request cancellation for the active build process via daemon. |
| `/build targets` | | List platform build support currently available in this Unity Editor. |
| `/build logs` | | Reopen live build log tail (restartable, with error filtering). |
| `/init [path]` | | Generate bridge-mode config and install editor-side dependencies. |
| `/clear` | | Clear and redraw the boot screen and log. |
| `/help [topic]` | `/?` | Show help by topic. |

> **Note:** Additional diagnostic commands (`/doctor`, `/logs`, `/scan`, `/info`, `/unity detect`) are available but may be in active development.

### UPM Management Stability Note

UPM commands in CLI mode (`upm list/install/remove/update`) are currently under active stabilization.  
For critical package operations, the recommended workflow is still Unity Editor GUI (Package Manager window), then use unifocl for verification and follow-up automation.

### 2. Daemon Management
The daemon maintains a persistent connection to the project. Manage it using the `/daemon` (or `/d`) command suite.

| Subcommand | Description |
| :--- | :--- |
| `start` | Start a daemon. Accepts flags: `--port`, `--unity <path>`, `--project <path>`, `--headless` (Host mode), `--allow-unsafe`. |
| `stop` | Stop the daemon instance controlled by this CLI. |
| `restart` | Restart the currently attached daemon. |
| `ps` | List running daemon instances, ports, uptimes, and associated projects. |
| `attach <port>` | Attach the CLI to an existing daemon at the specified port. |
| `detach` | Detach the CLI but keep the daemon alive in the background. |

### AI Agent Worktree Orchestration

For concurrent autonomous agents, provision isolated git worktrees and run daemon boot per worktree with dynamic port mapping.

- Bash workflow: `src/unifocl/scripts/agent-worktree.sh`
- PowerShell workflow: `src/unifocl/scripts/agent-worktree.ps1`

Example (bash):

```bash
src/unifocl/scripts/agent-worktree.sh provision \
  --repo-root . \
  --worktree-path ../unifocl-agent-a \
  --branch codex/agent-a \
  --source-project . \
  --seed-library

src/unifocl/scripts/agent-worktree.sh start-daemon \
  --worktree-path ../unifocl-agent-a \
  --project-path ../unifocl-agent-a
```

Lifecycle pipeline and operating boundaries:

1. Initialize
- Provision a dedicated branch worktree with `git worktree add <path> -b <agent-branch> origin/main`.
- Never share one mutable branch/worktree across agents.

2. Seed
- Copy warmed Unity cache before daemon boot:
  - macOS/Linux: `cp -a <MainProject>/Library <Worktree>/Library`
  - PowerShell: `Copy-Item <MainProject>\Library <Worktree>\Library -Recurse`

3. Boot Daemon
- Allocate an open localhost port dynamically.
- Start from inside the provisioned worktree:
  - `unifocl /daemon start --project <path> --port <dynamic-port> --headless`
- Validate readiness via `http://127.0.0.1:<dynamic-port>/ping`.

4. Execute
- Run automation only after daemon health is confirmed.
- Keep all mutations scoped to the provisioned worktree.

5. Commit/Push
- Commit only agent-branch changes and push for review.

6. Teardown
- Stop worktree daemon, then run `git worktree remove --force <path>` and `git worktree prune`.

Operating boundaries:
- No cross-worktree file edits.
- No port reuse assumptions across concurrent agents.
- No shared mutable daemon state across projects.
- Teardown is mandatory after each completed run.

### GitHub Milestone Tracking

Track this stream in GitHub Milestones under: `Worktree Isolation and Multi-Agent Daemon Safety`.

Current progress:
- [x] Step 1: Git worktree provisioning + teardown script
- [x] Step 2: Library cache seeding strategy
- [x] Step 3: Dynamic daemon port assignment + readiness check
- [x] Step 4: Agent lifecycle orchestration documentation

### 3. Mode Switching
Once a project is opened, use these commands to switch your active context.

| Command | Alias | Description |
| :--- | :--- | :--- |
| `/project` | `/p` | Switch to Project mode (asset structure navigation). |
| `/hierarchy` | `/h` | Switch to Hierarchy mode (scene structure TUI). |
| `/inspect <idx/path>`| `/i` | Switch to Inspector mode and focus a target. |

### 4. Contextual Operations (Non-Slash Commands)
When inside a specific mode (Project, Hierarchy, or Inspector), omit the slash to interact directly with the active environment. Mutating operations are safely routed through Bridge mode when available, or Host mode fallback when applicable.

| Command | Alias | Description |
| :--- | :--- | :--- |
| `list` | `ls` | List entries in the current active context. |
| `enter <idx>` | `cd` | Enter the selected node, folder, or component by index. |
| `up` | `..` | Navigate up one level to the parent. |
| `make <type> <name>`| `mk` | Create an item (e.g., `mk script Player`, `mk gameobject`). |
| `load <idx/name>` | | Load/open a scene, prefab, or script. |
| `remove <idx>` | `rm` | Remove the selected item. |
| `rename <idx> <new>`| `rn` | Rename the selected item. |
| `set <field> <val>` | `s` | Set a field or property value. |
| `toggle <target>` | `t` | Toggle boolean/active/enabled flags. |
| `move <...>` | `mv` | Move, reparent, or reorder an item. |

### 5. Fuzzy Search & Intellisense
unifocl features a composer with Intellisense. 
* Type `/` to open the slash-command suggestion palette.
* Type any standard text to receive project-mode suggestions.

**Fuzzy Finding:**
Use the `f` or `ff` command to trigger fuzzy search across your project or inspector. In project mode, you can scope searches using `--type`/`-t` or `t:<type>`.
* **Syntax:** `f [--type <type>|-t <type>|t:<type>] <query>`
* **Supported Types:** `script`, `scene`, `prefab`, `material`, `animation`
* **Example:** `f --type script PlayerController`

### 6. Keybindings & Focus Modes
The CLI provides keyboard-driven navigation for interacting with lists and structures without typing out indices.

**Global Keybinds**
* **`F7`**: Toggle focus for Hierarchy TUI, Project navigator, Recent projects list, and Inspector.
* **`Esc`**: Dismiss Intellisense, or clear input if already dismissed.
* **`↑` / `↓`**: Navigate fuzzy/Intellisense candidates.
* **`Enter`**: Insert selected suggestion or commit input.

**Context-Specific Focus Navigation**
Once focused (`F7`), the arrow keys and tab behave contextually:

| Action | Hierarchy Focus | Project Focus | Inspector Focus |
| :--- | :--- | :--- | :--- |
| **`↑` / `↓`** | Move highlighted GameObject | Move highlighted file/folder | Move highlighted component/field |
| **`Tab`** | Expand selected node | Reveal/open selected entry | Inspect selected component |
| **`Shift+Tab`**| Collapse selected node | Move to parent folder | Back to component list |
| **Exit Focus** | `Esc` or `F7` | `Esc` or `F7` | `Esc` or `F7` |

---

## Roadmap

Current development priorities include:
* Daemon stabilization and Host/Bridge mode hardening.
* Command syntax stabilization.
* Packaging and distribution (Global Tool, Homebrew).
* Implementation of a script execution layer.

## Contributing & License

External contributions are accepted for version 0.3.0 and later.
Unless explicitly stated otherwise, any Contribution intentionally submitted for inclusion in version 0.3.0 and later is licensed under the Apache License 2.0.

Apache License 2.0 applies to version 0.3.0 and all later versions.
All content before version 0.3.0 is proprietary and all rights reserved.
