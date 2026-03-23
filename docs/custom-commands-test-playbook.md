# Custom Commands Feature — Agent Test Playbook

End-to-end smoke test for the `[UnifoclCommand]` / deferred MCP tool loading feature.
Target project: `agentic-realistic-v2/` (already has the unifocl bridge package installed).

---

## Prerequisites

- unifocl CLI built from `codex/tool-manifest-deferred-mcp` and on `PATH`
  ```
  dotnet build src/unifocl/unifocl.csproj -c Release
  # or confirm: unifocl --version  (expect 2.2.0-a9)
  ```
- Unity Editor installed (2022.2 or later); editor path known (check `/unity detect`)
- The smoke project at `agentic-realistic-v2/` has been opened in Unity at least once
  (so `Library/ScriptAssemblies/` exists and `UniFocl.EditorBridge.dll` is compiled)

---

## Step 1 — Install the new editor scripts into the smoke project

The package at `agentic-realistic-v2/Packages/com.unifocl.cli/Editor/` currently contains only
the asmdef. The new feature files need to be copied there from the unifocl source tree.

Copy these files (relative to repo root) into
`agentic-realistic-v2/Packages/com.unifocl.cli/Editor/`:

| Source | Destination (under `Editor/`) |
|--------|-------------------------------|
| `src/unifocl.unity/EditorScripts/UnifoclCommandAttribute.cs` | `UnifoclCommandAttribute.cs` |
| `src/unifocl.unity/EditorScripts/UnifoclManifestGenerator.cs` | `UnifoclManifestGenerator.cs` |
| `src/unifocl.unity/EditorScripts/Models/DaemonBridgeModels.cs` | `Models/DaemonBridgeModels.cs` |
| `src/unifocl.unity/EditorScripts/Services/DaemonCustomToolService.cs` | `Services/DaemonCustomToolService.cs` |
| `src/unifocl.unity/EditorScripts/Services/DaemonDryRunAssetModificationProcessor.cs` | `Services/DaemonDryRunAssetModificationProcessor.cs` |
| `src/unifocl.unity/EditorScripts/Services/DaemonDryRunContext.cs` | `Services/DaemonDryRunContext.cs` |

Also copy the SharedModels that `DaemonBridgeModels.cs` may depend on:

```
src/unifocl.unity/SharedModels/  →  agentic-realistic-v2/Packages/com.unifocl.cli/SharedModels/
```

**Verification:** After copying, confirm the destination looks like:
```
Packages/com.unifocl.cli/Editor/
  UniFocl.EditorBridge.asmdef
  UnifoclCommandAttribute.cs
  UnifoclManifestGenerator.cs
  Models/
    DaemonBridgeModels.cs
  Services/
    DaemonCustomToolService.cs
    DaemonDryRunAssetModificationProcessor.cs
    DaemonDryRunContext.cs
```

---

## Step 2 — Write the test tool script

Create `agentic-realistic-v2/Assets/Editor/TestTools.cs` with the following content:

```csharp
#if UNITY_EDITOR
using System.Collections.Generic;
using UniFocl.EditorBridge;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Three [UnifoclCommand] methods used to smoke-test the deferred MCP tool loading feature.
/// Category: "TestTools"
/// </summary>
public static class TestTools
{
    // ── Tool 1: trivial ping ────────────────────────────────────────────────
    [UnifoclCommand(
        name: "ping_project",
        description: "Returns a health-check string confirming the custom tool bridge is working.",
        category: "TestTools")]
    public static string PingProject()
    {
        return "{\"ok\":true,\"message\":\"pong from agentic-realistic-v2\"}";
    }

    // ── Tool 2: read-only asset query ───────────────────────────────────────
    [UnifoclCommand(
        name: "count_assets",
        description: "Counts assets in the project matching an optional type filter (e.g. 't:Script'). Returns JSON with count and filter used.",
        category: "TestTools")]
    public static string CountAssets(string filter = "")
    {
        var query   = string.IsNullOrWhiteSpace(filter) ? "" : filter;
        var guids   = AssetDatabase.FindAssets(query);
        return $"{{\"ok\":true,\"filter\":\"{query}\",\"count\":{guids.Length}}}";
    }

    // ── Tool 3: mutation that exercises the Undo dry-run sandbox ────────────
    [UnifoclCommand(
        name: "create_marker_object",
        description: "Creates a GameObject named 'UnifoclTestMarker' in the active scene. Supports dryRun — the object is reverted automatically when dryRun is true.",
        category: "TestTools")]
    public static string CreateMarkerObject()
    {
        var go = new GameObject("UnifoclTestMarker");
        Undo.RegisterCreatedObjectUndo(go, "create UnifoclTestMarker");

        // DaemonDryRunContext.IsActive is checked by the dispatcher wrapper;
        // if dryRun is true the Undo group is reverted after this method returns.
        return $"{{\"ok\":true,\"message\":\"created GameObject 'UnifoclTestMarker'\",\"instanceId\":{go.GetInstanceID()}}}";
    }
}
#endif
```

---

## Step 3 — Trigger compilation and manifest generation

Open the project in Unity (GUI or batch) so the editor compiles the new scripts and
`UnifoclManifestGenerator` runs its post-compile hook.

### Option A — Open in GUI editor (preferred)

```
unifocl exec "/open $(pwd)/agentic-realistic-v2" --agentic --format json
```

Wait for the compile cycle to finish (watch the Console for "Manifest written to …" log),
then close or leave the editor open.

### Option B — Batch-mode trigger (headless)

```bash
/path/to/Unity -batchmode -projectPath "$(pwd)/agentic-realistic-v2" \
  -executeMethod UnifoclManifestGenerator.RegenerateManifest \
  -logFile - -quit
```

### Verify the manifest was written

```bash
cat agentic-realistic-v2/.local/unifocl-manifest.json
```

Expected shape:
```json
{
  "schemaVersion": 1,
  "generatedAtUtc": "<ISO timestamp>",
  "categories": [
    {
      "name": "TestTools",
      "tools": [
        {
          "name": "ping_project",
          "description": "Returns a health-check string ...",
          "declaringType": "TestTools",
          "methodName": "PingProject",
          "inputSchema": { "type": "object", "properties": { "dryRun": { "type": "boolean", ... } } }
        },
        {
          "name": "count_assets",
          ...
          "inputSchema": { "type": "object", "properties": { "filter": { "type": "string", ... }, "dryRun": ... } }
        },
        {
          "name": "create_marker_object",
          ...
        }
      ]
    }
  ]
}
```

**Pass criteria:** file exists, `categories` contains exactly one entry named `"TestTools"` with
`tools` length = 3.

---

## Step 4 — Start the unifocl daemon

The MCP server needs a live daemon to forward tool calls to Unity.

```bash
unifocl exec "/open $(pwd)/agentic-realistic-v2" --agentic --format json
# note the daemon port in the response (or check /daemon ps)
unifocl exec "/daemon ps" --agentic --format json
```

Keep the daemon running for the rest of the test.

---

## Step 5 — Start the MCP server and run the call sequence

Start the MCP server with the project path set explicitly:

```json
{
  "mcpServers": {
    "unifocl": {
      "command": "unifocl",
      "args": ["--mcp-server"],
      "env": {
        "UNIFOCL_UNITY_PROJECT_PATH": "/absolute/path/to/agentic-realistic-v2"
      }
    }
  }
}
```

Then execute the following MCP tool calls in order and record responses.

---

### Call 5.1 — `get_categories`

**Input:** _(no arguments)_

**Expected response shape:**
```json
{
  "manifestLoaded": true,
  "categories": [
    { "name": "TestTools", "toolCount": 3, "active": false }
  ]
}
```

**Pass criteria:**
- `manifestLoaded` is `true`
- exactly one category named `"TestTools"` with `toolCount` = 3 and `active` = false

---

### Call 5.2 — `load_category` with `"TestTools"`

**Input:** `{ "categoryName": "TestTools" }`

**Expected response shape:**
```json
{
  "ok": true,
  "message": "category 'TestTools' loaded: 3 tool(s) registered",
  "toolsAdded": 3
}
```

**Pass criteria:**
- `ok` is `true`
- `toolsAdded` = 3
- The MCP client receives a `notifications/tools/list_changed` notification and the three new
  tools now appear in the tool list: `ping_project`, `count_assets`, `create_marker_object`

---

### Call 5.3 — `ping_project`

**Input:** _(no arguments, or `{}`)_

**Expected response:**
```json
{"ok":true,"message":"pong from agentic-realistic-v2"}
```

**Pass criteria:** response text contains `"ok":true` and `"pong"`.

---

### Call 5.4 — `count_assets` (no filter)

**Input:** `{}`

**Expected response shape:**
```json
{"ok":true,"filter":"","count":<N>}
```

**Pass criteria:** `ok` is `true`, `count` > 0 (the project has at least a few assets).

---

### Call 5.5 — `count_assets` with type filter

**Input:** `{ "filter": "t:Script" }`

**Expected response shape:**
```json
{"ok":true,"filter":"t:Script","count":<M>}
```

**Pass criteria:** `ok` is `true`, `count` ≥ 1 (at least `TestTools.cs` itself).

---

### Call 5.6 — `create_marker_object` (live — no dry-run)

**Input:** `{}`

**Expected response shape:**
```json
{"ok":true,"message":"created GameObject 'UnifoclTestMarker'","instanceId":<id>}
```

**Pass criteria:** response contains `"ok":true` and a numeric `instanceId`.

**Manual verification (optional):** open the active scene in the Unity Editor and confirm
`UnifoclTestMarker` exists in the hierarchy.

---

### Call 5.7 — `create_marker_object` with `dryRun: true`

**Input:** `{ "dryRun": true }`

**Expected response shape (from `DaemonCustomToolService.InvokeWithDryRun`):**
```json
{
  "ok": true,
  "result": "{\"ok\":true,\"message\":\"created GameObject 'UnifoclTestMarker'\",\"instanceId\":<id>}",
  "message": "[dry-run] tool 'create_marker_object' executed and reverted; result captured above"
}
```

**Pass criteria:**
- Response contains `"[dry-run]"` in the `message` field
- The `result` field contains the tool's own return value
- **Unity scene state is unchanged:** `UnifoclTestMarker` does NOT persist (the Undo group was
  reverted). Verify by calling `count_assets` with `filter = "t:GameObject"` — count must not
  have increased, or open the hierarchy and confirm no new object.

---

### Call 5.8 — `get_categories` again (verify `active` flag)

**Input:** _(no arguments)_

**Expected response shape:**
```json
{
  "manifestLoaded": true,
  "categories": [
    { "name": "TestTools", "toolCount": 3, "active": true }
  ]
}
```

**Pass criteria:** `active` is now `true` because the category was loaded in 5.2.

---

### Call 5.9 — `unload_category` with `"TestTools"`

**Input:** `{ "categoryName": "TestTools" }`

**Expected response shape:**
```json
{
  "ok": true,
  "message": "category 'TestTools' unloaded: 3 tool(s) removed",
  "toolsRemoved": 3
}
```

**Pass criteria:**
- `ok` is `true`, `toolsRemoved` = 3
- MCP client receives another `notifications/tools/list_changed`; `ping_project`,
  `count_assets`, and `create_marker_object` no longer appear in the tool list

---

### Call 5.10 — `get_categories` final check

**Input:** _(no arguments)_

**Expected:**
```json
{
  "manifestLoaded": true,
  "categories": [
    { "name": "TestTools", "toolCount": 3, "active": false }
  ]
}
```

**Pass criteria:** `active` is back to `false`.

---

## Pass / Fail Summary

| # | Call | Pass Criteria |
|---|------|---------------|
| 5.1 | `get_categories` | manifest loaded, TestTools toolCount=3, active=false |
| 5.2 | `load_category("TestTools")` | ok=true, toolsAdded=3, tools appear in list |
| 5.3 | `ping_project` | response contains "pong" |
| 5.4 | `count_assets {}` | ok=true, count > 0 |
| 5.5 | `count_assets {filter:"t:Script"}` | ok=true, count ≥ 1 |
| 5.6 | `create_marker_object` | ok=true, instanceId present |
| 5.7 | `create_marker_object {dryRun:true}` | "[dry-run]" in message, scene unchanged |
| 5.8 | `get_categories` | active=true |
| 5.9 | `unload_category("TestTools")` | ok=true, toolsRemoved=3, tools gone from list |
| 5.10 | `get_categories` | active=false |

All 10 calls must pass for the feature to be considered working end-to-end.

---

## Troubleshooting

**`manifestLoaded: false` in 5.1**
→ The manifest file was not generated. Check that `UnifoclManifestGenerator` was compiled and
ran its post-compile callback. Re-run Step 3. Confirm `.local/unifocl-manifest.json` exists.

**`toolsAdded: 0` in 5.2**
→ The manifest has a `TestTools` category but the tool entries are empty or malformed. Check the
manifest JSON from Step 3 against the expected shape.

**Tool call returns `"tool not found"`**
→ The `DaemonCustomToolService` could not find the method via `TypeCache`. Confirm
`TestTools.cs` is inside an Editor assembly (either `Assets/Editor/` or an asmdef with
Editor-only platform constraints) and Unity has compiled without errors.

**Dry-run (5.7) returns no `"[dry-run]"` in message**
→ The `dryRun` flag was not forwarded from the MCP server to the daemon. Check that
`ManifestMcpServerTool.InvokeAsync` strips `dryRun` from args before forwarding and passes it
as a separate parameter to `ForwardToUnityAsync`.

**Scene is mutated after call 5.7**
→ `Undo.RegisterCreatedObjectUndo` was not called in `CreateMarkerObject`, or
`DaemonCustomToolService.InvokeWithDryRun` is not calling `Undo.RevertAllDownToGroup`. Check
`DaemonCustomToolService.cs`.
