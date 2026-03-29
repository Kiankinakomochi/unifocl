# Validate & Build Workflow

unifocl ships two complementary command families for project health and build observability:

- **`/validate`** — stateless project health checks that run at any time, before or outside a build
- **`/build` workflow** — preflight, artifact introspection, and failure classification tied to the Unity build pipeline

---

## Shared Output Model

Every validator and build workflow command returns a uniform response envelope. Consumers (humans or agents) can process all results with the same schema.

```json
{
  "validator": "asmdef",
  "passed": false,
  "errorCount": 1,
  "warningCount": 2,
  "diagnostics": [
    {
      "severity": "Error",
      "errorCode": "VASD004",
      "message": "circular dependency detected: 'Game.Combat' -> 'Game.Core'",
      "assetPath": "Assets/Combat/Game.Combat.asmdef",
      "objectPath": null,
      "sceneContext": null,
      "fixable": false
    }
  ]
}
```

| Field | Type | Description |
| --- | --- | --- |
| `severity` | `Error` / `Warning` / `Info` | Severity level of the finding |
| `errorCode` | string | Stable machine-readable code (e.g. `VASD004`) |
| `message` | string | Human-readable description |
| `assetPath` | string? | Project-relative asset path where the issue was found |
| `objectPath` | string? | Hierarchy path inside a scene or prefab |
| `sceneContext` | string? | Scene path when finding originates from a loaded scene |
| `fixable` | bool | Whether unifocl can offer an automatic fix (future) |

---

## Validators

### `validate scene-list`

**Requires daemon.** Reads `EditorBuildSettings.scenes` and checks every entry.

| Code | Severity | Condition |
| --- | --- | --- |
| `VSC001` | Warning | Build settings scenes list is empty |
| `VSC002` | Error | A scene entry has an empty path |
| `VSC003` | Error | Scene path does not exist on disk (fixable) |
| `VSC004` | Info | Scene entry is disabled |

```sh
/validate scene-list
unifocl exec "/validate scene-list" --agentic --format json --project ./MyProject
```

---

### `validate missing-scripts`

**Requires daemon.** Scans all currently loaded scenes and all prefab assets for `null` `MonoBehaviour` components — the symptom of a deleted or renamed script.

| Code | Severity | Condition |
| --- | --- | --- |
| `VMS001` | Error | Null component slot found on a GameObject (fixable) |

Expensive on large projects. Prefab scan iterates `AssetDatabase.FindAssets("t:Prefab")`.

```sh
/validate missing-scripts
unifocl exec "/validate missing-scripts" --agentic --format json --project ./MyProject
```

---

### `validate packages`

**No daemon required.** Reads `Packages/manifest.json` and `Packages/packages-lock.json` from disk.

| Code | Severity | Condition |
| --- | --- | --- |
| `VPK001` | Error | `manifest.json` not found |
| `VPK002` | Warning | `manifest.json` has no `dependencies` block |
| `VPK003` | Warning | Package in manifest but absent from lock file (fixable) |
| `VPK004` | Info | Manifest version differs from resolved lock version |
| `VPK005` | Warning | `packages-lock.json` not found — regenerate by opening Unity (fixable) |
| `VPK006` | Error | Failed to parse package files |

```sh
/validate packages
unifocl exec "/validate packages" --agentic --format json --project ./MyProject
```

---

### `validate build-settings`

**Requires daemon.** Checks `PlayerSettings` and `EditorUserBuildSettings` for common misconfigurations.

| Code | Severity | Condition |
| --- | --- | --- |
| `VBS001` | Error | Application identifier (bundle ID) is empty |
| `VBS002` | Error | Bundle ID contains spaces |
| `VBS003` | Warning | `productName` is empty |
| `VBS004` | Warning | `companyName` is default (`DefaultCompany`) or empty |
| `VBS005` | Warning | `bundleVersion` is empty |
| `VBS006` | Info | `bundleVersion` is a common default (`0.1` / `1.0`) |
| `VBS007` | Warning | Active build target group is `Unknown` |
| `VBS008` | Warning | No enabled scenes in build settings |
| `VBS009` | Info | Active config summary (target, group, scripting backend, scene count) |

```sh
/validate build-settings
unifocl exec "/validate build-settings" --agentic --format json --project ./MyProject
```

---

### `validate asmdef`

**No daemon required.** Recursively finds all `.asmdef` files under `Assets/`, parses them as JSON, builds a dependency graph, and runs three checks.

**Checks performed:**
- **Duplicate names** — two or more `.asmdef` files declare the same `name` field
- **Undefined references** — a `references` entry names an assembly not found in any scanned `.asmdef`
- **Circular dependencies** — depth-first search over the dependency graph detects back edges

| Code | Severity | Condition |
| --- | --- | --- |
| `VASD001` | Error | `Assets/` directory not found |
| `VASD002` | Error | Duplicate assembly name across two or more `.asmdef` files |
| `VASD003` | Warning | A `references` entry names an assembly not defined in any local `.asmdef` |
| `VASD004` | Error | Circular dependency detected between two assemblies |

> Note: `VASD003` (undefined reference) is a warning rather than an error because the referenced assembly may be a Unity built-in, a package assembly, or a platform SDK not scanned by the local file search. Treat it as an error only if the reference name looks like a project assembly.

```sh
/validate asmdef
unifocl exec "/validate asmdef" --agentic --format json --project ./MyProject
```

---

### `validate asset-refs`

**Requires daemon.** Scans `.unity`, `.prefab`, `.asset`, `.mat`, and `.controller` files under `Assets/` for GUID-based asset references. For each unique GUID found in YAML, calls `AssetDatabase.GUIDToAssetPath(guid)` — an empty result means the referenced asset no longer exists.

| Code | Severity | Condition |
| --- | --- | --- |
| `VAR001` | Error | GUID in an asset file resolves to no known asset path |
| `VAR002` | Info | Total number of assets scanned |
| `VAR000` | Warning | Output capped at 500 findings; more broken refs may exist |

The scan deduplicates broken GUIDs — each missing asset is reported once with the first file that references it. On large projects, scanning can take several seconds; assets in `Assets/` only are included (packages and Library are excluded).

```sh
/validate asset-refs
unifocl exec "/validate asset-refs" --agentic --format json --project ./MyProject
```

---

### `validate addressables`

**Requires daemon.** Checks the structural health of an Addressables setup. If the `com.unity.addressables` package is not listed in `Packages/manifest.json`, the validator returns a single `Info` diagnostic and exits cleanly.

| Code | Severity | Condition |
| --- | --- | --- |
| `VADR000` | Info | Addressables package not installed — validation skipped |
| `VADR001` | Error | `Assets/AddressableAssetsData/AddressableAssetSettings.asset` not found (fixable) |
| `VADR002` | Warning | `Assets/AddressableAssetsData/AssetGroups/` directory not found (fixable) |
| `VADR003` | Info | Number of group `.asset` files found in `AssetGroups/` |
| `VADR004` | Warning | Settings asset could not be loaded by `AssetDatabase` |

```sh
/validate addressables
unifocl exec "/validate addressables" --agentic --format json --project ./MyProject
```

---

### `validate all`

Runs every registered validator in sequence. Daemon-required validators are skipped if the daemon is not running and reported as warnings.

```sh
/validate all
unifocl exec "/validate all" --agentic --format json --project ./MyProject
```

---

## Build Workflow

### Build Report Capture

unifocl registers a `IPostprocessBuildWithReport` callback (`BuildReportCapture`) that fires automatically at the end of every Unity build. It serializes the `BuildReport` — files, steps, messages, and summary — to:

```
Library/unifocl-last-build-report.json
```

This file is the data source for `artifact-metadata`, `failure-classify`, and `report`. It is overwritten on each build and lives in `Library/` (not version-controlled).

---

### `build snapshot-packages`

**No daemon required.** Takes a point-in-time snapshot of the package manifest and writes it to:

```
.unifocl-runtime/snapshots/packages-{yyyyMMdd-HHmmss}.json
```

The snapshot file includes: `timestamp` (ISO 8601), `packageCount`, `packages` array (`name`, `version`), and `lockfilePresent`.

Use this before a build to create a baseline for rollback or audit.

```sh
/build snapshot-packages
unifocl exec "/build snapshot-packages" --agentic --format json --project ./MyProject
```

---

### `build preflight`

**Requires daemon.** Orchestrates three validators in sequence and reports an aggregated result:

1. `validate scene-list`
2. `validate build-settings`
3. `validate packages`

Returns a `BuildPreflightResult`:

```json
{
  "passed": true,
  "errorCount": 0,
  "warningCount": 1,
  "sceneList": { "passed": true, ... },
  "buildSettings": { "passed": true, ... },
  "packages": { "passed": true, ... }
}
```

Preflight is designed to be run immediately before `/build run` to surface blocking issues without starting the build. The `build report` command calls preflight automatically.

```sh
/build preflight
unifocl exec "/build preflight" --agentic --format json --project ./MyProject
```

---

### `build artifact-metadata`

**Requires daemon.** Reads `Library/unifocl-last-build-report.json` and returns artifact file metadata.

Response content:

```json
{
  "buildTarget": "StandaloneWindows64",
  "result": "Succeeded",
  "outputPath": "Builds/Win64/MyGame.exe",
  "totalSize": 52428800,
  "buildTime": "00:03:24",
  "fileCount": 87,
  "files": ["Builds/Win64/MyGame.exe", "Builds/Win64/MyGame_Data/..."]
}
```

Returns an error if no build has been captured yet in this project (`Library/` is clean).

```sh
/build artifact-metadata
unifocl exec "/build artifact-metadata" --agentic --format json --project ./MyProject
```

---

### `build failure-classify`

**Requires daemon.** Reads `Library/unifocl-last-build-report.json`, iterates every message in every build step, and classifies each one into a named category.

| Category | Match rule |
| --- | --- |
| `CompileError` | Message matches `CS\d{4}` (Roslyn diagnostic code) |
| `LinkerError` | Message contains `linker` or `stripping` (case-insensitive) |
| `MissingAsset` | Message contains `Missing` and (`asset` or `prefab`) |
| `ScriptError` | Message contains `.cs(` or `ScriptCompilationFailed` |
| `Timeout` | Message contains `timed out` or `timeout` |

Messages that match none of the above are omitted (not classified as `Other`).

Response content:

```json
{
  "hasFailures": true,
  "buildResult": "Failed",
  "totalErrors": 3,
  "failures": [
    { "kind": "CompileError", "stepName": "Compile scripts", "message": "Assets/Scripts/Player.cs(42,5): error CS0246: ..." },
    { "kind": "MissingAsset", "stepName": "Build player", "message": "Missing prefab reference in ..." }
  ]
}
```

```sh
/build failure-classify
unifocl exec "/build failure-classify" --agentic --format json --project ./MyProject
```

---

### `build report`

**Requires daemon.** Runs preflight, then reads `artifact-metadata` and `failure-classify` from the last captured build, and renders a consolidated summary.

```
Build Report — 2026-03-29 10:05:22
────────────────────────────────────────────────
Preflight    PASS   0 errors, 1 warning
Artifacts    87 files   50.0 MB   StandaloneWindows64
Result       Succeeded   build time 00:03:24
Failures     0 classified errors
```

If no build has been captured, the artifact and failure sections report "no build report found" while preflight still runs normally.

```sh
/build report
unifocl exec "/build report" --agentic --format json --project ./MyProject
```

---

## ExecV2 Operation Reference

All validate and build workflow operations are registered as `SafeRead` — they require no approval gating and can be called freely by agents.

| Operation | Command |
| --- | --- |
| `validate.scene-list` | `/validate scene-list` |
| `validate.missing-scripts` | `/validate missing-scripts` |
| `validate.packages` | `/validate packages` |
| `validate.build-settings` | `/validate build-settings` |
| `validate.asmdef` | `/validate asmdef` |
| `validate.asset-refs` | `/validate asset-refs` |
| `validate.addressables` | `/validate addressables` |
| `build.snapshot-packages` | `/build snapshot-packages` |
| `build.preflight` | `/build preflight` |
| `build.artifact-metadata` | `/build artifact-metadata` |
| `build.failure-classify` | `/build failure-classify` |
| `build.report` | `/build report` |
