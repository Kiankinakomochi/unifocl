# Command & TUI Reference

Full reference of all unifocl commands, keybindings, dry-run mechanics, and dynamic C# eval.

unifocl operates through a unified command set. Humans can launch the interactive shell (boot screen) to use **slash commands** (e.g., `/open`) for lifecycle operations and **standard commands** (e.g., `ls`, `cd`) for contextual actions. AI agents access these exact same commands via the stateless `exec` pathway or the built-in MCP server.

## 1. System & Lifecycle Commands

These commands manage your session, project loading, and configuration. In the interactive shell, they are prefixed with a slash (`/`).

| **Command** | **Alias** | **Description** |
| --- | --- | --- |
| `/open <path> [--allow-unsafe]` | `/o` | Open a Unity project. Starts/attaches to the daemon and loads metadata. |
| `/close` | `/c` | Detach from the current project and stop the attached daemon. |
| `/quit` | `/q`, `/exit` | Exit the CLI client (leaves the daemon running). |
| `/daemon <start\|stop\|restart\|ps\|attach\|detach>` | `/d` | Manage daemon lifecycle commands. |
| `/new <name> [version]` |  | Bootstrap a new Unity project. |
| `/clone <git-url>` |  | Clone a repository and set up local CLI bridge-mode config. |
| `/recent [idx]` |  | List recent projects or open one by index. |
| `/config <get/set/list/reset>` | `/cfg` | Manage CLI preferences (e.g., themes). |
| `/status` | `/st` | Show daemon, mode, editor, project, and session status summary. |
| `/doctor` |  | Run environment and tooling diagnostics. |
| `/scan [--root <dir>] [--depth <n>]` |  | Scan directories for Unity projects. |
| `/info <path?>` |  | Inspect Unity project metadata and protocol details. |
| `/logs [daemon\|unity] [-f]` |  | Show daemon runtime summary or follow logs. |
| `/examples` |  | Show common operational command flows. |
| `/update` |  | Show installed CLI version and update guidance. |
| `/install-hook` |  | Run bridge dependency install flow (`/init`) against current/open project. |
| `/agent install <codex\|claude> [--workspace <path>] [--server-name <name>] [--config-root <path>] [--dry-run]` |  | Install/update MCP integration for Codex or Claude. |
| `/unity detect` |  | List installed Unity editors. |
| `/unity set <path>` |  | Set default Unity editor path. |
| `/build run [target] [--dev] [--debug] [--clean] [--path <output-path>]` | `/b` | Trigger a Unity player build. If target is omitted, choose from an interactive target selector. |
| `/build exec <Method>` | `/bx` | Execute a static build method (e.g., `CI.Builder.BuildAndroidProd`). |
| `/build scenes` |  | Open an interactive TUI to view, toggle, and reorder build scenes. |
| `/build addressables [--clean] [--update]` | `/ba` | Trigger an Addressables content build (full or update mode). |
| `/build cancel` |  | Request cancellation for the active build process via daemon. |
| `/build targets` |  | List platform build support currently available in this Unity Editor. |
| `/build logs` |  | Reopen live build log tail (restartable, with error filtering). |
| `/build snapshot-packages` |  | Snapshot `Packages/manifest.json` to a timestamped file under `.unifocl-runtime/snapshots/`. |
| `/build preflight` |  | Run scene-list + build-settings + packages validators sequentially and report aggregated pass/fail before a build. |
| `/build artifact-metadata` |  | Show file list, sizes, and target from the last captured build report. |
| `/build failure-classify` |  | Classify errors from the last build into CompileError / LinkerError / MissingAsset / ScriptError / Timeout categories. |
| `/build report` |  | Render a consolidated build summary: preflight + artifacts + classified failures. |
| `/upm` |  | Show Unity Package Manager command usage and options. |
| `/upm list [--outdated] [--builtin] [--git]` | `/upm ls` | List installed Unity packages (with optional outdated/builtin/git filters). |
| `/upm install <target>` | `/upm add`, `/upm i` | Install a package by package ID, Git URL, or `file:` target. |
| `/upm remove <id>` | `/upm rm`, `/upm uninstall` | Remove a package by package ID. |
| `/upm update <id> [version]` | `/upm u` | Update a package to latest or a specified version. |
| `/prefab create <idx\|name> <asset-path>` |  | Convert a scene GameObject into a new Prefab Asset on disk. |
| `/prefab apply <idx>` |  | Push instance overrides back to the source Prefab Asset. |
| `/prefab revert <idx>` |  | Discard local overrides, revert to the source Prefab Asset. |
| `/prefab unpack <idx> [--completely]` |  | Break the prefab connection, turning the instance into a regular GameObject. |
| `/prefab variant <source-path> <new-path>` |  | Create a Prefab Variant inheriting from a base prefab. |
| `/animator param add <asset-path> <name> <type>` |  | Add a parameter to an AnimatorController. `<type>` must be `float`, `int`, `bool`, or `trigger`. (SafeWrite) |
| `/animator param remove <asset-path> <name>` |  | Remove an existing parameter from an AnimatorController by name. (DestructiveWrite) |
| `/animator state add <asset-path> <name> [--layer <n>]` |  | Add a new state to the target layer's root state machine (layer 0 by default). (SafeWrite) |
| `/animator transition add <asset-path> <from-state> <to-state> [--layer <n>]` |  | Create a transition between two states in the specified layer. Use `AnyState` as `<from-state>` to route from the Any State. (SafeWrite) |
| `/clip config <asset-path> [--loop-time <bool>] [--loop-pose <bool>]` |  | Modify loop settings of an AnimationClip (`loopTime` / `loopPose`). At least one flag required. (SafeWrite) |
| `/clip event add <asset-path> <time> <function-name> [--string <val>\|--float <val>\|--int <val>]` |  | Insert an `AnimationEvent` at the specified time (seconds). Optionally set one parameter value. (SafeWrite) |
| `/clip event clear <asset-path>` |  | Remove all animation events from a clip. (DestructiveWrite) |
| `/clip curve clear <asset-path>` |  | Remove all property curves and keyframes from a clip. (DestructiveWrite) |
| `/tag <list\|add\|remove>` |  | Manage Unity project tags (built-in and custom). |
| `/tag list` | `/tag ls` | List all tags (built-in and custom). |
| `/tag add <name>` | `/tag a` | Add a new custom tag. Fails if it already exists. |
| `/tag remove <name>` | `/tag rm` | Remove a custom tag. Fails if the tag is a built-in (e.g., Untagged, Player). |
| `/layer <list\|add\|rename\|remove>` |  | Manage Unity project layers (indices 0–31). |
| `/layer list` | `/layer ls` | List all layers with their index and name. |
| `/layer add <name> [--index <idx>]` | `/layer a` | Add a layer. Finds the first empty user slot (8–31) unless `--index` is specified. |
| `/layer rename <old-name\|index> <new-name>` | `/layer rn` | Rename a user layer. Fails for built-in layers 0–7. |
| `/layer remove <name\|index>` | `/layer rm` | Clear a user layer slot. Fails for built-in layers 0–7. |
| `/asset rename <path> <new-name>` |  | Rename an asset at the given path. (DestructiveWrite) |
| `/asset remove <path>` |  | Delete an asset at the given path. (DestructiveWrite) |
| `/asset create <type> <path>` |  | Create a new asset of the given type at path. |
| `/asset create-script <name> <path>` |  | Create a new C# script at path. |
| `/asset describe <path> [--engine blip\|clip]` |  | Describe asset visually using a local BLIP/CLIP model. (SafeRead) See [§6](#6-asset-describe-local-vision). |
| `/build scenes set <json-array>` |  | Set the build scene list programmatically from a JSON array of paths. |
| `/init [path]` |  | Generate bridge-mode config and install editor-side dependencies. |
| `/keybinds` | `/shortcuts` | Show modal keybinds and shortcuts. |
| `/version` |  | Show CLI and protocol version. |
| `/protocol` |  | Show supported JSON schema capabilities. |
| `/dump <hierarchy\|project\|inspector> [--format json\|yaml] [--compact] [--depth n] [--limit n]` |  | Dump deterministic mode state for agentic workflows. |
| `/eval '<code>' [--declarations '<decl>'] [--timeout <ms>] [--dry-run]` | `/ev` | Evaluate arbitrary C# in the Unity Editor context (PrivilegedExec). |
| `/validate <sub>` | `/val` | Run project validation checks (`scene-list`, `missing-scripts`, `packages`, `build-settings`, `asmdef`, `asset-refs`, `addressables`, `scripts`, `all`). |
| `/test <sub>` |  | Run Unity tests via subprocess (`list`, `run editmode`, `run playmode`, `flaky-report`). No daemon required. |
| `/diag <sub>` |  | Run project diagnostics (`script-defines`, `compile-errors`, `assembly-graph`, `scene-deps`, `prefab-deps`, `asset-size`, `import-hotspots`, `all`). All ops are read-only and require the daemon. See [`project-diagnostics.md`](project-diagnostics.md). |
| `/playmode <start\|stop\|pause\|resume\|step>` |  | Control Unity Editor Play Mode. |
| `/playmode start` |  | Enter Play Mode. (PrivilegedExec) |
| `/playmode stop` |  | Exit Play Mode and restore edit-time state. (PrivilegedExec) |
| `/playmode pause` |  | Pause the active Play Mode session. (SafeWrite) |
| `/playmode resume` |  | Resume a paused Play Mode session. (SafeWrite) |
| `/playmode step` |  | Advance the game by exactly one frame. Only valid while paused. (SafeWrite) |
| `/clear` |  | Clear and redraw the boot screen and log. |
| `/help [topic]` | `/?` | Show help by topic (`root`, `project`, `inspector`, `build`, `upm`, `daemon`). |

**Behavior Notes & Protocol Hardening:**

- `/daemon` without a subcommand returns usage plus process summary.
- Unsupported slash-command routes return explicit `unsupported route` messaging.
- **Host-mode hierarchy fallback** is available when no GUI bridge is attached:
    - `HIERARCHY_GET` returns an `Assets` root snapshot.
    - `HIERARCHY_FIND` fuzzy-searches node names/paths.
    - `HIERARCHY_CMD` supports `mk`, `rm`, `rename`, `mv`, `toggle` with guardrails.
    - *Host-mode fallback safety constraints:* All mutations are constrained within `Assets`; move/rename path-escape is rejected; moving a directory into itself/descendants is rejected; `mk` validates names and supports typed placeholders (`Empty`, `EmptyChild`, `EmptyParent`, `Text/TMP`, `Sprite`, default prefab).
- Durable project mutations are supported (`submit -> status -> result`) so mutation outcomes remain queryable even if Unity refresh/compile/domain reload interrupts an in-flight HTTP response.
- Durable mutations use native daemon HTTP endpoints by default and no longer require the external Unity-MCP package/runtime dependencies.
- Built-in MCP server mode is available for automation tooling: start with `unifocl --mcp-server` (stdio transport, .NET MCP SDK).
- MCP command lookup tools are exposed by the built-in server so agents can discover usage without reading full docs:
    - `ListCommands(scope, query, limit)`
    - `LookupCommand(command, scope)`
- **Custom tool category tools** allow agents to discover and load user-defined `[UnifoclCommand]` methods on demand:
    - `get_categories()` — list available tool categories from the project manifest
    - `load_category(name)` — register a category's tools as live MCP tools (`tools/list_changed` is fired)
    - `unload_category(name)` — remove a category's tools from the active list
    - Full guide: [`custom-commands.md`](custom-commands.md)
- MCP server architecture + agent JSON configuration guide:
    - [`mcp-server-architecture.md`](mcp-server-architecture.md)
    - Quick multi-client setup helper: `scripts/setup-mcp-agents.sh`
- **Durable HTTP fallback endpoints:** `POST /project/mutation/submit`, `GET /project/mutation/status?requestId=<id>`, `GET /project/mutation/result?requestId=<id>`, `POST /project/mutation/cancel?requestId=<id>`

## 2. Daemon Management

The daemon acts as the persistent backend coordinator for both human operators and agentic workflows. Manage it using the `/daemon` (or `/d`) command suite.

| **Subcommand** | **Description** |
| --- | --- |
| `start` | Start a daemon. Accepts flags: `--port`, `--unity <path>`, `--project <path>`, `--headless` (Host mode), `--allow-unsafe`, `--unsafe-http` (enable HTTP listener in addition to UDS). |
| `stop` | Stop the daemon instance controlled by this CLI. |
| `restart` | Restart the currently attached daemon. |
| `ps` | List running daemon instances, ports, uptimes, and associated projects. |
| `attach <port>` | Attach the CLI to an existing daemon at the specified port. |
| `detach` | Detach the CLI but keep the daemon alive in the background. |

**Concurrent Autonomous Agents Notes:** For concurrent autonomous agents, provision isolated git worktrees and run daemon boot per worktree with dynamic port mapping. See [`agentic-workflow.md`](agentic-workflow.md) for full details.

## 3. Context & Mode Switching

Switch the active operational context for your session or agent execution.

| **Command** | **Alias** | **Description** |
| --- | --- | --- |
| `/project` | `/p` | Switch to Project mode (asset structure navigation). |
| `/hierarchy` | `/h` | Switch to Hierarchy mode (scene structure TUI/tree). |
| `/inspect <idx/path>` | `/i` | Switch to Inspector mode and focus a target. |

## 4. Contextual Operations (Non-Slash Commands)

Interact directly with the active environment. Mutating operations are safely routed through Bridge mode when available, or Host mode fallback when applicable, ensuring deterministic behavior for both humans and AI.

| **Command** | **Alias** | **Description** |
| --- | --- | --- |
| `list` | `ls` | List entries in the current active context. |
| `enter <idx>` | `cd` | Enter the selected node, folder, or component by index. |
| `up` | `..` | Navigate up one level to the parent. |
| `make <type> <name>` | `mk` | Create an item (e.g., `mk script Player`, `mk gameobject`). |
| `load <idx/name>` |  | Load/open a scene, prefab, or script. |
| `remove <idx>` | `rm` | Remove the selected item. |
| `rename <idx> <new>` | `rn` | Rename the selected item. |
| `set <field> <val>` | `s` | Set a field or property value. |
| `toggle <target>` | `t` | Toggle boolean/active/enabled flags. |
| `move <...>` | `mv` | Move, reparent, or reorder an item. |
| `f [--type <type>\|t:<type>] <query>` | `ff` | Run fuzzy find in the active mode. |
| `go find <query>` |  | Hierarchy-mode fuzzy find alias for `f`. |
| `go duplicate <idx> [name]` |  | Duplicate a hierarchy GameObject. |
| `asset find <query>` |  | Project-mode fuzzy find alias for `f`. |
| `asset duplicate <idx\|name> [new-path]` |  | Duplicate an asset in project mode. |
| `inspect [idx\|path]` |  | Enter inspector root target from inspector context. |
| `edit <field> <value...>` | `e` | Edit serialized field value for the selected component (inspector). |
| `component add <type>` | `comp add <type>` | Add a component to the inspected object. |
| `component find <query>` |  | Find components on the inspected object. |
| `component duplicate <index\|name>` |  | Duplicate a component on the inspected object. |
| `component remove <index\|name>` | `comp remove <index\|name>` | Remove a component from the inspected object. |
| `scroll [body\|stream] <up\|down> [count]` |  | Scroll inspector body or command stream. |
| `upm list [--outdated] [--builtin] [--git]` | `upm ls` | List installed Unity packages in project mode. |
| `upm install <target>` | `upm add`, `upm i` | Install package by ID, Git URL, or `file:` target in project mode. |
| `upm remove <id>` | `upm rm`, `upm uninstall` | Remove package by package ID in project mode. |
| `upm update <id> [version]` | `upm u` | Update package to latest or specified version in project mode. |
| `build run [target] [--dev] [--debug] [--clean] [--path <output-path>]` | `b` | Run Unity build in project mode. |
| `build exec <Method>` | `bx` | Execute static build method in project mode. |
| `build scenes` |  | Open scene build-settings TUI in project mode. |
| `build addressables [--clean] [--update]` | `ba` | Build Addressables content in project mode. |
| `build cancel` |  | Request cancellation for active build in project mode. |
| `build targets` |  | List Unity build support targets in project mode. |
| `build logs` |  | Open restartable build log tail in project mode. |
| `build snapshot-packages` |  | Snapshot package manifest to `.unifocl-runtime/snapshots/` in project mode. |
| `build preflight` |  | Run pre-build validation suite in project mode. |
| `build artifact-metadata` |  | Show last build artifact files and sizes in project mode. |
| `build failure-classify` |  | Classify last build errors by category in project mode. |
| `build report` |  | Consolidated build report in project mode. |
| `addressable init` |  | Create Addressables settings and default groups if missing. |
| `addressable profile list` |  | List all profiles and evaluated variables. |
| `addressable profile set <name>` |  | Set active Addressables profile. |
| `addressable group list` |  | List groups with packing/compression details. |
| `addressable group create <name> [--default]` |  | Create a group and optionally set it as default for new entries. |
| `addressable group remove <name>` |  | Remove a group and unmark contained entries safely. |
| `addressable entry add <asset-path> <group-name>` |  | Mark an asset as Addressable and place it in a group. |
| `addressable entry remove <asset-path>` |  | Remove Addressable flag from an asset entry. |
| `addressable entry rename <asset-path> <new-address>` |  | Change an entry's address key. |
| `addressable entry label <asset-path> <label> [--remove]` |  | Add/remove a label on a specific Addressable entry. |
| `addressable bulk add --folder <path> --group <name> [--type <T>]` |  | Add all matching assets in a folder to a group in one operation. |
| `addressable bulk label --folder <path> --label <name> [--type <T>] [--remove]` |  | Add/remove labels for matching folder assets in one operation. |
| `addressable analyze [--duplicate]` |  | Output structured Addressables analysis or duplicate dependency report. |
| `test list` |  | List all available edit-mode tests (name + assembly). No daemon required. |
| `test run editmode [--timeout <s>]` |  | Run all EditMode tests via Unity subprocess; returns structured JSON results. Default timeout 600s. |
| `test run playmode [--timeout <s>]` |  | Run all PlayMode tests via Unity subprocess. May trigger player build. Default timeout 1800s. |
| `diag script-defines` |  | Show scripting define symbols per build target group in project mode. |
| `diag compile-errors` |  | Show compiler messages from last compilation pass in project mode. |
| `diag assembly-graph` |  | Show asmdef-level assembly dependency graph in project mode. |
| `diag scene-deps` |  | Show transitive asset dependencies per enabled build scene in project mode. |
| `diag prefab-deps` |  | Show transitive asset dependencies per prefab (capped at 100) in project mode. |
| `prefab create <idx\|name> <asset-path>` |  | Convert scene GameObject to new Prefab Asset on disk in project mode. |
| `prefab apply <idx>` |  | Push instance overrides back to source Prefab Asset in project mode. |
| `prefab revert <idx>` |  | Discard local overrides, revert to source Prefab Asset in project mode. |
| `prefab unpack <idx> [--completely]` |  | Break prefab connection in project mode. |
| `prefab variant <source-path> <new-path>` |  | Create Prefab Variant from base prefab in project mode. |
| `animator param add <asset-path> <name> <type>` |  | Add a parameter to an AnimatorController. `<type>`: `float`, `int`, `bool`, or `trigger`. |
| `animator param remove <asset-path> <name>` |  | Remove a parameter from an AnimatorController by name. |
| `animator state add <asset-path> <name> [--layer <n>]` |  | Add a new state to the target layer's root state machine (layer 0 by default). |
| `animator transition add <asset-path> <from-state> <to-state> [--layer <n>]` |  | Create a transition between two states. Use `AnyState` as `<from-state>` for Any State transitions. |
| `clip config <asset-path> [--loop-time <bool>] [--loop-pose <bool>]` |  | Modify loop settings of an AnimationClip. At least one of `loopTime` / `loopPose` required. |
| `clip event add <asset-path> <time> <function-name> [--string <val>\|--float <val>\|--int <val>]` |  | Insert an `AnimationEvent` at the specified time (seconds). |
| `clip event clear <asset-path>` |  | Remove all animation events from a clip. |
| `clip curve clear <asset-path>` |  | Remove all property curves and keyframes from a clip. |
| `tag list` | `tag ls` | List all tags (built-in and custom). |
| `tag add <name>` | `tag a` | Add a new custom tag. |
| `tag remove <name>` | `tag rm` | Remove a custom tag. Fails for built-in tags. |
| `layer list` | `layer ls` | List all layers with index and name. |
| `layer add <name> [--index <idx>]` | `layer a` | Add a layer at first empty user slot (8–31) or specified index. |
| `layer rename <old-name\|index> <new-name>` | `layer rn` | Rename a user layer. Fails for built-in layers 0–7. |
| `layer remove <name\|index>` | `layer rm` | Clear a user layer slot. Fails for built-in layers 0–7. |
| `scene load <path>` |  | Load a scene by path, replacing the current scene. |
| `scene add <path>` |  | Additively load a scene by path. |
| `scene unload <path>` |  | Unload an additively-loaded scene. |
| `scene remove <path>` |  | Remove a scene from the loaded set. |
| `hierarchy snapshot` |  | Dump the current scene hierarchy as structured data (same as `/dump hierarchy`). |
| `asset rename <path> <new-name>` |  | Rename an asset at the given path. (DestructiveWrite) |
| `asset remove <path>` |  | Delete an asset at the given path. (DestructiveWrite) |
| `asset create <type> <path>` |  | Create a new asset of the given type at path. |
| `asset create-script <name> <path>` |  | Create a new C# script at path. |
| `asset describe <path> [--engine blip\|clip]` |  | Describe asset visually using a local BLIP/CLIP model. (SafeRead) |
| `compile request` |  | Trigger a Unity script recompilation (Bridge mode only). |
| `compile status` |  | Check the result of the last compilation pass (Bridge mode only). |
| `console clear` |  | Clear the Unity console log. |
| `playmode start` |  | Enter Play Mode. (PrivilegedExec) |
| `playmode stop` |  | Exit Play Mode. (PrivilegedExec) |
| `playmode pause` |  | Pause Play Mode. (SafeWrite) |
| `playmode resume` |  | Resume paused Play Mode. (SafeWrite) |
| `playmode step` |  | Advance one frame while paused. (SafeWrite) |

## 5. Profiling (Lazy-Loaded Category)

The `profiling` category provides capture, analysis, and live telemetry tools backed by Unity's Profiler, MemoryProfiler, ProfilerRecorder, and FrameTimingManager APIs. It is **lazy-loaded** — call `load_category('profiling')` to register the tools as live MCP tools.

**CLI commands:**

```
/profiler inspect
/profiler start [--deep] [--editor] [--keep-frames]
/profiler stop
/profiler save <path>
/profiler load <path> [--keep-existing]
/profiler snapshot <path>
/profiler frames --from <a> --to <b>
/profiler counters --from <a> --to <b> [--names <list>]
/profiler threads --frame <n>
/profiler markers --frame <n>
/profiler markers --from <a> --to <b>
/profiler sample --frame <n> --thread <idx>
/profiler gc-alloc --from <a> --to <b>
/profiler compare <baseline> <candidate>
/profiler budget-check <expressions...>
/profiler export-summary <path>
/profiler live start [--counters <list>] [--duration <seconds>]
/profiler live stop
/profiler recorders
/profiler frame-timing
/profiler binary-log start <path>
/profiler binary-log stop
/profiler annotate session <json>
/profiler annotate frame <json>
```

**Agent / MCP operations (after `load_category('profiling')`):**

| Operation | Risk | Description |
| --- | --- | --- |
| `profiling.capabilities` | SafeRead | Feature probe for current editor/runtime context |
| `profiling.inspect` | SafeRead | Profiler state, frame range, memory stats |
| `profiling.start_recording` | PrivilegedExec | Start profiler recording (deep, editor, keepFrames) |
| `profiling.stop_recording` | PrivilegedExec | Stop recording, return frame range summary |
| `profiling.save_profile` | SafeWrite | Save editor profiler session as `.data` capture |
| `profiling.load_profile` | SafeWrite | Load `.data` capture into editor session |
| `profiling.take_snapshot` | SafeWrite | Take memory snapshot (`.snap`) |
| `profiling.frames` | SafeRead | Frame range stats: CPU/GPU/FPS avg/p50/p95/max |
| `profiling.counters` | SafeRead | Counter series extraction for a frame range |
| `profiling.threads` | SafeRead | Thread enumeration for a given frame |
| `profiling.markers` | SafeRead | Top markers by total/self time |
| `profiling.sample` | SafeRead | Raw per-sample timing, metadata, callstacks |
| `profiling.gc_alloc` | SafeRead | GC allocation tracking by marker and frame |
| `profiling.compare` | SafeRead | Baseline vs candidate frame range deltas |
| `profiling.budget_check` | SafeRead | CI-friendly pass/fail budget rules |
| `profiling.export_summary` | SafeRead | Write stats JSON summary to disk |
| `profiling.live_start` | PrivilegedExec | Start ProfilerRecorder counter collection |
| `profiling.live_stop` | PrivilegedExec | Stop live collection, return stats + samples |
| `profiling.recorders_list` | SafeRead | Enumerate available ProfilerRecorder counters |
| `profiling.frame_timing` | SafeRead | FrameTimingManager CPU/GPU timing |
| `profiling.binary_log_start` | PrivilegedExec | Start raw binary log (`.raw`) streaming |
| `profiling.binary_log_stop` | PrivilegedExec | Stop binary logging, return file path and size |
| `profiling.annotate_session` | SafeWrite | Emit session-level metadata into profiler stream |
| `profiling.annotate_frame` | SafeWrite | Emit frame-level metadata into profiler stream |
| `profiling.gpu_capture_begin` | PrivilegedExec | Begin external GPU capture (RenderDoc/PIX) |
| `profiling.gpu_capture_end` | PrivilegedExec | End external GPU capture |

**Important:** Editor capture save/load (`.data` via `ProfilerDriver`) and runtime binary logging (`.raw` via `Profiler.logFile`) are separate flows — do not confuse them.

## 6. Asset Describe (Local Vision)

The `asset.describe` command lets agents "see" Unity assets without burning tokens on multimodal vision. It exports a thumbnail from the Unity Editor and runs a local BLIP or CLIP model to produce a compact text description.

**Architecture:** Two-phase composite command —
1. Unity daemon exports a preview PNG via `AssetPreview` API
2. CLI runs a Python captioning script locally via `uv run --script`
3. Thumbnail is deleted after captioning; only the text description is returned

**CLI usage:**

```
/asset describe Assets/Sprites/hero.png
/asset describe Assets/Sprites/hero.png --engine clip
```

**Agentic exec usage:**

```json
{
  "operation": "asset.describe",
  "requestId": "req-042",
  "args": {
    "assetPath": "Assets/Sprites/hero.png",
    "engine": "blip"
  }
}
```

**Parameters:**

| Arg | Required | Default | Description |
| --- | --- | --- | --- |
| `assetPath` | Yes | — | Unity asset path (e.g., `Assets/Textures/Tile.png`) |
| `engine` | No | `blip` | Captioning engine: `blip` (open-ended captions) or `clip` (zero-shot classification against game-asset labels) |

**Response (agentic):**

```json
{
  "status": "Completed",
  "result": {
    "assetPath": "Assets/Sprites/hero.png",
    "assetType": "Texture2D",
    "fileSizeBytes": 24576,
    "description": "a cartoon character with a blue hat",
    "engine": "blip",
    "model": "Salesforce/blip-image-captioning-base@82a37760"
  }
}
```

**Prerequisites:**

- `python3` (>= 3.10) and `uv` — run `unifocl init` to install if missing
- First invocation downloads the model (~990 MB for BLIP, ~600 MB for CLIP); subsequent runs use the HuggingFace cache at `~/.cache/huggingface/`
- Non-image assets (meshes, materials, prefabs) work if Unity can generate an `AssetPreview`; falls back to mini-thumbnail icon, then metadata-only

**Security hardening:**

- Model revisions are **pinned to exact commit SHAs** — a compromised HuggingFace account cannot silently swap model weights
- Thumbnail paths are GUID-based (generated server-side), not influenced by agent input
- The `engine` argument is validated to `blip|clip` by the Python script's argparse
- Thumbnails are deleted immediately after captioning completes

**Dry-run:** Returns a pre-flight check (asset existence, `uv`/`python3` availability, model cache status, estimated download size) without exporting a thumbnail or running inference.

## 7. Safe Mutation: Dry-Run Previews

Both human operators and AI agents can validate mutations safely before execution. `--dry-run` is supported for mutation commands in all interactive and agentic modes:

- `Hierarchy` mutations (`mk`, `toggle`, `rm`, `rename`, `mv`, `go duplicate`)
- `Inspector` mutations (`set`, `toggle`, `component add/duplicate/remove`, `make`, `remove`, `rename`, `move`)
- `Project` filesystem mutations (`mk-script`, `rename-asset`, `duplicate-asset`, `remove-asset`, `prefab-create`, `prefab-apply`, `prefab-revert`, `prefab-unpack`, `prefab-variant`)
- `Addressables` mutations (`addressable init`, `profile set`, `group create/remove`, `entry add/remove/rename/label`, `bulk add`, `bulk label`)
- **Custom `[UnifoclCommand]` tools** — pass `dryRun: true` in the tool arguments; unifocl wraps the call in a Unity Undo group and reverts all in-memory and AssetDatabase changes automatically (see [`custom-commands.md`](custom-commands.md))
- **`/eval` dynamic C# execution** — pass `--dry-run` to execute code inside the Undo sandbox; all Unity-tracked changes are reverted after execution completes

**Behavior:**

- **Hierarchy / Inspector (memory layer):** unifocl captures pre/post state snapshots, executes inside an Undo group, immediately reverts, and returns a structured diff preview.
- **Project (filesystem layer):** unifocl returns proposed path/meta changes without performing file I/O.
- **TUI/Agentic rendering:** When `dry-run` is appended, unified diff lines are shown in the transcript output for humans, or nested in the `diff` payload for agents.

**Examples:**

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

## 8. Dynamic C# Eval

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

Eval uses a dual-compiler strategy that selects the best backend for the current environment:

| Mode | Compiler | Detail |
| --- | --- | --- |
| Bridge (GUI editor) | Unity `AssemblyBuilder` | Async — yields to the editor update loop while `buildFinished` fires on the next tick. Same C# language version as the project. |
| Host (batchmode) | Unity-bundled Roslyn `csc` | Out-of-process via `Process.Start` using the `dotnet` and `csc.dll` shipped inside the Unity editor install. No dependency on the editor update loop. |

Both paths resolve assembly references from `AppDomain.CurrentDomain.GetAssemblies()`, so project scripts, packages, and plugins are available. Temporary eval DLLs are self-filtered to avoid stale references.

- The entry point is always `async Task<object>`, so `await` works naturally without special flags or detection heuristics.
- A `CancellationToken cancellationToken` parameter is available inside eval code, wired to the `--timeout` value.
- Default usings cover the most common scenarios: `System`, `System.IO`, `System.Linq`, `System.Collections.Generic`, `System.Text.RegularExpressions`, `System.Threading.Tasks`, `UnityEngine`, and `UnityEditor`.

**Execution model:**

Eval is dispatched through the same durable mutation protocol as all other project-mutating commands (submit -> poll -> result). This avoids blocking the main thread during compilation and allows the editor update loop to continue processing internal callbacks.

The `SynchronizationContext` is temporarily cleared before invoking user code, so `await` expressions inside eval do not deadlock by posting continuations back to the occupied main thread — they resume on the thread pool instead.

**Result serialization:**

unifocl uses a multi-tier serialization strategy to produce the most informative output for each return type:

| Return type | Serialization strategy |
| --- | --- |
| `null` / void | `"null"` |
| `string` | Raw string value |
| Primitives (`int`, `float`, `bool`, ...) | Literal value with full numeric precision (`float` G9, `double` G17) |
| `IDictionary` | JSON object with string keys |
| `IEnumerable` (arrays, lists, sets, ...) | JSON array |
| `UnityEngine.Object` | Full editor serialization via `EditorJsonUtility.ToJson` |
| `[Serializable]` types | Unity's fast `JsonUtility.ToJson` path |
| Structured objects | Depth-limited reflection walk over public fields and readable properties |
| Other | `obj.ToString()` |

The reflection serializer is depth-limited (max 8 levels) to safely handle cyclic or deeply nested object graphs without risking stack overflows.

**Safety and approval:**

- `eval.run` is classified as `PrivilegedExec` in the ExecV2 API. Like `build.run` and `build.exec`, it requires two-step approval before execution — agents cannot silently evaluate code without explicit confirmation.
- `--dry-run` wraps execution in the same Undo-group sandbox used by custom `[UnifoclCommand]` tools. All Unity Undo-tracked changes (component edits, hierarchy modifications, scene state) are captured in an Undo group and reverted immediately after execution. `System.IO` writes are **not** reverted — this is a documented and intentional limitation shared with all dry-run paths in unifocl.
- The `--timeout` flag provides a hard cancellation boundary. If eval code exceeds the timeout, the `CancellationToken` is triggered and execution is interrupted.

## 9. Human Interface: TUI & Keybindings

For developers using the interactive CLI, unifocl features a composer with Intellisense and keyboard-driven navigation.

- Type `/` to open the slash-command suggestion palette.
- Type any standard text to receive project-mode suggestions.
- **Fuzzy Finding:** Use the `f` or `ff` command to trigger fuzzy search (e.g., `f --type script PlayerController`).

**Global Keybinds**

- **`F7`**: Toggle focus for Hierarchy TUI, Project navigator, Recent projects list, and Inspector.
- **`Esc`**: Dismiss Intellisense, or clear input if already dismissed.
- **`Up` / `Down`**: Navigate fuzzy/Intellisense candidates.
- **`Enter`**: Insert selected suggestion or commit input.

**Context-Specific Focus Navigation**

Once focused (`F7`), the arrow keys and tab behave contextually:

| **Action** | **Hierarchy Focus** | **Project Focus** | **Inspector Focus** |
| --- | --- | --- | --- |
| **`Up` / `Down`** | Move highlighted GameObject | Move highlighted file/folder | Move highlighted component/field |
| **`Tab`** | Expand selected node | Reveal/open selected entry | Inspect selected component |
| **`Shift+Tab`** | Collapse selected node | Move to parent folder | Back to component list |
| **Exit Focus** | `Esc` or `F7` | `Esc` or `F7` | `Esc` or `F7` |

## 10. Project Validation

The `/validate` command family runs project health checks and produces structured diagnostics. Each validator returns a uniform `ValidateResult` envelope with severity-tagged findings (`Error`, `Warning`, `Info`), error codes, and fixability hints.

```
/validate <subcommand>
```

| Subcommand | Requires Daemon | Description |
| --- | --- | --- |
| `scene-list` | Yes | Checks that all `EditorBuildSettings.scenes` paths exist on disk. Flags disabled and empty entries. |
| `missing-scripts` | Yes | Scans loaded scenes and all prefab assets for null `MonoBehaviour` components (missing script references). |
| `packages` | No | Compares `manifest.json` vs `packages-lock.json` — detects missing lock entries, version mismatches, and missing files. |
| `build-settings` | Yes | Checks `PlayerSettings` sanity — bundle ID, product/company name, version format, active build target, enabled scenes, scripting backend. |
| `asmdef` | No | Parses all `.asmdef` files under `Assets/`, builds a dependency graph, and checks for duplicate assembly names, undefined references, and circular dependencies. |
| `asset-refs` | Yes | Scans `.unity`, `.prefab`, `.asset`, `.mat`, and `.controller` files for GUID references that do not resolve to any known asset in `AssetDatabase`. Caps output at 500 findings. |
| `addressables` | Yes | Checks whether the Addressables package is installed, then validates the settings asset, groups directory, and basic settings structure. |
| `scripts` | No | Offline Roslyn compile check for all project C# scripts. Generates a temporary `.csproj` referencing Unity managed DLLs and runs `dotnet build` locally — no running editor required. Returns CS#### error codes with file/line locations. |
| `all` | Mixed | Runs all validators sequentially. |

Every diagnostic carries: `severity` (Error/Warning/Info), `errorCode` (e.g. `VSC003`, `VASD004`, `VAR001`), `message`, optional `assetPath`/`objectPath`, and a `fixable` flag.

**Agentic usage:**

```sh
unifocl exec "/validate packages" --agentic --format json --project ./my-project --session-seed my-seed
unifocl exec "/validate asmdef" --agentic --format json --project ./my-project --session-seed my-seed
unifocl exec "/validate asset-refs" --agentic --format json --project ./my-project --session-seed my-seed
```

ExecV2 operations (all `SafeRead` — no approval required):
`validate.scene-list`, `validate.missing-scripts`, `validate.packages`, `validate.build-settings`, `validate.asmdef`, `validate.asset-refs`, `validate.addressables`, `validate.scripts`

Full reference: [`validate-build-workflow.md`](validate-build-workflow.md)

## 11. Build Workflow

The build workflow commands extend `/build` with pre-build validation, post-build introspection, and a unified report surface. Build reports are automatically captured after every build via a `IPostprocessBuildWithReport` hook and stored at `Library/unifocl-last-build-report.json`.

```
/build <snapshot-packages|preflight|artifact-metadata|failure-classify|report>
```

| Subcommand | Requires Daemon | Description |
| --- | --- | --- |
| `snapshot-packages` | No | Reads `Packages/manifest.json` and writes a timestamped snapshot to `.unifocl-runtime/snapshots/packages-{timestamp}.json`. |
| `preflight` | Yes | Orchestrates `validate scene-list` + `validate build-settings` + `validate packages` sequentially and reports aggregated pass/fail. |
| `artifact-metadata` | Yes | Returns the file list, roles, sizes, output path, build target, and duration from the last captured build report. |
| `failure-classify` | Yes | Reads the last build report and classifies each error message into one of five categories: `CompileError`, `LinkerError`, `MissingAsset`, `ScriptError`, `Timeout`. |
| `report` | Yes | Runs preflight, then reads artifact-metadata and failure-classify, and renders a consolidated summary. |

**Agentic usage:**

```sh
unifocl exec "/build preflight" --agentic --format json --project ./my-project --session-seed my-seed
unifocl exec "/build artifact-metadata" --agentic --format json --project ./my-project --session-seed my-seed
unifocl exec "/build report" --agentic --format json --project ./my-project --session-seed my-seed
```

ExecV2 operations (all `SafeRead` — no approval required):
`build.snapshot-packages`, `build.preflight`, `build.artifact-metadata`, `build.failure-classify`, `build.report`

Full reference: [`validate-build-workflow.md`](validate-build-workflow.md)

## 12. Test Orchestration

The `test` commands run Unity's built-in test runner as a **direct subprocess** — no daemon, no running editor required. This makes them safe to call from CI, parallel agent sessions, or any headless environment.

```
/test list
/test run <editmode|playmode> [--timeout <seconds>]
/test flaky-report
```

| Subcommand | Platform flag | Default timeout | Description |
| --- | --- | --- | --- |
| `list` | EditMode | 5 min | Lists all available tests. Output: `[{ testName, assembly }]`. |
| `run editmode` | EditMode | 10 min | Runs all EditMode tests. |
| `run playmode` | PlayMode | 30 min | Runs all PlayMode tests. May trigger a player build. |
| `flaky-report` | — | — | Shows tests with mixed Pass/Fail outcomes across run history (requires prior test runs). |

**Output contract (`test run`):**

```json
{
  "total": 42,
  "passed": 40,
  "failed": 2,
  "skipped": 0,
  "durationMs": 8340,
  "artifactsPath": "<project>/Logs/unifocl-test",
  "failures": [
    { "testName": "MyTests.SomeTest", "message": "Expected 1 but was 2", "stackTrace": "...", "durationMs": 12 }
  ]
}
```

Results come from the NUnit v3 XML file Unity writes to `Logs/unifocl-test/`. If Unity crashes before writing results, the envelope still returns with all counters at zero and an empty failures array.

**Agentic usage:**

```sh
# List tests (no project open required)
unifocl exec "test list" --agentic --format json --project ./my-project

# Run EditMode suite
unifocl exec "test run editmode" --agentic --format json --project ./my-project --session-seed my-seed

# Run PlayMode suite with extended timeout
unifocl exec "test run playmode --timeout 3600" --agentic --format json --project ./my-project
```

ExecV2 operations: `test.list` (`SafeRead`), `test.run` (`PrivilegedExec` — requires approval on first call), `test.flaky-report` (`SafeRead`).

Full reference: [`test-orchestration.md`](test-orchestration.md)

## 13. Project Diagnostics

The `diag` command family provides read-only structural introspection of the project — assembly topology, define symbols, and asset dependency trees. Unlike `/validate`, `diag` commands are data dumps rather than pass/fail checks.

```
/diag <script-defines|compile-errors|assembly-graph|scene-deps|prefab-deps|asset-size|import-hotspots|all>
```

| Subcommand | Description |
| --- | --- |
| `script-defines` | Scripting define symbols per build target group (`PlayerSettings.GetScriptingDefineSymbolsForGroup`). |
| `compile-errors` | Compiler messages from the last compilation pass (`CompilationPipeline.GetAssemblies` + `.compilerMessages`). |
| `assembly-graph` | Asmdef-level assembly dependency graph (`assemblyReferences` per assembly). |
| `scene-deps` | Transitive `AssetDatabase.GetDependencies` per enabled build scene. |
| `prefab-deps` | Transitive `AssetDatabase.GetDependencies` per prefab under `Assets/` (capped at 100). |
| `asset-size` | Lists all project assets sorted by file size, with dependency counts. |
| `import-hotspots` | Shows the most frequently re-imported assets from recorded import history. |

All operations require the daemon. All are `SafeRead` — no approval gating.

**Agentic usage:**

```sh
unifocl exec "/diag assembly-graph" --agentic --format json --project ./my-project --session-seed my-seed
unifocl exec "/diag script-defines" --agentic --format json --project ./my-project --session-seed my-seed
unifocl exec "/diag scene-deps" --agentic --format json --project ./my-project --session-seed my-seed
```

ExecV2 operations (all `SafeRead` — no approval required):
`diag.script-defines`, `diag.compile-errors`, `diag.assembly-graph`, `diag.scene-deps`, `diag.prefab-deps`, `diag.asset-size`, `diag.import-hotspots`

Full reference: [`project-diagnostics.md`](project-diagnostics.md)

## Development & Contributing

### Local Compatcheck Bootstrap

When you need to run Unity editor compatibility checks locally (especially after bridge/editor code changes), use:

```
./scripts/setup-compatcheck-local.sh
```

What this command does:

- Detects a local Unity editor install.
- Creates/bootstraps a benchmark Unity project under `.local/compatcheck-benchmark`.
- Writes local path settings to `local.config.json`.
- Runs: `dotnet build src/unifocl.unity.compatcheck/unifocl.unity.compatcheck.csproj --disable-build-servers -v minimal`

Local artifacts are intentionally uncommitted (`local.config.json`, `.local/`).
