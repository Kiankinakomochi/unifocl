# Runtime Operations

unifocl extends its typed, risk-classified execution model to running Unity player instances. The runtime operations surface lets you control, query, and observe Editor PlayMode sessions, standalone builds, and device builds through the same CLI and MCP interface used for editor operations.

## Architecture Overview

```
CLI / MCP Agent
     |  ExecV2 (runtime.*)
     v
unifocl daemon (HTTP on 127.0.0.1)
     |  /runtime/* endpoints
     v
DaemonRuntimeBridge (Editor-side)
     |  EditorConnection  (chunked JSON envelopes)
     v
UnifoclRuntimeClient (Player-side)
     |  RuntimeCommandRegistry lookup
     v
[UnifoclRuntimeCommand] handler method
```

The transport uses Unity's `EditorConnection` / `PlayerConnection` APIs. Messages are JSON envelopes chunked into 16 KB segments with correlation-based request/response matching.

## Target Addressing

Runtime targets use the format `<platform>:<name>`:

| Address | Description |
|---------|-------------|
| `editor:playmode` | Unity Editor in Play Mode (default) |
| `android:pixel-7` | Android device matched by name |
| `ios:*` | First available iOS device |
| `windows:standalone` | Local Windows standalone build |
| `macos:*` | Any macOS standalone build |

The platform component is inferred from the player connection name. The name component supports partial matching and `*` wildcard.

## ExecV2 Operations

All runtime operations go through the standard ExecV2 pipeline and respect the existing risk classification and approval model.

### Target Management
| Operation | Risk | Description |
|-----------|------|-------------|
| `runtime.target.list` | SafeRead | Enumerate available runtime targets |
| `runtime.attach` | SafeWrite | Attach to a target by address |
| `runtime.status` | SafeRead | Connection state of the attached target |
| `runtime.detach` | SafeWrite | Disconnect from the current target |

### Manifest Discovery
| Operation | Risk | Description |
|-----------|------|-------------|
| `runtime.manifest` | SafeRead | Request the runtime command manifest from the attached player |

### Query + Command Execution
| Operation | Risk | Description |
|-----------|------|-------------|
| `runtime.query` | SafeRead | Execute a read-only query on the attached target |
| `runtime.exec` | PrivilegedExec | Execute a mutating command on the attached target |

Args for both: `{ "command": "...", "args": { ... } }`

### Durable Jobs + Fan-out
| Operation | Risk | Description |
|-----------|------|-------------|
| `runtime.job.submit` | PrivilegedExec | Submit a long-running job; returns `jobId` |
| `runtime.job.status` | SafeRead | Poll job state (pending/running/completed/failed/cancelled) |
| `runtime.job.cancel` | SafeWrite | Cancel a running job |
| `runtime.job.list` | SafeRead | List all jobs and their states |

Jobs wrap `runtime.exec` with lifecycle tracking. Submit returns a `jobId` immediately; poll for completion.

### Streams + Watches
| Operation | Risk | Description |
|-----------|------|-------------|
| `runtime.stream.subscribe` | SafeWrite | Subscribe to a named event channel |
| `runtime.stream.unsubscribe` | SafeWrite | Unsubscribe by subscription ID |
| `runtime.watch.add` | SafeWrite | Add a variable watch expression |
| `runtime.watch.remove` | SafeWrite | Remove a watch |
| `runtime.watch.list` | SafeRead | List active watches |
| `runtime.watch.poll` | SafeRead | Poll all watches for current values |

Watches evaluate expressions on the attached player and cache results editor-side for polling.

### Scenario Files
| Operation | Risk | Description |
|-----------|------|-------------|
| `runtime.scenario.run` | PrivilegedExec | Execute a YAML scenario file step-by-step |
| `runtime.scenario.list` | SafeRead | List `.unifocl/scenarios/*.yaml` files |
| `runtime.scenario.validate` | SafeRead | Parse and validate a scenario file |

Example via MCP `exec`:

```json
{
  "operation": "runtime.target.list",
  "requestId": "req-001"
}
```

Response:

```json
{
  "status": "Completed",
  "requestId": "req-001",
  "result": {
    "ok": true,
    "targets": [
      {
        "playerId": 0,
        "name": "playmode",
        "platform": "editor",
        "deviceId": "local",
        "isConnected": false
      }
    ]
  }
}
```

### Attaching

```json
{
  "operation": "runtime.attach",
  "requestId": "req-002",
  "args": { "target": "editor:playmode" }
}
```

Once attached, query and command operations route to that target.

## CLI Commands

Interactive shell commands mirror the ExecV2 operations:

### Target Management
| Command | Description |
|---------|-------------|
| `/runtime target list` | List available targets |
| `/runtime attach <target>` | Attach (e.g., `/runtime attach android:pixel-7`) |
| `/runtime status` | Show connection state |
| `/runtime detach` | Disconnect |

### Manifest Discovery
| Command | Description |
|---------|-------------|
| `/runtime manifest` | Request and display the runtime command manifest |

### Query + Command Execution
| Command | Description |
|---------|-------------|
| `/runtime query <command> [argsJson]` | Execute a read-only query |
| `/runtime exec <command> [argsJson]` | Execute a mutating command |

### Durable Jobs
| Command | Description |
|---------|-------------|
| `/runtime job submit <command> [argsJson]` | Submit a long-running job |
| `/runtime job status <jobId>` | Check job status |
| `/runtime job cancel <jobId>` | Cancel a running job |
| `/runtime job list` | List all jobs |

### Streams + Watches
| Command | Description |
|---------|-------------|
| `/runtime stream subscribe <channel> [filterJson]` | Subscribe to a live event stream |
| `/runtime stream unsubscribe <subscriptionId>` | Unsubscribe from a stream |
| `/runtime watch add <expression> [target] [intervalMs]` | Add a variable watch |
| `/runtime watch remove <watchId>` | Remove a watch |
| `/runtime watch list` | List active watches |
| `/runtime watch poll` | Poll all watches for current values |

### Scenario Files
| Command | Description |
|---------|-------------|
| `/runtime scenario run <path>` | Run a YAML scenario file |
| `/runtime scenario list` | List scenario files in `.unifocl/scenarios/` |
| `/runtime scenario validate <path>` | Validate a scenario file |

## Custom Runtime Commands

Game teams extend the runtime surface by authoring C# methods in player code and marking them with `[UnifoclRuntimeCommand]`. These are discovered at player startup via reflection and exposed through the same lazy-loadable category system used for editor custom commands.

### The `[UnifoclRuntimeCommand]` Attribute

```csharp
using UniFocl.Runtime;

public static class EconomyCommands
{
    [UnifoclRuntimeCommand(
        name: "economy.grant",
        description: "Grant currency to the player",
        category: "liveops",
        kind: RuntimeCommandKind.Command,
        risk: RuntimeRiskLevel.PrivilegedExec)]
    public static object GrantCurrency(string argsJson)
    {
        // parse argsJson, execute game logic
        return new { success = true, newBalance = 1500 };
    }
}
```

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | `string` | yes | | Fully qualified command name (e.g., `economy.grant`) |
| `description` | `string` | yes | | Shown to agents and in help text |
| `category` | `string` | no | `"default"` | Groups related commands for lazy loading |
| `kind` | `RuntimeCommandKind` | no | `Query` | `Query`, `Command`, or `Stream` |
| `risk` | `RuntimeRiskLevel` | no | `SafeRead` | `SafeRead` or `PrivilegedExec` |

**Constraints:**
- The method must be `static`.
- The method receives arguments as a single `string` parameter (raw JSON).
- The return value is serialized via `JsonUtility.ToJson()`.
- The containing assembly must compile into player builds (not an Editor-only assembly).

### Command Kinds

| Kind | Semantics | Typical Risk |
|------|-----------|-------------|
| `Query` | Read-only inspection, no side effects | `SafeRead` |
| `Command` | Mutates gameplay or runtime state | `PrivilegedExec` |
| `Stream` | Produces a continuous data flow (events, counters) | `SafeRead` |

### Risk Levels and Approval

Runtime commands participate in the same ExecV2 approval model as editor operations:

- **SafeRead**: No approval required. Queries, status checks, state inspection.
- **PrivilegedExec**: Requires approval token. Gameplay mutations, economy changes, AI commands.

On player builds, mutations are gated behind a compile-time define:

```
UNIFOCL_RUNTIME_ALLOW_MUTATIONS
```

Without this define, the player rejects any operation with risk level above `SafeRead`. This is a build-time opt-in — development builds enable it, release builds omit it.

### Categories as Distribution Units

Categories group related commands for independent loading. Teams can ship domain-specific packs:

| Category | Example Commands |
|----------|-----------------|
| `combat-tools` | `combat.spawn-enemy`, `combat.damage-log`, `combat.ai-state` |
| `liveops` | `economy.grant`, `economy.balance`, `inventory.add` |
| `qa-repros` | `qa.clear-state`, `qa.force-tutorial`, `qa.simulate-disconnect` |
| `level-authoring` | `level.reload`, `level.set-spawn`, `level.toggle-debug-vis` |

Agents discover and load categories through the standard MCP flow:

```
get_categories()                → [..., { name: "liveops", source: "runtime" }]
load_category("liveops")        → registers economy.grant, economy.balance, etc.
unload_category("liveops")      → removes them
```

## Runtime Manifest

When a target is attached, the player sends a typed manifest describing all available commands. Each entry includes:

```json
{
  "name": "economy.grant",
  "kind": "command",
  "risk": "PrivilegedExec",
  "category": "liveops",
  "argsSchema": {
    "type": "object",
    "properties": {
      "amount": { "type": "integer" }
    },
    "required": ["amount"]
  },
  "resultSchema": {
    "type": "object"
  }
}
```

This manifest powers:
- **Schema validation** of arguments before transmission
- **Auto-generated help** in the CLI
- **MCP tool schemas** for agent autocompletion
- **Risk classification** for the approval pipeline

## Transport Protocol

### Envelope Format

All messages use a unified `RuntimeEnvelope`:

| Field | Type | Description |
|-------|------|-------------|
| `correlationId` | `string` | GUID for request/response matching |
| `messageType` | `int` | Discriminator: Request (0), Response (1), StreamFrame (2), ManifestRequest (3), ManifestResponse (4), Ping (5), Pong (6) |
| `payload` | `string` | UTF-8 JSON body |
| `isChunked` | `bool` | Whether this is part of a multi-chunk message |
| `chunkIndex` | `int` | Zero-based chunk position |
| `totalChunks` | `int` | Total chunks in the sequence |

### Chunking

Payloads exceeding 16 KB are split into chunks. The chunking algorithm respects UTF-8 byte boundaries to avoid corrupting multi-byte characters. The receiver reassembles chunks by correlation ID before dispatching.

### Message GUIDs

| Direction | GUID | Purpose |
|-----------|------|---------|
| Editor to Player | `b8f3a1e0-7c4d-4f2e-9a6b-3d5e8f1c2a40` | Commands, queries, manifest requests, pings |
| Player to Editor | `c9e4b2f1-8d5e-4a3f-ab7c-4e6f9a2d3b51` | Responses, manifest data, stream frames, pongs |

## Player-Side Setup

### Assembly Definition

Runtime code lives in a dedicated assembly that compiles into player builds:

```
src/unifocl.unity/RuntimeScripts/
    UniFocl.Runtime.asmdef      ← references UnityEngine only
    UnifoclRuntimeCommandAttribute.cs
    UnifoclRuntimeClient.cs
    RuntimeCommandRegistry.cs
    RuntimeEnvelope.cs
    ChunkAccumulator.cs
```

This assembly must **not** reference `UnityEditor`. Editor-side code (`DaemonRuntimeBridge`) lives in the existing `EditorScripts/` assembly and references `UniFocl.Runtime` for shared types.

### Initialization

At player startup (`RuntimeInitializeOnLoadMethod`):
1. `RuntimeCommandRegistry.Discover()` scans all assemblies for `[UnifoclRuntimeCommand]` methods
2. `UnifoclRuntimeClient.AutoInitialize()` creates a persistent `[unifocl.runtime]` GameObject
3. Handlers are registered via `UnifoclRuntimeClient.RegisterHandler()`
4. The client listens for incoming envelopes on `PlayerConnection`

No manual setup is required. The system auto-initializes in any development build that includes the `UniFocl.Runtime` assembly.

## Scenario File Format
Scenario files live in `.unifocl/scenarios/` and use a YAML-like format:

```yaml
name: "economy smoke test"
description: "Verify grant and balance flow"

steps:
  - name: "grant currency"
    command: economy.grant
    args: {"amount": 100}
    capture_as: grant_result
    assert:
      - success == true
      - exists(resultJson)

  - name: "check balance"
    command: economy.balance
    args: {}
    delay_ms: 500
    assert:
      - success == true

  - name: "invalid grant"
    command: economy.grant
    args: {"amount": -1}
    continue_on_failure: true
    assert:
      - success == false
```

### Step Fields

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `name` | string | yes | Step label for output |
| `command` | string | yes | Runtime command to execute |
| `args` | JSON string | no | Arguments (default `{}`) |
| `capture_as` | string | no | Save response as a variable for `${name}` substitution |
| `assert` | list | no | Assertions evaluated against the response |
| `continue_on_failure` | bool | no | Continue to next step even if assertions fail |
| `delay_ms` | int | no | Milliseconds to wait after this step |

### Assertion Syntax

- `field == value` — JSON path equals expected value
- `field != value` — JSON path does not equal value
- `exists(field)` — JSON path exists in response

## Roadmap

All runtime operations sprints are now delivered:

| Sprint | Feature | Status |
|--------|---------|--------|
| S1 | Transport + Targets | Done |
| S2 | Runtime Manifest + Discovery | Done |
| S3 | Query + Command Execution | Done |
| S4 | Durable Jobs + Fan-out | Done |
| S5 | Streams + Watches | Done |
| S6 | Scenario Files | Done |
