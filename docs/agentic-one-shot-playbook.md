# Agentic One-Shot Playbook (Current State)

This note captures the current, post-fix behavior for `unifocl` one-shot agentic mode and how to run steps 4-6 reliably.

## What Is Fixed

1. `/hierarchy` now works in one-shot flows and updates context correctly.
2. Hierarchy-mode one-shot commands are routed without interactive TUI.
3. UI object parenting is stable when creating under a canvas (scene move + parent ordering fixed).
4. Inspector one-shot path resolution is more robust:
   - scene-root-prefixed paths are normalized
   - Unity-style suffixes like `Name (1)` are resolved leniently
5. Root-level `set <field> <value>` works when the field is uniquely resolvable across components.
6. Step 5-6 layout/scaler changes were verified to persist in scene YAML.

## One-Shot Golden Path (Steps 4-6)

1. Keep a single session context across the full chain:
   - use the same `--session-seed`
   - keep daemon/attach target stable for the sequence
2. Use this mode ordering:
   - `/open <project>`
   - `load <scene>`
   - `/hierarchy`
   - hierarchy `mk` operations
   - `/inspect <path-or-id>` for component/property mutations
3. After each mutation group, verify with:
   - `/dump hierarchy`
   - `/dump inspector`
   - scene YAML spot-check for persisted values
4. For FHD height-match canvas in one-shot:
   - `CanvasScaler.uiScaleMode = ScaleWithScreenSize`
   - `CanvasScaler.referenceResolution = 1920x1080`
   - `CanvasScaler.matchWidthOrHeight = 1`
5. For start screen layout, set anchored positions/sizes explicitly for title and buttons instead of relying on defaults.

## Best Practices For Future Agents

1. If build/type issues appear around shared contracts, run:
   - `git submodule sync --recursive`
   - `git submodule update --init --recursive`
2. Treat one-shot success as provisional until state is verified from dumps or YAML.
3. Prefer path-based targeting over index-only targeting when possible.
4. Keep hierarchy and inspector actions grouped by intent:
   - create/parent in hierarchy mode
   - mutate component fields in inspector mode
5. When running via `dotnet run`, parse the JSON envelope from first `{` because build logs may precede it.

## Residual Risks / Caveats

1. Daemon warmup/attach latency still exists right after `/open`; immediate follow-up commands may need retry logic in scripts.
2. Index-based `inspect <idx>` remains cwd-sensitive; path-based inspect is safer for deterministic runs.
3. Name collisions can still introduce suffixes (`(1)`, `(2)`); validate the final object path from hierarchy dump before inspector mutations.

## What Made Earlier Attempts Hard

These are practical friction points, not user mistakes:

1. Pure one-shot + no TUI removed fallback flows while core one-shot hierarchy/inspector paths were still maturing.
2. Full UI composition plus layout persistence in a single agentic run exposed multiple codepaths (routing, parenting, path resolution, serialization) at once.
3. Validation needed both runtime dumps and YAML checks, which increases token/tool overhead but is required for confidence.

## Recommended Next Hardening

1. Add a CLI-level one-shot smoke test that covers:
   - `/hierarchy`
   - parented `mk`
   - inspector `set` persistence for `CanvasScaler` and `RectTransform`
2. Add a command to print current hierarchy cwd + selected node in one-shot mode.
3. Add an optional strict mode that fails a mutation command when expected post-state does not match.
