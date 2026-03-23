# Custom MCP Commands

unifocl lets you expose your own Unity editor methods as MCP tools, discoverable and callable by any connected AI agent. Methods are registered at compile time via the `[UnifoclCommand]` attribute, grouped into named categories, and loaded on demand by the agent through three built-in MCP tools: `get_categories`, `load_category`, and `unload_category`.

## Quick Start

```csharp
using UniFocl.EditorBridge;
using UnityEditor;

public static class GameDataTools
{
    [UnifoclCommand(
        name: "export_balance_sheet",
        description: "Exports a CSV of all ScriptableObject balance values to the given path.",
        category: "GameData")]
    public static string ExportBalanceSheet(string outputPath)
    {
        // ... your editor logic here
        return $"Exported to {outputPath}";
    }
}
```

After the next Unity Editor compilation, this method is available to any MCP agent as:

```
get_categories()                  → ["GameData"]
load_category("GameData")         → registers export_balance_sheet as a live MCP tool
export_balance_sheet(outputPath)  → calls your method, returns the result string
unload_category("GameData")       → removes the tool from the active list
```

## The `[UnifoclCommand]` Attribute

```csharp
[UnifoclCommand(name, description, category = "Default")]
public static ReturnType MethodName(ParamType param, ...) { ... }
```

| Parameter | Type | Required | Description |
|---|---|---|---|
| `name` | `string` | yes | Tool name as it appears to the MCP client. Use `snake_case`. |
| `description` | `string` | yes | Shown to the agent as the tool description. Be specific. |
| `category` | `string` | no | Groups related tools. Defaults to `"Default"`. |

**Constraints:**
- The method must be `static`.
- The method must be in a class visible to Unity's type cache (i.e., in a compiled editor assembly, inside an `#if UNITY_EDITOR` guard or an `Editor` asmdef).
- Return type should be `string`. Other types are coerced via `.ToString()`.
- Supported parameter types: `string`, `int`, `long`, `float`, `double`, `bool`. Complex types receive the raw JSON string.

## Manifest Generation

When Unity finishes a compilation cycle, `UnifoclManifestGenerator` scans all assemblies via `TypeCache.GetMethodsWithAttribute<UnifoclCommandAttribute>()` and writes a manifest to:

```
<ProjectRoot>/.local/unifocl-manifest.json
```

The manifest is also regenerated on demand via **unifocl → Regenerate Tool Manifest** in the Unity menu bar.

Each entry in the manifest captures the tool name, description, declaring type, method name, and a JSON Schema for the input parameters (derived from the method signature). This schema is what the MCP client uses to know what arguments to send.

The `.local/` directory is local-only and should not be committed. Add it to `.gitignore` if it isn't already.

## MCP Discovery Flow

The unifocl MCP server exposes three built-in tools for managing custom tool categories. An agent typically follows this pattern:

```
1. get_categories()
   → { manifestLoaded: true, categories: [{ name: "GameData", toolCount: 3, active: false }] }

2. load_category("GameData")
   → { ok: true, message: "category 'GameData' loaded: 3 tool(s) registered", toolsAdded: 3 }
   → MCP client receives tools/list_changed notification
   → Agent's tool list now includes the 3 registered tools

3. ... agent calls custom tools ...

4. unload_category("GameData")   (optional: frees slots, triggers list_changed)
```

`get_categories` and `load_category` both attempt auto-loading the manifest if it hasn't been loaded yet. The project path is resolved from `UNIFOCL_UNITY_PROJECT_PATH` (env var) or from the first live daemon in `~/.unifocl-runtime`.

### Setting the project path explicitly

When running `unifocl --mcp-server` in contexts where the env var is not set and no daemon is running:

```json
{
  "mcpServers": {
    "unifocl": {
      "command": "unifocl",
      "args": ["--mcp-server"],
      "env": {
        "UNIFOCL_UNITY_PROJECT_PATH": "/absolute/path/to/YourUnityProject"
      }
    }
  }
}
```

## Dry-Run Sandbox

Custom tools support `dryRun: true` in their input arguments without any code changes on your part. Pass it alongside the tool's normal arguments:

```json
{ "outputPath": "Assets/Data/balance.csv", "dryRun": true }
```

When `dryRun` is `true`, unifocl wraps the invocation in a three-layer sandbox before calling your method:

### Layer 1 — Unity Undo group (in-memory mutations)

An Undo group is opened immediately before your method runs and `Undo.RevertAllDownToGroup` is called immediately after. Any component or scene changes made via `Undo.RecordObject` are fully reverted. `AssetDatabase.Refresh()` is called afterwards to resync Unity state.

**Covered:** all standard `Undo.RecordObject`-tracked modifications to GameObjects, components, and ScriptableObjects.

### Layer 2 — AssetDatabase modification interceptor

`DaemonDryRunAssetModificationProcessor` is active during the invocation. It intercepts:

| AssetDatabase operation | Effect during dry-run |
|---|---|
| `AssetDatabase.SaveAssets()` | Returns 0 paths saved — no files written |
| `AssetDatabase.MoveAsset()` | Returns `FailedMove` |
| `AssetDatabase.DeleteAsset()` | Returns `FailedDelete` |
| `AssetDatabase.CreateAsset()` | Not blockable — use `Undo.RegisterCreatedObjectUndo` so Layer 1 reverts it |

**Covered:** all AssetDatabase-level write operations except CreateAsset (see note above).

### Layer 3 — `DaemonDryRunContext.IsActive` (opt-in runtime guard)

Your method can check `DaemonDryRunContext.IsActive` to suppress any writes that the Undo system or AssetModificationProcessor cannot intercept:

```csharp
[UnifoclCommand("export_balance_sheet", "Exports balance data.", "GameData")]
public static string ExportBalanceSheet(string outputPath)
{
    var csv = BuildCsv();

    if (DaemonDryRunContext.IsActive)
    {
        // Return a preview without writing anything
        return $"[dry-run] would write {csv.Length} bytes to {outputPath}";
    }

    File.WriteAllText(outputPath, csv);
    return $"Exported {csv.Length} bytes to {outputPath}";
}
```

### Coverage summary

| Write type | Layer 1: Undo | Layer 2: AssetDB | Layer 3: manual guard |
|---|---|---|---|
| `Undo.RecordObject` mutations | ✅ auto-reverted | — | — |
| `AssetDatabase.SaveAssets()` | — | ✅ blocked | — |
| `AssetDatabase.MoveAsset/DeleteAsset()` | — | ✅ blocked | — |
| `AssetDatabase.CreateAsset()` | ⚠️ only with `RegisterCreatedObjectUndo` | — | optional |
| `System.IO.File.WriteAllText()` etc. | ❌ | ❌ | ✅ your responsibility |

## UNIFOCL001 Analyzer: Compile-Time Warning for Raw I/O

To catch `System.IO` writes at compile time, unifocl ships a Roslyn analyzer that emits **UNIFOCL001** on any `[UnifoclCommand]` method that calls write-capable I/O APIs directly:

```
warning UNIFOCL001: 'ExportBalanceSheet' calls 'File.WriteAllText' which writes to the
file system and will NOT be reverted by the unifocl Undo-based dry-run. Prefer AssetDatabase
APIs, or guard with DaemonDryRunContext.IsActive and skip the write manually.
```

**Detected operations:** `File.WriteAllText/Bytes/Lines`, `File.AppendAll*`, `File.Create/Delete/Move/Copy/Replace`, `Directory.CreateDirectory/Delete/Move`, `new StreamWriter(...)`, `new BinaryWriter(...)`, `new FileStream(...)`.

**Not detected:** writes performed inside helper methods outside the annotated method body, or P/Invoke.

### Installing the analyzer in your Unity project

1. Build `src/unifocl.unity.analyzer/unifocl.unity.analyzer.csproj`:
   ```
   dotnet build src/unifocl.unity.analyzer/unifocl.unity.analyzer.csproj -c Release
   ```

2. Copy the output DLL into your Unity project:
   ```
   src/unifocl.unity.analyzer/bin/Release/netstandard2.0/UniFocl.Unity.Analyzer.dll
   → <YourUnityProject>/Assets/Editor/Analyzers/UniFocl.Unity.Analyzer.dll
   ```

3. In the Unity Editor, select the DLL in the Project window and set its **Asset Label** to `RoslynAnalyzer`.

Unity 2022.2 and later will run the analyzer on every script compilation. Warnings appear in the Console window and in your IDE if it is connected to the Unity project.

**Suppressing a specific call:** if a raw I/O call is intentional and guarded manually, suppress inline:

```csharp
#pragma warning disable UNIFOCL001
File.WriteAllText(path, data);
#pragma warning restore UNIFOCL001
```

## Best Practices

**Prefer AssetDatabase APIs over System.IO.** Methods like `AssetDatabase.CreateAsset`, `AssetDatabase.MoveAsset`, and `AssetDatabase.ImportAsset` go through Unity's tracking pipeline and are protected by Layer 2. Direct `File.*` calls bypass all three layers unless guarded with `DaemonDryRunContext.IsActive`.

**Always register created objects with Undo.** If your tool creates assets or GameObjects:

```csharp
var obj = ScriptableObject.CreateInstance<MyData>();
AssetDatabase.CreateAsset(obj, path);
Undo.RegisterCreatedObjectUndo(obj, "create MyData");  // Layer 1 can now revert this
```

**Return structured output.** The tool result is returned as a string to the MCP client. Return a short JSON payload for complex results so the agent can parse it:

```csharp
return $"{{\"ok\":true,\"path\":\"{path}\",\"bytes\":{data.Length}}}";
```

**Keep categories focused.** Group tools that are always needed together. Agents load and unload categories as a unit — a bloated category adds unnecessary tools to the agent's context window.

## Limitations

- **`AssetDatabase.CreateAsset` during dry-run** — cannot be blocked by the AssetModificationProcessor hook. Use `Undo.RegisterCreatedObjectUndo` immediately after so Layer 1 reverts the created object.
- **Indirect I/O** — the UNIFOCL001 analyzer only inspects the annotated method body directly. Writes inside helper methods called from the annotated method are not flagged.
- **`System.IO` raw writes** — not reverted automatically. Guard them with `DaemonDryRunContext.IsActive` and annotate the result string accordingly.
- **Main thread only** — Unity editor APIs require the main thread. The unifocl dispatcher already ensures methods are called from the HTTP request handler on the main Unity thread; do not dispatch to background threads inside your tool.
- **No async methods** — `[UnifoclCommand]` methods must be synchronous `static` methods. Async is not supported by the dispatcher.
