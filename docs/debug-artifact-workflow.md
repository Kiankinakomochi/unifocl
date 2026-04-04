# Debug Artifact Workflow

The debug artifact system collects tiered snapshots of a Unity project's state into a single structured JSON file. It is designed for agents and CI pipelines to produce data that feeds into bug reports, markdown summaries, and issue tracker tickets (Jira, Wrike, etc.).

## Tiers

| Tier | What's collected | Approx size |
|------|-----------------|-------------|
| **T0** | PlayerSettings, compile status | ~2-5 KB |
| **T1** | + console errors/warnings, compile errors, 6 validators (scene-list, missing-scripts, packages, build-settings, asmdef, asset-refs) | ~20-50 KB |
| **T2** | + hierarchy snapshot, build report, profiler state, frame timing | ~100-300 KB |
| **T3** | + profiler frames/GC alloc/markers, profiler export summary, recorder output, memory snapshot | ~500 KB-2 MB |

## Workflow: prep → playmode → collect

T0/T1 artifacts can be collected at any time — they only read console logs and validation data that are always available.

T2/T3 artifacts require the profiler (and optionally the recorder) to be running during playmode. The `prep` command handles this setup automatically.

### Interactive CLI

```
# 1. Prep: clears console, starts profiler (T2+), starts recorder (T3)
/debug-artifact prep --tier 3

# 2. Enter playmode and reproduce the issue
/playmode start
# ... interact with the game ...

# 3. Stop playmode and captures
/playmode stop
/profiler stop
/recorder stop

# 4. Collect the artifact
/debug-artifact collect --tier 3
```

### One-shot exec (agentic / CI)

```sh
# Use --session-seed to share state across calls
unifocl exec '/debug-artifact prep --tier 3'  --agentic --project ./MyGame --session-seed dbg
unifocl exec '/playmode start'                --agentic --session-seed dbg
# ... wait for repro ...
unifocl exec '/playmode stop'                 --agentic --session-seed dbg
unifocl exec '/profiler stop'                 --agentic --session-seed dbg
unifocl exec '/recorder stop'                 --agentic --session-seed dbg
unifocl exec '/debug-artifact collect --tier 3' --agentic --session-seed dbg
```

### ExecV2 API (MCP agents)

```json
{"requestId":"1","operation":"debug-artifact.prep","args":{"tier":3}}
{"requestId":"2","operation":"playmode.start"}
// ... wait for repro ...
{"requestId":"3","operation":"playmode.stop"}
{"requestId":"4","operation":"profiling.stop_recording"}
{"requestId":"5","operation":"recorder.stop"}
{"requestId":"6","operation":"debug-artifact.collect","args":{"tier":3,"ticketMeta":{"title":"Camera jitter","severity":"major","labels":["rendering"]}}}
```

## Commands

| Command | Risk level | Description |
|---------|-----------|-------------|
| `/debug-artifact prep [--tier 0\|1\|2\|3]` | PrivilegedExec | Clears console, starts profiler (T2+, deep for T3), starts recorder (T3) |
| `/debug-artifact collect [--tier 0\|1\|2\|3]` | SafeRead | Snapshots current state, writes artifact JSON |

## Output

Artifacts are written to:
```
{projectPath}/.unifocl-runtime/artifacts/{yyyyMMdd-HHmmss}-debug-artifact.json
```

See [`docs/schemas/debug-artifact.schema.json`](schemas/debug-artifact.schema.json) for the full JSON Schema definition.

## Artifact Structure

The artifact JSON contains these top-level sections, populated based on the tier:

| Section | Tier | Source commands |
|---------|:----:|----------------|
| `environment.settings` | 0+ | `settings inspect` |
| `environment.compileStatus` | 0+ | `compile.status` |
| `logs.consoleErrors` | 1+ | `console dump --type error --limit 500` |
| `logs.consoleWarnings` | 1+ | `console dump --type warning --limit 200` |
| `logs.compileErrors` | 1+ | `diag.compile-errors` |
| `validation.sceneList` | 1+ | `validate.scene-list` |
| `validation.missingScripts` | 1+ | `validate.missing-scripts` |
| `validation.packages` | 1+ | `validate.packages` |
| `validation.buildSettings` | 1+ | `validate.build-settings` |
| `validation.asmdef` | 1+ | `validate.asmdef` |
| `validation.assetRefs` | 1+ | `validate.asset-refs` |
| `stateDumps.hierarchySnapshot` | 2+ | `hierarchy.snapshot` |
| `stateDumps.buildReport` | 2+ | `build.report` |
| `stateDumps.buildArtifactMeta` | 2+ | `build.artifact-metadata` |
| `performance.profilerInspect` | 2+ | `profiling.inspect` |
| `performance.frameTiming` | 2+ | `profiling.frame_timing` |
| `performance.frames` | 3 | `profiling.frames` (full frame range) |
| `performance.gcAlloc` | 3 | `profiling.gc_alloc` (full frame range) |
| `performance.markers` | 3 | `profiling.markers` (full frame range) |
| `performance.exportSummaryPath` | 3 | `profiling.export_summary` |
| `media.recorderStatus` | 3 | `recorder.status` |
| `media.memorySnapshotPath` | 3 | `profiling.take_snapshot` |

The `errors` array captures any sub-operation that failed — the artifact is always produced even if some commands fail (e.g., profiler not available, recorder package not installed).

## Ticket Metadata

The `ticketMeta` field is a stub for issue-tracker integration. Agents populate it after analyzing the collected data:

```json
{
  "ticketMeta": {
    "title": "NullReferenceException on scene load",
    "severity": "critical",
    "labels": ["runtime", "scene-loading"],
    "repro": "1. Open MainMenu scene\n2. Press Play\n3. Click 'Start Game'"
  }
}
```

Pass `ticketMeta` as an arg to `debug-artifact.collect` via ExecV2, or have the agent fill it in post-collection by reading and amending the artifact file.

## What prep does per tier

| Tier | Console cleared | Profiler started | Recorder started |
|------|:-:|:-:|:-:|
| 0 | yes | — | — |
| 1 | yes | — | — |
| 2 | yes | yes (standard) | — |
| 3 | yes | yes (deep profiling) | yes |
