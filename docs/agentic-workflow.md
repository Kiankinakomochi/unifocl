# Agentic Workflow & Architecture

unifocl treats machine execution as a first-class citizen. It provides an **agentic execution path** specifically designed for LLMs, automations, and tool wrappers that require deterministic I/O instead of interactive TUI behavior.

Core principles:

- Structured response envelope for every command.
- No Spectre/TUI rendering in agentic one-shot mode.
- Standardized error taxonomy and process exit codes.
- Explicit state serialization commands for context hydration.

## 1. One-Shot CLI for Agents

Use `exec` to run a single command and exit, suppressing all human UI:

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

- `agentic` enables machine output (single response payload).
- `format` controls payload encoding (`json` or `yaml`).
- `project`, `mode`, and `attach-port` seed runtime context so commands can execute without interactive setup.

Agentic best-practice profile (native bridge + built-in MCP server):

- Use native durable daemon HTTP mutation lifecycle for writes (`submit -> status -> result`).
- Use `unifocl --mcp-server` when automation needs compact command lookup/context tools over stdio.
- For project mutations, prefer durable lifecycle calls (`submit -> status -> result`) instead of relying on a single long HTTP response.
- Reuse one `--session-seed` and one daemon attach target per workflow chain to avoid context rehydration churn.
- For deterministic edits, prefer path-based targeting and perform grouped verification (`/dump hierarchy` + `/dump inspector`) after each mutation batch.
- For concurrent agents, use one worktree and one daemon port per agent; do not run multiple mutating agents in the same worktree.

## 2. Unified Agentic Envelope

`agentic` responses adhere to a strict machine-readable schema:

```json
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

- `status`: high-level outcome (`success` or `error`).
- `requestId`: caller-supplied correlation id (or generated if omitted).
- `mode`: effective runtime context after command execution.
- `action`: normalized command family (e.g. `version`, `dump`, `upm`).
- `data`: command payload (shape varies by action).
- `errors`: deterministic machine errors (empty on success).
- `warnings`: non-fatal issues.
- `diff`: optional dry-run diff payload (present when `dry-run` preview is returned).
- `meta`: schema/protocol/exit metadata plus optional command-specific extras.

Agentic VCS setup guard:

- Agentic project mutations short-circuit with `E_VCS_SETUP_REQUIRED` when UVCS is detected but project VCS setup is incomplete.
- Non-mutation agentic commands continue to run.

## 3. Agentic Exit Codes

| **Exit Code** | **Meaning** |
| --- | --- |
| `0` | Success |
| `2` | Validation / parse / context-state error |
| `3` | Daemon/bridge availability or timeout class failure |
| `4` | Internal execution error |
| `6` | Escalation required (likely sandbox/network restriction prevented execution) |

`E_VCS_SETUP_REQUIRED` is classified under exit code `2`. `E_ESCALATION_REQUIRED` is classified under exit code `6`.

## 4. `/dump` State Serialization

`/dump` is uniquely designed for agent context-window transfer and deterministic snapshots:

```
/dump <hierarchy|project|inspector> [--format json|yaml] [--compact] [--depth n] [--limit n]
```

Current behavior:

- `hierarchy`: fetches hierarchy snapshot from attached daemon.
- `project`: serializes deterministic `Assets` tree entries.
- `inspector`: serializes inspector components/fields from attached bridge path.

Context handling:

- If required runtime state is missing (for example no attached daemon for `hierarchy`), response returns `E_MODE_INVALID` with a corrective hint.
- Unsupported category returns `E_VALIDATION`.

## 5. Daemon ExecV2 Endpoints

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
| `asset.describe` | SafeRead | `assetPath`, `engine?` (blip\|clip, default: blip) |
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
| `test.list` | SafeRead | _(none)_ |
| `test.run` | PrivilegedExec | `platform` (`EditMode`\|`PlayMode`), `timeoutSeconds?` |
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

## 6. Error Taxonomy

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

## 7. Capability Discovery and OpenAPI

Runtime capability discovery:

```
unifocl exec "/protocol" --agentic --format json
# HTTP (requires --unsafe-http daemon flag and token header):
curl -H "X-Unifocl-Token: $(cat ~/.unifocl-runtime/http-token-8080.txt)" \
  "http://127.0.0.1:8080/agent/capabilities"
```

Static OpenAPI contract:

- [`openapi-agentic.yaml`](openapi-agentic.yaml)

## 8. Concurrent Worktree Integration (Parallel Agents)

Agentic mode is designed to run safely across multiple autonomous agents by isolating each agent in its own worktree and daemon port.

Use the built-in orchestration scripts:

- Bash: `src/unifocl/scripts/agent-worktree.sh`
- PowerShell: `src/unifocl/scripts/agent-worktree.ps1`

Recommended flow (bash example):

```sh
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
- tear down completed worktrees via script (`teardown`) or `git worktree remove --force`.

Operating boundaries: no cross-worktree edits, no shared mutable daemon state, no daemon port reuse assumptions.

Smoke project default: `setup-smoke-project` seeds `Packages/manifest.json` with `com.unity.modules.imageconversion`.

## Architecture & Core Systems

### Application Architecture

unifocl is a .NET console application built for cross-platform compatibility (Windows, macOS, Linux). The architecture seamlessly supports both the human CLI and the agentic machine interfaces. It is divided into four primary layers:

1. **CLI Layer:** Handles commands, Spectre.Console human interactions, and stateless agentic routing.
2. **Mode System:** Manages the context-aware environments (Hierarchy, Project, Inspector).
3. **Daemon Layer:** A persistent background coordinator that tracks project state and serves as the backend API.
4. **Bridge Mode Channel:** The communication interface between the daemon and an active Unity Editor/runtime.

### The unifocl Daemon

The daemon is a localhost control process, not a kernel/OS-level file mutation service.

Current implementation summary:

- **External transport (CLI <-> unifocl daemon):** The CLI and agents communicate with the unifocl daemon over a **Unix Domain Socket** (`~/.unifocl-runtime/daemon-{port}.sock`, chmod 0600) by default. An HTTP listener on `127.0.0.1:<port>` is only started when the daemon is launched with `--unsafe-http`, and requires the `X-Unifocl-Token` header (token written to `~/.unifocl-runtime/http-token-{port}.txt` at startup, chmod 0600). Request body size is capped at 1 MB on the HTTP path.
- **Internal transport (unifocl daemon <-> Unity Editor):** The unifocl daemon communicates internally with the Unity Editor-side bridge (`CLIDaemon`) over a separate localhost HTTP channel — this is always local-only and not exposed to agents.
- The daemon keeps a project-scoped session warm so commands do not need to cold-start Unity every time.
- Mode selection is runtime-based:
    - **Host mode:** If no suitable GUI editor bridge is attached, unifocl starts Unity in batch/no-graphics mode (`headless`) and serves commands through that Unity process.
    - **Bridge mode:** If a GUI Unity editor for the same project is already active and attachable, unifocl routes commands to that live editor bridge endpoint.
- Project operations are executed by Unity-side services/contracts (`CLIDaemon`/`DaemonProjectService`), then reported back to the CLI/Agent as typed ExecV2 responses.
- If an endpoint is reachable but unhealthy (for example ping works but project commands do not), unifocl restarts and re-attaches the managed daemon path.
- Daemon state is tracked per project (deterministic port + local `.unifocl` config/session metadata).

What this means in practice:

- unifocl does not bypass Unity with privileged OS hooks.
- It either executes through a Host-mode Unity runtime or through a Bridge-mode attached editor runtime, depending on what is available.

## Persistence Safety Contract

unifocl enforces a mutation safety contract across `hierarchy`, `inspector`, and `project` modes, crucial for safe autonomous agent execution and human user error prevention. The implementation is split into four layers.

### 1. Transactional Envelope (Daemon Core)

All mutating requests carry a required `MutationIntent` envelope before Unity API or filesystem execution.

Current envelope fields:

- `transactionId`
- `target`
- `property`
- `oldValue`
- `newValue`
- `flags.dryRun`
- `flags.requireRollback` (must be `true`)
- `flags.vcsMode` (optional: `uvcs_all` or `uvcs_hybrid_gitignore`)
- `flags.vcsOwnedPaths[]` (optional per-path owner metadata used for checkout policy)

Daemon-side validation is centralized in `DaemonMutationTransactionCoordinator` and rejects mutation requests that are missing or invalid. Valid intents are routed to a deterministic safety handler by mode:

- `hierarchy` / `inspector` -> `memory`
- `project` -> `filesystem`

Each mutation entrypoint returns a unified transaction decision envelope (`success|error`) before command execution continues.

### 2. Memory Layer Safety (Hierarchy & Inspector)

Inspector and hierarchy property writes are routed through Unity serialized APIs and guarded for idempotency:

- Mutations use `SerializedObject` / `SerializedProperty`.
- Read-before-write checks skip no-op writes.
- `Undo.RecordObject(...)` + `ApplyModifiedProperties()` execute only when values actually change.

Lifecycle and multi-step memory mutations are wrapped in Undo boundaries:

- Creates use `Undo.RegisterCreatedObjectUndo(...)`.
- Deletes use `Undo.DestroyObjectImmediate(...)`.
- Multi-step operations use grouped Undo with `Undo.CollapseUndoOperations(groupId)` on success.
- Failures revert via `Undo.RevertAllDownToGroup(groupId)`.

Persistence hooks for scene/prefab integrity:

- Prefab instances are tracked with `PrefabUtility.RecordPrefabInstancePropertyModifications(...)`.
- Successful scene mutations mark and save through `EditorSceneManager.MarkSceneDirty(...)` and scene persistence services.
- Dry-run mode suppresses durable scene writes.

### 3. Filesystem Layer Safety (Project Mode)

Project-mode mutations that bypass Unity Undo are protected with transactional stashing and VCS-aware preflight:

- Before execution, UVCS-owned paths are preflighted for checkout (checkout-first policy; mutation fails if checkout is unavailable).
- Ownership mode is resolved per project:
    - `uvcs_all`: all mutation targets are treated as UVCS-owned.
    - `uvcs_hybrid_gitignore`: ownership is resolved from `.gitignore` rules at path level.
- Before execution, target assets and matching `.meta` files are shadow-copied into runtime stash storage under `$(UNIFOCL_PROJECT_STASH_ROOT || <temp>/unifocl-stash)/<project-hash>/...`.
- On success, stash contents are removed (commit path).
- On failure or exception, the stash is restored and cleanup targets are removed, then `AssetDatabase.Refresh(ForceUpdate)` is called to re-sync Unity state.

Unity Version Control (formerly Plastic SCM) behavior:

- UVCS uses checkout semantics, so writable filesystem state alone is not treated as authority for safe mutation.
- unifocl resolves ownership per target path, then performs checkout preflight before any file mutation is attempted.
- Paths classified as UVCS-owned must pass checkout preflight first; otherwise the mutation is rejected before file I/O begins.
- In `uvcs_hybrid_gitignore` mode, `.gitignore` is used as a pragmatic ownership split so UVCS checkout is enforced only for paths considered UVCS-owned.
- Dry-run includes ownership and checkout hints so automation can validate mutation viability before execution.

Interactive setup guard:

- When UVCS is auto-detected but unconfigured, the first project mutation prompts for one-time VCS setup and stores `.unifocl/vcs-config.json`.
- If setup is declined, mutation is aborted with actionable guidance.

Critical filesystem mutation sections are serialized with `SemaphoreSlim` to avoid concurrent race conditions during stash/restore and mutation execution.

### 4. Dry-Run & Preview Mechanics

Dry-run behavior is wired end-to-end from CLI parsing to daemon execution and agentic responses.

Memory dry-run (`hierarchy` / `inspector`):

- Snapshot pre-mutation state with `EditorJsonUtility.ToJson(...)`.
- Execute mutation inside an Undo group.
- Snapshot post-mutation state.
- Immediately revert with Undo.
- Return a structured unified diff payload.

Filesystem dry-run (`project`):

- No `System.IO` mutation occurs.
- Daemon returns proposed path and metadata changes (including `.meta` side effects), plus ownership/checkout hints for each path change.

CLI / agentic integration:

- Interactive outputs append unified dry-run diff lines in Spectre command logs.
- `agentic.v1` envelopes include optional `diff` payloads (`format`, `summary`, `lines`) for machine consumers.
