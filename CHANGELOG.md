# Changelog

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
