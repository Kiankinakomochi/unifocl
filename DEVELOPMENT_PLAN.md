# unifocl Development Plan and Implementation Details

## 1. Purpose
`unifocl` is a terminal-first Unity development assistant with a modal workflow for scene hierarchy, project assets, and inspector editing. It uses an always-warm backend daemon to avoid Unity cold starts and to provide low-latency command execution.

## 2. Goals and Non-Goals
### Goals
- Provide a native CLI UX for Unity scene and asset operations.
- Keep hierarchy navigation stateful with stable index snapshots.
- Preserve Unity metadata integrity by routing project file operations through Unity APIs.
- Provide inspector-level component and field editing with type-aware value parsing.
- Maintain fast response time via a persistent Unity headless daemon.

### Non-Goals (Initial Milestone)
- Replacing full Unity Editor UI capabilities.
- Runtime game debugging beyond basic scene and component manipulation.
- Multi-user concurrent editing guarantees in v1.

## 3. High-Level Architecture
- CLI App (`unifocl`): command parsing, local state, mode transitions, rendering.
- Transport Layer: JSON request/response over localhost (HTTP or WebSocket).
- Unity Editor Bridge: C# server script that executes requested operations using UnityEditor/Unity APIs.
- Background Daemon Manager: startup/probe logic for always-warm Unity headless process.

## 4. Mode Design
### 4.1 Hierarchy Mode (Scene Architect)
#### Functional Requirements
- Track Current Working Directory (CWD) in hierarchy context.
- `ls` lists children of current CWD with stable indices.
- `cd <index>` updates CWD and prompt (e.g., `unifocl:/Player >`).
- `tree -d <depth>` prints hierarchy from current CWD to requested depth.
- `mk <name> [-p]` creates GameObject (empty by default, primitive when `-p` is set and recognized).
- `mv <index> <target_index>` reparents using `Transform.SetParent`.
- `ref` refreshes scene snapshot and reassigns index table.
- `rm <index>` prompts with recursive child count confirmation.

#### Snapshot and Index Stability
- CLI stores a per-refresh snapshot map:
  - `index -> Unity object instance ID`
  - `instance ID -> object metadata`
- Any mutating command invalidates current snapshot and triggers selective refresh.
- On stale index access, return deterministic error with remediation hint (`run ref`).

### 4.2 Project Mode (Assets + Metadata Safety)
#### Functional Requirements
- All file-affecting operations route through Unity `AssetDatabase` methods.
- Support template-driven creation for scripts and files.
- Enforce template search order:
  1. Custom Template from `templates.json`
  2. Project Default template
  3. Unity Standard template fallback
- Report import/refresh completion status after operations that trigger reimport.

#### Template Engine Details
- `templates.json` at project root defines logical template keys and file paths.
- `mk script <Name>` flow:
  1. Resolve template path by search order.
  2. Read template content.
  3. Replace placeholders (minimum: `#NAME#`).
  4. Save via Unity bridge to ensure metadata consistency.

### 4.3 Inspector Mode (Component Surgeon)
#### Functional Requirements
- `inspect <index>` enters inspector context for selected GameObject.
- `ls` lists components with indices.
- `edit <index>` enters component field context.
- `:i` navigates up one mode layer:
  - Field -> Component
  - Component -> Inspector Root
  - Inspector Root -> Hierarchy
- `set <field> <values...>` parses and applies typed values.
- `toggle <field>` flips boolean values.

#### Type-Aware Value Parsing (v1)
- Supported scalar types: `int`, `float`, `bool`, `string`.
- Supported Unity structs initially: `Vector2`, `Vector3`, `Color`.
- Parser behavior:
  - Infer target type from reflected field/property metadata.
  - Validate arity and conversion.
  - Return typed JSON payload for bridge execution.

## 5. Backend Daemon (Always-Warm)
### Lifecycle
- On CLI startup:
  1. Probe localhost endpoint (default port `8080`).
  2. If unavailable, launch Unity in batch/headless mode with project path.
  3. Poll readiness endpoint until timeout.
- Keep daemon alive across command invocations.

### Unity Bridge
- Implement C# Editor script with listener (HTTP initially; WebSocket optional).
- Accept JSON packets:

```json
{
  "mode": "hierarchy",
  "command": "create",
  "params": { "name": "NewPlayer", "parentID": 5 }
}
```

- Return standardized response envelope:
  - `ok` (bool)
  - `result` (data)
  - `error` (code/message/details)
  - `snapshotVersion` (for hierarchy state coherence)

## 6. Command Contract (Draft)
- Global: `mode`, `status`, `ref`, `help`, `quit`.
- Hierarchy: `ls`, `cd`, `tree`, `mk`, `mv`, `rm`.
- Project: `ls`, `mk`, `mv`, `ren`, `rm`, `template`.
- Inspector: `inspect`, `ls`, `edit`, `set`, `toggle`, `:i`.

## 7. Error Handling and UX Rules
- All commands return deterministic machine-readable errors plus concise human text.
- Mutating commands require explicit confirmation where destructive (`rm`).
- Include actionable remediation in errors (e.g., stale snapshot -> `ref`).
- Prompt always includes mode and contextual path/object.

## 8. Security and Safety
- Localhost-only daemon binding.
- Validate command schema and parameter types server-side.
- Reject unsupported reflection targets in inspector mode.
- Add opt-in confirmation for broad destructive asset operations.

## 9. Implementation Roadmap
### Milestone 1: Core CLI Shell + Daemon Probe
- Implement CLI parser and mode framework.
- Add daemon health check and launcher process management.
- Add request/response transport and shared schema.

### Milestone 2: Hierarchy Mode End-to-End
- Implement hierarchy snapshot indexing.
- Add `ls`, `cd`, `tree`, `mk`, `mv`, `rm`, `ref`.
- Add delete safety prompt and stale index handling.

### Milestone 3: Project Mode + Template Engine
- Implement `AssetDatabase` bridge endpoints.
- Add `templates.json` resolver with search fallback.
- Add import completion reporting.

### Milestone 4: Inspector Mode + Typed Editing
- Implement object selection and component traversal.
- Add field reflection, `set`, `toggle`, and `:i` escape navigation.
- Add parser support for scalar + vector/color types.

### Milestone 5: Hardening and Developer Experience
- Logging, retries, timeout tuning.
- Improved help docs and command examples.
- Integration tests across all modes.

## 10. Testing Strategy
- Unit Tests:
  - CLI parser and mode transitions.
  - Value parser/type coercion.
  - Template resolution order.
- Integration Tests:
  - CLI <-> daemon packet contract.
  - Hierarchy snapshot refresh and stale index behavior.
  - Asset operations preserving metadata via Unity bridge.
- Manual Smoke Tests:
  - Cold start to warm daemon reuse.
  - Destructive command confirmation flow.
  - Inspector edit roundtrip on common component fields.

## 11. Deliverables in This Repository (Initial)
- `DEVELOPMENT_PLAN.md` (this document).
- Next phase will add:
  - CLI project scaffold.
  - Unity bridge scaffold.
  - Shared JSON contract definitions.
  - Initial automated tests.

## 12. Success Criteria
- User can navigate scene hierarchy with stable indices and low latency.
- User can perform asset operations without breaking Unity metadata.
- User can edit component fields safely with type-aware parsing.
- Daemon startup behavior is reliable and avoids repeated cold starts.
