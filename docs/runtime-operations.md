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

| Command | Description |
|---------|-------------|
| `/runtime target list` | List available targets |
| `/runtime attach <target>` | Attach (e.g., `/runtime attach android:pixel-7`) |
| `/runtime status` | Show connection state |
| `/runtime detach` | Disconnect |

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

## Roadmap

Sprint 1 (this release) delivers target management and the transport/extensibility foundation. Planned follow-up sprints:

| Sprint | Feature | Description |
|--------|---------|-------------|
| S2 | Runtime Manifest + Discovery | Manifest exchange on attach, MCP category integration |
| S3 | Query + Command Execution | Typed dispatch with schema validation and approval |
| S4 | Durable Jobs + Fan-out | Long-running jobs, multi-target execution |
| S5 | Streams + Watches | Live events, variable watches, log streaming |
| S6 | Scenario Files | YAML-based scripted repro flows with assertions |
