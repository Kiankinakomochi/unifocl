# Changelog

## 0.27.0 - 2026-03-21

### Changed
- Officialized `0.27.0` by closing the development cycle suffix.
- Added Unity Version Control-aware mutation safety for project filesystem mutations (`mk-script`, `rename-asset`, `remove-asset`) with support for:
  - `uvcs_all` mode (UVCS owns all mutation targets)
  - `uvcs_hybrid_gitignore` mode (ownership resolved from `.gitignore` rules per path)
- Added project VCS profile detection/setup flow:
  - auto-detect UVCS and Git markers
  - one-time interactive setup prompt on first mutation when unconfigured
  - persisted local setup state at `.unifocl/vcs-config.json`
- Extended mutation intent envelopes with additive VCS metadata (`flags.vcsMode`, `flags.vcsOwnedPaths`) and propagated ownership details into project mutation dispatch.
- Hardened project rollback persistence flow with VCS-aware preflight checks and moved transaction stash root to runtime-safe temp storage:
  - default: `<temp>/unifocl-stash/<project-hash>/...`
  - override: `UNIFOCL_PROJECT_STASH_ROOT`
- Extended project dry-run previews to include per-path ownership and checkout expectation hints.
- Added agentic early-return guard for unconfigured UVCS projects:
  - project mutation commands now return `E_VCS_SETUP_REQUIRED` with actionable hint
  - non-mutation agentic commands remain unaffected
- Classified `E_VCS_SETUP_REQUIRED` under validation-class exit code `2`.

## 0.26.0 - 2026-03-20

### Changed
- Officialized `0.26.0` by closing the development cycle suffix.
- Hardened `/init` MCP package installation by moving package install into a dedicated Unity batch process with explicit PID tracking, status-file progress updates, timeout handling, and deterministic teardown.
- Updated MCP install flow to support fallback Git target install and to recursively install missing dependencies by reading installed package `package.json` dependency entries.
- Updated smoke project scaffolding (`setup-smoke-project`) to include `com.unity.modules.imageconversion` by default so generated projects align with Unity Hub-style module availability required by MCP runtime screenshot helpers.

## 0.24.0 - 2026-03-11

### Changed
- Officialized `0.24.0` by closing the development cycle suffix.
- Updated agentic-mode persistence to store and restore deterministic session state by `sessionSeed` and to persist request lifecycle snapshots for status polling:
  - added session/request persistence models and a runtime-backed persistence service under `.unifocl-runtime/agentic/`
  - wired `--session-seed` through CLI `exec` parsing and one-shot execution metadata
  - `/agent/status` now returns tracked request state (running/success/error), timing metadata, and command context instead of a stateless placeholder payload
- Hardened top-level cancellation/shutdown handling in `Program.cs` with a reference-counted cancellation guard so signal/exit callbacks cannot cancel a disposed token source.
- Extended worktree orchestration with `setup-smoke-project` in `src/unifocl/scripts/agent-worktree.sh` to scaffold a minimal Unity project for agentic smoke tests, and documented setup flow updates in `README.md`.
- Implemented previously stub-routed root command coverage in lifecycle handling for `/help`, `/status`, `/doctor`, `/scan`, `/info`, `/logs`, `/examples`, `/update`, and `/install-hook`.
- Replaced generic unimplemented route messaging in interactive and one-shot command paths with explicit unsupported-route guidance.
- Expanded host/stub project bridge handling for `PROJECT_CMD` actions (`upm-list`, `build-targets`, `build-cancel`, and explicit Bridge-required responses for unsupported mutation/build actions).
- Implemented host-mode hierarchy daemon fallback:
  - filesystem-backed `Assets` snapshot and fuzzy-search endpoints
  - host-safe hierarchy command support (`mk`, `rm`, `rename`, `mv`, `toggle`)
  - path-boundary/mutation guardrails (Assets-root confinement, anti path-escape, and anti self-descendant move constraints)
- Updated README command documentation with newly implemented command explanations and host-mode hierarchy fallback behavior.

## 0.23.0 - 2026-03-11

### Changed
- Officialized `0.23.0` by closing the development cycle suffix.
- Hardened CLI cancellation management from top-level entrypoints through one-shot command dispatch by introducing root-token handling, cancellation-aware await boundaries, and explicit cancellation outcomes (`E_CANCELED`/exit `130`) for interrupted agentic executions.
- Hardened daemon/background task lifecycles to reduce zombie/orphan risk:
  - tracked and drained in-flight daemon request tasks during shutdown in CLI and Unity bridge daemons
  - added cancellation-aware HTTP request/response handling for daemon endpoints
  - replaced unbounded/fire-and-forget timeout flows with cancellable timeout sources tied to command completion
- Hardened external-process safety with bounded waits and kill-on-timeout behavior for Unity Hub module installation, git probe/clone operations, and ADB deploy operations.
- Hardened utility/service reliability around process handle lifetime and cancellation-aware dump/monitor behavior to prevent indefinite wait loops under daemon disconnect/cancellation scenarios.
- Added a README persistence-safety section documenting the shipped enterprise mutation contract:
  - transactional mutation intent envelope and daemon transaction coordinator routing
  - memory-layer idempotent serialized mutations with Undo rollback semantics
  - project-layer stash-based filesystem rollback with `.meta` coverage and AssetDatabase refresh
  - dry-run preview behavior and diff integration across CLI/TUI and `agentic.v1` responses

## 0.22.0 - 2026-03-11

### Changed
- Officialized `0.22.0` by closing the development cycle suffix.
- Refactored `ProjectViewService` into an orchestration-focused partial service split by concern (`FocusMode`, `FileOps`, `Upm`) while preserving behavior.
- Extracted dedicated project-view utility services for tree state/navigation, transcript retention, and `mk/make` argument parsing/parent resolution to reduce service complexity.

## 0.21.0 - 2026-03-11

### Changed
- Officialized `0.21.0` by closing the development cycle suffix.
- Refactored CLI bootstrap orchestration by extracting command catalog, boot logo rendering, command parsing, one-shot execution, dump handling, composer input/render pipeline, and IntelliSense (UPM/fuzzy/mk/component) into dedicated services.
- Refactored `ProjectViewService` toward orchestration-focused responsibility by moving payload records into model files and extracting reusable business logic into `ProjectViewServiceUtils`.

## 0.20.0 - 2026-03-11

### Changed
- Officialized `0.20.0` by closing the development cycle suffix.
- Fixed inspector focus-mode body rendering to pin inspector section headers (component header / field table header) while scrolling list contents.
- Fixed inspector focus-mode first-exit behavior after hierarchy-origin transitions so exiting selection mode with `F7` no longer collapses to prompt-only output.
- Fixed hierarchy-to-inspector transition flow to preserve the already-rendered inspector frame by removing transition-time console clear operations.

## 0.19.0 - 2026-03-11

### Changed
- Officialized `0.19.0` by closing the development cycle suffix.
- Hardened trackable progress rendering performance in project mode by reducing redraw cadence for high-frequency spinner/progress updates.
- Hardened build monitor progress rendering by suppressing redundant frame redraws when monitor output is unchanged, with periodic refresh fallback.

## 0.18.0 - 2026-03-11

### Changed
- Officialized `0.18.0` by closing the development cycle suffix.
- Added typed index-jump navigation across non-fuzzy interactive selectors:
  - hierarchy focus mode (`idx` jump to visible object index)
  - project focus mode (`idx` jump to visible entry index)
  - inspector focus mode (`idx` jump for component list and field list)
  - recent project selection mode (`idx` jump to entry)
  - UPM selection/action menus in project mode (`idx` jump to package/action)
- Updated keybind/help and focus-mode UI hints to document `idx` jump behavior in interactive selection flows.
- Added explicit field index rendering in inspector field-list focus view to make typed-jump targets visible.

## 0.17.0 - 2026-03-11

### Changed
- Officialized `0.17.0` by closing the development cycle suffix.
- Fixed interactive composer drift in slash-command typing flows (for example `/b`, `/bu`, `/bui`) by hardening frame redraw anchoring and viewport-constrained rendering.
- Normalized composer line output/newline handling for cross-platform terminal behavior (macOS/Windows) to prevent cursor-column drift during incremental redraw.
- Removed the extra `Input` heading row from composer rendering and added first-input boot-logo collapse so input/suggestion rows remain available in small terminals.

## 0.16.0 - 2026-03-11

### Changed
- Officialized `0.16.0` by closing the development cycle suffix.
- Added README guidance for local Unity compatcheck bootstrap via `./scripts/setup-compatcheck-local.sh`, including generated local artifacts and expected behavior.

## 0.15.0 - 2026-03-11

### Changed
- Officialized `0.15.0` by closing the development cycle suffix.
- Added mandatory worktree bootstrap steps to `AGENT.md` (branch from `main`, pull `origin/main`, init submodules, bump minor/start dev cycle, increment dev cycle on dev builds).
- Fixed inspector mutation error reporting to return actionable details from Bridge and daemon responses.
- Increased inspector mutation request timeout to avoid false failure logs when prefab/scene persistence takes longer.
- Fixed inspector mutation persistence flow to skip preview/unsaveable scenes so successful prefab mutations no longer fail on post-save exceptions.

## 0.14.0 - 2026-03-11

### Changed
- Officialized `0.14.0` by closing the development cycle suffix.
- Fixed Unity bridge detach/reopen lifecycle in editor GUI mode by auto-restarting the bridge listener after `/stop`, allowing `/open` reattach without restarting Unity.
- Fixed Unity bridge wait polling to stop immediately when Unity editor closes and continue with Host mode startup instead of waiting until timeout.
- Added Unity editor lifecycle teardown hooks to explicitly close bridge listener ports on editor quit and before assembly/domain reload, preventing stale zombie endpoints.

## 0.13.0a1 - 2026-03-11

### Changed
- Started the `0.13.0` development cycle with `a1` suffix versioning.
- Fixed project-mode prefab loading so loading `.prefab` assets (including focus mode selection) switches to Hierarchy mode like scene loading.
- Fixed prefab hierarchy snapshots to use loaded prefab contents explicitly (not active-scene roots), so prefab load works whether or not a scene is open.
- Fixed inspector target resolution in prefab context so component lists/fields resolve from current hierarchy roots (prefab or scene), not scene-only roots.
- Improved inspector empty-component diagnostics to explicitly report bridge failure mode, payload status, attached daemon port, hierarchy snapshot root, and target-path resolution outcome.
- Fixed persistence flow for loaded-prefab mutations by explicitly saving loaded prefab contents during mutation/preflight persistence and by preserving preflight save-before-clear ordering on scene load.
- Improved inspector mutation failure messaging to clearly distinguish Unity Bridge mode requirements vs Host/stub daemon mode.
- Bumped bridge protocol to `v7`; projects must re-run `/init` to refresh embedded bridge payload.

## 0.12.1 - 2026-03-11

### Changed
- Officialized `0.12.1` by closing the development cycle suffix.
- Updated composer IntelliSense Enter behavior to execute immediately when input already matches a catalog command trigger.
- Improved hierarchy fuzzy flow by enabling live fuzzy preview suggestions during `f`/`ff` input and ensuring command execution is not blocked by prompt suggestions.
- Added leaf-focused fuzzy suggestion styling across hierarchy/project/inspector results so parent path context is dimmed and the deepest segment is emphasized.

## 0.12.0 - 2026-03-10

### Changed
- Officialized `0.12.0` by closing the development cycle suffix.
- Added project-mode fuzzy `--type`/`-t` argument support (in addition to existing `t:<type>`) for type-scoped fuzzy queries.
- Refactored project mk catalog usage so fuzzy-type parsing/filtering and mk type extension resolution now share `ProjectMkCatalog` as the common source of truth.
- Added inspector ObjectReference search workflow via `set <field> --search <query> [--scene|--project]` and indexed assignment via `set <field> @<index>`.
- Added inspector reference search scope flags:
  - `--scene`: only search scene references
  - `--project`: only search project asset references
  - omitted flags search both scopes.
- Added bridge-side `find-reference` inspector action and ObjectReference assignment support for:
  - `scene:/...` and `scene:/...#Component`
  - `asset:Assets/...` and direct `Assets/...` asset path
  - `null` to clear reference fields.
- Enabled interactive focus-mode editing for inspector `ObjectReference` fields:
  - `Enter` on an `ObjectReference` field now opens a fuzzy reference picker.
  - Supports live query typing, Up/Down selection, Enter apply, Esc cancel, and `Tab` scope cycle (`scene` / `project` / `all`).
- Reworked inspector interactive `ObjectReference` fuzzy picker UI to render in the inspector stream pane (below the main TUI body).
- Hardened ObjectReference type restriction in bridge reference search by resolving expected reference types from serialized property metadata (`PPtr<...>`) when reflection cannot resolve the backing field.
- Reference suggestions for typed fields (for example animation controller references) are now filtered to compatible reference types instead of broad `UnityEngine.Object` matches.
- Styled the selected row in inspector ObjectReference fuzzy picker preview with theme cursor highlight colors and fixed bracket-cell markup escaping for reliable Spectre rendering.
- Bumped bridge protocol to `v6`; projects must re-run `/init` to refresh embedded bridge payload.

## 0.12.0a6 - 2026-03-10

### Changed
- Fixed inspector reference-picker preview markup rendering by escaping literal bracket cells (`[index]`, `[scope]`) and using valid Spectre markup tags for styled rows.

## 0.12.0a5 - 2026-03-10

### Changed
- Styled the selected row in inspector ObjectReference fuzzy picker preview with theme cursor highlight colors to improve visual focus and readability.

## 0.12.0a4 - 2026-03-10

### Changed
- Reworked inspector interactive `ObjectReference` fuzzy picker UI to render in the inspector stream pane (below the main TUI body), removing the in-body overlay for this flow.
- Hardened ObjectReference type restriction in bridge reference search by resolving expected reference types from serialized property metadata (`PPtr<...>`) when reflection cannot resolve the backing field.
- Reference suggestions for typed fields (for example animation controller references) are now filtered to compatible reference types instead of broad `UnityEngine.Object` matches.

## 0.12.0a3 - 2026-03-10

### Changed
- Enabled interactive focus-mode editing for inspector `ObjectReference` fields:
  - `Enter` on an `ObjectReference` field now opens a fuzzy reference picker instead of showing "interactive edit not available".
  - Supports live query typing, Up/Down selection, Enter apply, Esc cancel, and `Tab` scope cycle (`scene` / `project` / `all`).

## 0.12.0a2 - 2026-03-10

### Changed
- Added inspector ObjectReference search workflow via `set <field> --search <query> [--scene|--project]` and indexed assignment via `set <field> @<index>`.
- Added inspector reference search scope flags:
  - `--scene`: only search scene references
  - `--project`: only search project asset references
  - omitted flags search both scopes.
- Added bridge-side `find-reference` inspector action and ObjectReference assignment support for:
  - `scene:/...` and `scene:/...#Component`
  - `asset:Assets/...` and direct `Assets/...` asset path
  - `null` to clear reference fields.

## 0.11.0 - 2026-03-10

### Changed
- Officialized `0.11.0` by closing the development cycle suffix.
- Made command IntelliSense context-aware so invalid preconditions are no longer suggested (for example `/project`, `/hierarchy`, `/inspect`, `/build*`, `/upm*` before opening a project).

## 0.11.0a1 - 2026-03-09

### Changed
- Started the `0.11.0` development cycle with `a1` suffix versioning.

## 0.10.0a2 - 2026-03-09

### Changed
- Hardened inspector set/update logs to display the applied field value returned from Bridge refresh (including clamped results), instead of echoing raw user input.
- Bumped bridge protocol to `v5`; projects must re-run `/init` to refresh embedded bridge payload.

## 0.10.0a1 - 2026-03-09

### Changed
- Started the `0.10.0` development cycle with `a1` suffix versioning.
- Added inspector numeric sanity clamping in bridge-side field mutation:
  - `Integer` and `Float` assignments now respect serialized-field `[Range]` and `[Min]` attributes.
  - `Color` channel inputs are now clamped to `[0, 1]`.
  - Non-finite numeric inputs (NaN/Infinity) are now rejected for numeric/vector assignments.

## 0.9.0 - 2026-03-09

### Changed
- Officialized `0.9.0` by closing the development cycle suffix.
- Added inspector component-list workflow parity with project mode via `component add` / `component remove`, including fuzzy catalog IntelliSense and `DisallowMultipleComponent` guards in bridge mutations.
- Standardized focus-mode controls to `F7` as the sole toggle across project/hierarchy/inspector contexts and updated keybind labels/docs accordingly.
- Fixed inspector TUI resilience around focus-exit transitions:
  - preserve component-list rendering on first focus-mode exit
  - remove redundant focus on/off stream logs
  - cap inspector component IntelliSense to first 10 visible suggestions with overflow indicator
- Fixed prompt badge markup escaping so literal badges render correctly (`[inspect]`, `[safe]`) instead of leaking raw style tags.
- Bumped bridge protocol to `v4`; projects must re-run `/init` to refresh embedded bridge payload.

## 0.9.0a2 - 2026-03-09

### Changed
- Fixed inspector `comp` / `component` IntelliSense candidate duplication by enforcing unique component suggestion entries.
- Fixed inspector composer prompt badge markup escaping so literal badges render correctly (`[inspect]`, `[safe]`) instead of leaking raw style tags.

## 0.9.0a1 - 2026-03-09

### Changed
- Started the `0.9.0` development cycle with `a1` suffix versioning.
- Added inspector component-list workflow parity with project mode via `component add` / `component remove`, including fuzzy catalog IntelliSense and `DisallowMultipleComponent` guards in bridge mutations.

## 0.8.0 - 2026-03-09

### Changed
- Officialized `0.8.0` by closing the development cycle suffix.
- Added agentic one-shot execution entrypoint: `unifocl exec "<command>" --agentic [--format json|yaml]` with deterministic response envelopes and standardized exit codes.
- Added deterministic `/dump` command family (`/dump hierarchy|project|inspector`) with JSON/YAML serialization for LLM context snapshots.
- Added daemon-side agentic HTTP surface (`/agent/exec`, `/agent/capabilities`, `/agent/status`, `/agent/dump/{category}`) aligned to the new envelope contract.
- Integrated concurrent worktree orchestration into the agentic workflow:
  - agent-capability discovery and OpenAPI docs now cover agent endpoints and machine-format contracts.
  - README agentic guidance now cross-links isolated worktree provisioning + dynamic daemon startup scripts for parallel agents.

## 0.7.1 - 2026-03-09

### Changed
- Officialized `0.7.1` by closing the development cycle suffix.
- Added agent worktree orchestration scripts for bash and PowerShell.
- Consolidated lifecycle and milestone progress guidance directly into `README.md`.
- Replaced standalone lifecycle/milestone docs with GitHub Milestone tracking guidance in `README.md`.

## 0.7.1a2 - 2026-03-09

### Changed
- Merged agent lifecycle pipeline and operating-boundary documentation directly into `README.md`.
- Replaced local milestone-tracker doc references with GitHub Milestone tracking guidance in `README.md`.
- Removed standalone docs now superseded by README:
  - `AGENT_WORKTREE_LIFECYCLE.md`
  - `MILESTONE_WORKTREE_ISOLATION.md`

## 0.7.1a1 - 2026-03-09

### Added
- Added agent worktree orchestration scripts for bash and PowerShell:
  - `src/unifocl/scripts/agent-worktree.sh`
  - `src/unifocl/scripts/agent-worktree.ps1`

### Changed
- Added README guidance for isolated worktree provisioning, Library cache seeding, and dynamic daemon port startup.
- Started the `0.7.1` development cycle with `a1` suffix versioning.

## 0.7.0 - 2026-03-09

### Changed
- Officialized `0.7.0` by closing the development cycle suffix.
- Fixed project mk root-parent validation by accepting `Assets`/`Assets/` as valid creation parent paths in bridge validators.
- Relaxed project mk IntelliSense/fuzzy candidate gating so type suggestions appear reliably whenever project mode is active with an open project.
- Updated project mk fuzzy suggestion flow:
  - type-first suggestions without argument noise during initial selection
  - usage hints only after type selection/continued typing
  - full list of matching mk type candidates (no 10-line cap)
- Added project asset creation name-collision handling with `_x` style suffix increments (`_1`, `_2`, ...).
- Added project remove bulk range support: `remove <startIdx:endIdx>` (inclusive).
- Added trackable progress spinner for project asset creation and remove operations.
- Fixed mk fuzzy suggestion commit behavior so Enter prioritizes suggestion insertion during type-selection phase.

## 0.6.0 - 2026-03-09

### Changed
- Added typed project-mode asset creation workflow aligned with hierarchy `mk` ergonomics:
  - `make --type <type> [--count <count>] [--name <name>|-n <name>]`
  - `mk <type> [count] [--name <name>|-n <name>]`
- Added project mk type catalog and alias normalization for a broad Unity asset/file set (scene/prefab/script/asmdef/shader/material/animation/UI/render pipeline/addressables/localization/testing and related asset families).
- Added daemon-side `mk-asset` action for project mode to create typed assets and return created paths.
- Updated project command hints and command palette entries to advertise typed `mk` usage.

## 0.5.3 - 2026-03-09

### Changed
- Officialized `0.5.3` by closing the development cycle suffix.
- Fixed project-context empty-input routing so redraw requests execute `ProjectViewService` rendering instead of returning early from the router.
- Fixed project-mode rendering transition after hierarchy exit by forcing a project frame refresh when returning from `/hierarchy` without entering inspector mode.
- Applied the same redraw guard to hierarchy auto-enter return paths triggered by scene load transitions.

## 0.5.2 - 2026-03-09

### Changed
- Officialized `0.5.2` by closing the development cycle suffix.
- Refactored TUI progress visuals into a shared `TuiTrackableProgress` renderer for spinner and progress-bar output across build and project UPM flows.
- Added active progress feedback during `/recent` and `/init` execution, and scene loading (`load <scene>`) in project mode.

## 0.5.1 - 2026-03-09

### Changed
- Added `DaemonScenePersistenceService` as the shared save pipeline for Unity scene persistence operations.
- Added `DaemonSceneManager` to centralize active-scene resolution and scene load/activation transitions for editor bridge services.
- Hierarchy and inspector mutation flows now persist scenes through the shared persistence service.
- Project scene-load flow now runs a shared persistence preflight save path before scene switching.
- Embedded package payload generation now includes `DaemonSceneManager.cs` and `DaemonScenePersistenceService.cs` so `/init` installs compile-complete bridge sources.
- Bumped bridge protocol to `v3`; projects must re-run `/init` to refresh embedded bridge payload.

## 0.5.0 - 2026-03-09

### Patch Train
- `a1`: Hierarchy-to-inspector transition workflow.
- `a2`: Hierarchy interactive-selection transition reliability and expansion-state preservation.
- `a3`: Inspector transition rendering and bridge payload compatibility fixes.
- `a4`: Inspector `Esc` staged navigation and hierarchy-return flow.
- `a5`: Inspector focus-mode toggle on `F7` and serialized-value `edit` command support.
- `a6`: Interactive field edit workflow in inspector focus mode (vector and enum handling).
- `a7`: Interactive edit cursor visibility improvements in inspector field selection.
- `a8`: Inspector field-edit UX refinement (styled cursor marker, bool cycle edit, numeric keyboard input).
- `a9`: String overlay editing and focus-mode auto-scroll improvements for long inspector lists.
- `a10`: Selection-mode scroll stabilization for inspector focus navigation.
- `a11`: Vector/color component keyboard-entry fix for interactive numeric editing.
- `a12`: Inspector markup rendering fix for cursor/list rows with bracketed literals.
- `a13`: Inspector focus-mode jitter fix via ANSI escape-sequence key normalization.
- `a14`: Extended ANSI escape handling for arrow-key variants to prevent inspector focus flicker.
- `a15`: Persist hierarchy scene mutations immediately so hierarchy object edits survive Unity editor restarts.
- `a16`: Fix scene-save guard regression and persist inspector component mutations to survive editor restarts.

### Added
- Hierarchy mode now supports `inspect <idx|name>` and `ins <idx|name>` to jump from hierarchy selection to inspector mode.

### Changed
- Officialized `0.5.0` release by removing the development suffix from CLI version output.
- Hierarchy focus mode now distinguishes transition keys: `Tab` expands selected nodes, `Enter` transitions to inspector for the selected object.
- Entering hierarchy interactive selection no longer collapses previously expanded hierarchy nodes.
- Hierarchy-originated inspector transitions now pass through contextual routing reliably in hierarchy context.
- Keybind help text was updated to describe the new hierarchy focus `Tab` behavior.
- `inspect` now enters inspector focus TUI immediately after a successful transition so the inspector frame remains active.
- Inspector bridge request payloads now serialize in camelCase for Unity-side `JsonUtility` compatibility, enabling component and serialized-field retrieval.
- Inspector focus `Esc` navigation is now staged:
  - In component field inspection, `Esc` returns to the component list.
  - In component list, `Esc` returns to hierarchy mode.
- Added inspector `edit <field> <value...>` command (alias `e`) to update serialized component values using the same bridge-backed mutation flow as `set`.
- While inspecting a component, `F7` now toggles between interactive selection mode and command input (`F7` enters focus mode from command input and exits focus mode back to command input).
- In inspector component-field selection mode, pressing `Enter` now opens interactive field edit flow.
- Interactive field edit supports vectors (`Tab` switch component, `←/→` adjust value, `Enter` apply, `Esc` cancel).
- Interactive field edit supports enum dropdown-style option cycling (`Tab`/`←/→` cycle options, `Enter` apply, `Esc` cancel).
- Inspector field transport now includes enum option lists from Unity bridge so enum dropdown editing is populated with concrete choices.
- Inspector field editor now renders a dedicated edit cursor state:
  - shows an explicit `EDITING <field>` status row with current part index.
  - highlights the actively edited vector component within the value cell.
  - wraps currently selected enum option value to distinguish active edit target.
- Removed full-row selection highlight in inspector field/component lists and replaced it with styled cursor markers for clearer edit targeting.
- Added boolean interactive edit with enum-like controls (`Tab`/`←`/`→` cycle, `Enter` apply, `Esc` cancel).
- Added direct keyboard input flow for numeric fields (`int`/`float`) with local validation before apply.
- Added keyboard text editing for string fields with a dedicated overlay that shows the full current value and insertion cursor.
- Inspector focus mode now keeps highlighted component/field visible by auto-scrolling with `↑/↓` when list rows exceed available viewport height.
- Smoothed inspector selection-mode scrolling by removing placeholder row substitution during body viewport slicing and using margin-based offset updates to reduce jitter.
- Fixed vector/color interactive edit flow to accept direct numeric keyboard entry per component (including backspace/delete), with per-component validation before apply.
- Fixed inspector styled-row markup escaping issue by escaping bracketed literal cells (`[index]`, `[type]`) so color tags render correctly instead of printing raw tag text.
- Normalized ANSI escape-sequence input parsing for arrow keys/back-tab in keyboard focus loops to prevent `Esc` mis-detection and resultant inspector/general TUI flicker during scrolling.
- Added generic CSI sequence mapping (`ESC [ ... A/B/C/D/Z`) and unknown-sequence suppression so terminals emitting extended arrow variants no longer trigger unintended `Esc` transitions.
- Hierarchy mutations (`mk`, `toggle`, `rename`, `rm`, `mv`) now persist by saving affected scenes after mutation instead of only marking dirty.
- Removed path-based save guard from hierarchy persistence (`SaveScene` is now attempted for valid scenes regardless of `scene.path`) and added warning logs on failed save attempts.
- Inspector component/field mutations now also persist scene changes immediately (`toggle-component`, `toggle-field`, `set-field`).

## 0.4.1 - 2026-03-09

### Patch Train
- `a2`: Protobuf-contract integration pass for hierarchy `mk` command typing and validation.

### Added
- Added `HierarchyMkType` enum to protobuf contracts (`external/unifocl-protobuf/contracts/hierarchy.proto`) as the shared source of truth for hierarchy create types.

### Changed
- Hierarchy `mk` / `make` command parsing now validates type tokens against protobuf-generated contract enum values.
- Hierarchy `mk` / `make` now normalize accepted aliases and send canonical contract type names to the daemon bridge.

## 0.4.0 - 2026-03-09

### Patch Train
- `a1`: Incremental development pass for hierarchy/inspector command completeness and typed object creation.

### Added
- Hierarchy mode now supports `make --type <type> [--count <count>]` and `mk <type> [count]`.
- Inspector mode now supports typed creation via `make`/`mk`.
- Typed `mk` object creation support for UI, 3D primitives, lights, 2D objects, camera/audio, and empty structural objects.
- `mk` suggestions and fuzzy matching now include common typed creation shortcuts directly in hierarchy IntelliSense.

### Changed
- Unified hierarchy command payload to carry typed create parameters (`type`, `count`) across CLI and Unity bridge.
- Inspector command metadata now documents typed `make`/`mk` forms.
- Standardized changelog file casing to `CHANGELOG.md` for GitHub tooling compatibility.

## 0.3.2a6 - 2026-03-09

### Changed
- Adopted development incremental versioning with `aX` suffixes (current CLI version: `0.3.2a6`).
- Added AGENT build/versioning rule requiring `aX` increments for development builds.
- Added automatic Bridge restart + one-time retry for UPM install/remove/update when project command times out but daemon ping remains healthy.
- Added post-timeout state verification for `upm update` to avoid false negatives when Unity applied the update but response completion timed out.
- Added runtime-restart detection during UPM project commands to stop waiting full timeout when Unity domain reload interrupts the in-flight response.
- Added daemon project-command status endpoint with runtime step telemetry and requestId tracking, and updated UPM transport to use status polling for early completion/failure detection instead of waiting full timeout.
- Fixed daemon startup regression by removing non-main-thread `EditorApplication.isUpdating/isCompiling` access and switching status reporting to main-thread-cached flags.

## 0.3.1 - 2026-03-08

### Added
- Hierarchy mode now provides in-prompt IntelliSense with command signatures, argument usage, and descriptions.
- Hierarchy mode prompt now supports suggestion navigation and insertion (`↑/↓` + `Enter`) plus `Esc` dismissal behavior.

### Changed
- Project composer IntelliSense now uses inspector-specific command suggestions while in inspector context.
- Inspector command suggestions now include context-specific command usage and explanations rather than only project-wide command listings.

## 0.3.0 - 2026-03-06

### Added
- New `/build` command suite and aliases:
  - `/build run [target] [--dev] [--debug] [--clean] [--path <output-path>]`
  - `/build exec <Method>`
  - `/build scenes`
  - `/build addressables [--clean] [--update]`
  - `/build cancel`
  - `/build targets`
  - `/build logs`
  - Aliases: `/b`, `/bx`, `/ba`
- Interactive build-target selection when target is omitted.
- Target install-then-build flow for missing modules.
- Build target caching (refresh on explicit install flow).
- In-terminal live build monitor with pinned status/log header.
- Restartable build log tail and cancelled-build log snapshot view.
- Build diagnostics expansion:
  - heartbeat and stall hints
  - last diagnostic / last exception reporting
  - log file path shown in monitor and summary

### Changed
- Build cancellation now performs daemon teardown and returns to Project mode after keypress.
- Build output routing now supports explicit `--path`.
- Default build outputs are isolated in timestamped folders to avoid artifact collisions.
- Main-thread dispatch safety improved for build execution and daemon stop paths.
- Added spinner/status UX for heavy build-related steps.

### Fixed
- Resolved Unity main-thread violation during daemon stop (`EditorApplication.Exit` off main thread).
- Improved resilience for detach/reattach with ongoing builds.
