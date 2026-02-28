# TUI Keybindings Incremental TODO

## Scope
Implement keyboard-first focus and navigation for:
- Hierarchy view
- Project view
- Inspector view

Do this in small, verifiable slices instead of one large refactor.

## Phase 0: Shared Input Model (Prerequisite)
- [x] Add shared key intent model (Up, Down, Tab, ShiftTab, FocusHierarchy, FocusProject, FocusInspector, Escape).
- [x] Add a tiny input reader utility that translates `Console.ReadKey(intercept: true)` into key intents.
- [ ] Keep existing command-line input behavior unchanged until each view opts in.
- [ ] Add temporary feature flag in session state to enable/disable keyboard navigation mode.

### Acceptance
- [x] App builds with no behavior change by default.
- [x] Key intent mapping is unit-testable/service-isolated.

## Phase 1: Hierarchy First (Requested Priority)
- [x] Add a focus keybinding to enter Hierarchy TUI focus mode.
- [ ] While focused in hierarchy:
- [x] `Up/Down` moves highlighted GameObject row.
- [x] `Tab` expands highlighted node and reveals children.
- [x] `Shift+Tab` collapses highlighted node.
- [x] `Enter` keeps current command behavior (no breaking change).
- [x] `Esc` exits hierarchy focus mode safely.

### Acceptance
- [ ] Highlight index remains valid after refresh.
- [ ] Expand/collapse only affects targeted node.
- [ ] Works with long trees and scrolling viewport.

## Phase 2: Project View Keyboard Navigation
- [x] Add focus keybinding for Project view.
- [ ] While focused in project:
- [x] `Up/Down` moves highlighted file/folder row.
- [x] `Tab` opens/reveals directory contents for highlighted folder.
- [x] `Shift+Tab` navigates back to parent (equivalent to `up` behavior).
- [x] Preserve existing typed commands (`ls`, `cd`, `load`, etc.).

### Acceptance
- [x] Folder expand/reveal is deterministic for repeated key presses.
- [x] Back navigation never escapes `Assets` root.
- [x] Focus survives frame redraw and refresh.

## Phase 3: Inspector View Keyboard Navigation
- [x] Add focus keybinding for Inspector view.
- [ ] While focused in inspector component list:
- [x] `Up/Down` moves highlighted component.
- [x] `Tab` drills into highlighted component fields.
- [x] `Shift+Tab` returns to component list (or exits inspector when already at top level).
- [x] Keep `set`/`toggle` commands working exactly as-is.

### Acceptance
- [x] Component-to-field and field-to-component transitions are stable.
- [x] Existing inspector command stream remains visible and scrollable.

## Phase 4: Keybind Help + Documentation
- [x] Implement `/keybinds` and `/shortcuts` output with mode-specific mappings.
- [x] Add active keybind hints to each focused TUI header.
- [x] Document fallback behavior when terminal cannot emit `Shift+Tab` distinctly.

### Acceptance
- [x] User can discover all bindings from CLI help.
- [x] No undocumented key collisions with current composer input.

## Suggested Implementation Order (One-by-One)
1. Phase 0 (shared key intent + flag)
2. Phase 1 (Hierarchy focus + Up/Down/Tab/ShiftTab)
3. Phase 2 (Project focus + Up/Down/Tab/ShiftTab)
4. Phase 3 (Inspector focus + Up/Down/Tab/ShiftTab)
5. Phase 4 (help/docs polish)

## Notes / Risks
- `Shift+Tab` may appear as `ConsoleKey.Tab` with `Shift` modifier on some terminals; normalize both key and modifier in one place.
- Keep navigation state in mode-specific state objects (avoid global mutable state).
- Prefer additive changes to avoid breaking existing command-based workflows.
