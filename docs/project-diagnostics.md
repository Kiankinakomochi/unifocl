# Project Diagnostics

unifocl ships a `/diag` command family for deep project introspection. Where `/validate` answers "is this project healthy?", `/diag` answers "what is this project made of?" — it dumps structural data about assemblies, defines, and asset dependencies without pass/fail judgements.

All `diag` operations **require the daemon** and are `SafeRead` — they never mutate project state.

---

## Output Contracts

Each `diag` subcommand returns a different JSON payload (serialized into the `content` field of the response envelope). Schemas are documented per-command below.

---

## Commands

### `diag script-defines`

Reads `PlayerSettings.GetScriptingDefineSymbolsForGroup()` for every major build target group and returns the define symbols as a per-platform list.

**Output:**

```json
{
  "op": "script-defines",
  "targetCount": 9,
  "targets": [
    { "buildTarget": "Standalone", "group": "Standalone", "defines": "MY_DEFINE;ANOTHER_FLAG" },
    { "buildTarget": "iOS",        "group": "iOS",        "defines": "MY_DEFINE;IOS_SPECIFIC" },
    { "buildTarget": "Android",    "group": "Android",    "defines": "MY_DEFINE" }
  ]
}
```

| Field | Description |
| --- | --- |
| `buildTarget` | `BuildTargetGroup` enum name (e.g. `Standalone`, `iOS`, `Android`, `WebGL`, `tvOS`, `PS4`, `PS5`, `XboxOne`, `Switch`) |
| `group` | Same as `buildTarget` (mirrors the enum) |
| `defines` | Semicolon-separated define symbols for that target group. Empty string if none set. |

**Use case:** audit define drift across platforms, verify CI define injection landed correctly, or diff symbols before/after a package install.

```sh
/diag script-defines
unifocl exec "/diag script-defines" --agentic --format json --project ./MyProject
```

---

### `diag compile-errors`

Reports whether the project compiles, combining three signals:

1. **Captured compiler messages** from the last compilation pass (collected via `CompilationPipeline.assemblyCompilationFinished`).
2. **Unity's `EditorUtility.scriptCompilationFailed` flag** — authoritative even when no messages were captured (e.g. the daemon domain reloaded since the failing pass).
3. **Expected-vs-actual assembly diff** — every assembly `CompilationPipeline.GetAssemblies()` intends to build must have its output DLL present in `Library/ScriptAssemblies`. An expected-but-missing DLL means that assembly's last compilation failed to emit, and it is reported as a compile failure with a synthetic `Error` message.

When any signal indicates a broken compile state, the response envelope carries **`ok: false`** (a failed exec status), so agents and CI gates cannot mistake a broken project for a clean one. The diagnostic payload is still present in `content`.

> **Note:** This reflects the state of Unity's last compile — it does not trigger a recompilation. Run `/asset refresh` (or `/compile request`) first if you changed scripts.

**Output:**

```json
{
  "op": "compile-errors",
  "assemblyCount": 12,
  "errorCount": 2,
  "warningCount": 0,
  "compilationFailed": true,
  "missingAssemblies": ["Game.Core"],
  "messages": [
    {
      "message": "Assets/Scripts/Player.cs(42,5): error CS0246: The type or namespace name 'Foo' could not be found",
      "type": "Error"
    },
    {
      "message": "assembly 'Game.Core' has no compiled output at Library/ScriptAssemblies/Game.Core.dll — its last compilation failed to emit a DLL; check Editor.log for 'error CS' lines",
      "type": "Error"
    }
  ]
}
```

| Field | Description |
| --- | --- |
| `assemblyCount` | Total number of assemblies discovered by the compilation pipeline |
| `errorCount` | Messages with `type == "Error"` (including synthetic missing-output messages) |
| `warningCount` | Messages with `type == "Warning"` |
| `compilationFailed` | `true` when the project is in a broken compile state (any of the three signals) |
| `missingAssemblies` | Names of assemblies whose expected output DLL is absent from `Library/ScriptAssemblies` |
| `messages[].message` | Full compiler message text; file/line context is embedded (e.g. `Assets/Foo.cs(42,5): error CS0246: ...`) |
| `messages[].type` | `Error` or `Warning` |

**Use case:** surface compile errors without opening the Unity editor console, or gate an agent/CI workflow on the project actually compiling. `ok: true` with `errorCount: 0` is only reported when Unity's own compilation state confirms the last pass succeeded and every expected assembly emitted output.

```sh
/diag compile-errors
unifocl exec "/diag compile-errors" --agentic --format json --project ./MyProject
```

---

### `diag assembly-graph`

Reads `CompilationPipeline.GetAssemblies()` and maps each assembly's direct `assemblyReferences` (the asmdef-level dependencies, not all compiled `.dll` references).

**Output:**

```json
{
  "op": "assembly-graph",
  "assemblyCount": 8,
  "assemblies": [
    { "name": "Assembly-CSharp",       "refs": "Game.Core;Game.UI" },
    { "name": "Game.Core",             "refs": "" },
    { "name": "Game.UI",               "refs": "Game.Core" }
  ]
}
```

| Field | Description |
| --- | --- |
| `assemblyCount` | Total number of assemblies in the compilation pipeline |
| `assemblies[].name` | Assembly name (matches `.asmdef` name field) |
| `assemblies[].refs` | Semicolon-separated names of directly referenced assemblies. Empty if no asmdef references. |

Assemblies are sorted alphabetically by name. Only `assemblyReferences` (direct asmdef deps) are listed — built-in Unity engine references are omitted to keep the graph readable.

**Use case:** understand dependency topology, verify intended isolation, or generate a dependency graph visualization.

```sh
/diag assembly-graph
unifocl exec "/diag assembly-graph" --agentic --format json --project ./MyProject
```

---

### `diag scene-deps`

Calls `AssetDatabase.GetDependencies(scenePath, recursive: true)` for every **enabled** scene in `EditorBuildSettings.scenes` and returns the dependency list per scene.

**Output:**

```json
{
  "op": "scene-deps",
  "sceneCount": 2,
  "scenes": [
    {
      "path": "Assets/Scenes/Main.unity",
      "depCount": 87,
      "topDeps": "Assets/Materials/Ground.mat;Assets/Prefabs/Player.prefab;Assets/Textures/Sky.png"
    }
  ]
}
```

| Field | Description |
| --- | --- |
| `sceneCount` | Number of enabled build-settings scenes processed |
| `scenes[].path` | Scene asset path |
| `scenes[].depCount` | Total number of transitive dependencies (excluding the scene file itself) |
| `scenes[].topDeps` | Semicolon-separated list of up to the first 20 dependency paths, sorted alphabetically |

Only enabled scenes from `EditorBuildSettings.scenes` are scanned (disabled scenes are skipped). The `topDeps` field is capped at 20 entries per scene to keep responses manageable — use `depCount` to gauge total depth.

**Use case:** understand what assets are pulled into each scene, detect unexpectedly heavy scenes, or verify asset isolation between scenes.

```sh
/diag scene-deps
unifocl exec "/diag scene-deps" --agentic --format json --project ./MyProject
```

---

### `diag prefab-deps`

Calls `AssetDatabase.GetDependencies(prefabPath, recursive: true)` for all prefabs found under `Assets/`. Results are capped at the first 100 prefabs to avoid timeout on large projects.

**Output:**

```json
{
  "op": "prefab-deps",
  "prefabCount": 45,
  "prefabs": [
    {
      "path": "Assets/Prefabs/Player.prefab",
      "depCount": 23,
      "topDeps": "Assets/Animations/Run.anim;Assets/Materials/Skin.mat;Assets/Textures/Skin.png"
    }
  ]
}
```

| Field | Description |
| --- | --- |
| `prefabCount` | Total number of prefabs found in `Assets/` (may exceed `prefabs` array length if capped) |
| `prefabs[].path` | Prefab asset path |
| `prefabs[].depCount` | Total transitive dependency count (excluding the prefab itself) |
| `prefabs[].topDeps` | Semicolon-separated list of up to 20 dependency paths, sorted alphabetically |

When `prefabCount > 100`, the `prefabs` array contains the first 100 results and the CLI renders a `(X shown of Y total)` notice.

**Use case:** audit prefab complexity, find prefabs with unexpectedly large dependency trees, or verify that platform-specific assets aren't pulled in by wrong prefabs.

```sh
/diag prefab-deps
unifocl exec "/diag prefab-deps" --agentic --format json --project ./MyProject
```

---

### `diag all`

Runs all five diagnostics in sequence, separated by a visual divider in interactive mode.

```sh
/diag all
unifocl exec "/diag all" --agentic --format json --project ./MyProject
```

---

## ExecV2 Operation Reference

All `diag` operations are registered as `SafeRead` — no approval gating required for agents.

| Operation | Command |
| --- | --- |
| `diag.script-defines` | `/diag script-defines` |
| `diag.compile-errors` | `/diag compile-errors` |
| `diag.assembly-graph` | `/diag assembly-graph` |
| `diag.scene-deps` | `/diag scene-deps` |
| `diag.prefab-deps` | `/diag prefab-deps` |

ExecV2 example:

```json
{
  "operation": "diag.assembly-graph",
  "requestId": "abc123"
}
```

---

## Performance Notes

| Command | Cost | Notes |
| --- | --- | --- |
| `script-defines` | Negligible | In-memory PlayerSettings read |
| `compile-errors` | Negligible | Reads cached compilation state |
| `assembly-graph` | Negligible | Reads cached compilation state |
| `scene-deps` | Moderate | One `GetDependencies` call per enabled scene |
| `prefab-deps` | Moderate–Slow | Up to 100 `GetDependencies` calls; may take several seconds on large projects |

`scene-deps` and `prefab-deps` call `AssetDatabase.GetDependencies` which walks the imported asset database. On first run in a cold Unity session this can be slow. Subsequent calls benefit from in-memory caching.
