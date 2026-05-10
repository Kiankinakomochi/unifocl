---
bump: minor
---

### Added
- `/compile request --force` triggers a clean recompile by passing `RequestScriptCompilationOptions.CleanBuildCache` to Unity's compilation pipeline. Previously there was no way from the CLI to bust Unity's incremental cache.
- `/compile request --wait` blocks until the compile and any subsequent domain reload have fully settled. The CLI polls a result file the daemon writes to `Temp/unifocl/compile-results/{requestId}.json`, plus Unity's own `Temp/compiling.lock` and `Temp/domainreload.lock` markers, so completion is observable even when the daemon HTTP socket is torn down during the reload that follows a successful compile.
- `compile.request` exec op now accepts `forceRecompile` and `waitForDomainReload` arguments, so MCP/agentic callers get the same controls.
- `CompilationStateValidationService` rejects compile requests up front when the editor is already compiling, importing, in domain reload, or contains duplicate `.asmdef` names — replacing silent stalls with actionable errors.
- Compile-did-not-start watchdog: if `EditorApplication.isCompiling` does not flip true within 4 s of `RequestScriptCompilation`, the daemon writes an `outcome=indeterminate` result file with a diagnostic listing the likely causes (Auto Refresh disabled, no script changes, duplicate asmdefs, locks held).

### Fixed
- `/compile request` now calls `AssetDatabase.Refresh()` before `RequestScriptCompilation`, so newly copied `.cs` files are picked up reliably without depending on the OS-level window-grab. The window-grab is kept as a cosmetic, opt-in fallback.
- Editor bridge install manifest now ships `CompilationStateValidationService.cs` and `CompileResultPersistenceService.cs` so the new compile flow links cleanly when the bridge is materialized into a target project.
- Daemon no longer drops compile completion state on domain reload. Compile results are persisted to disk by `CompileResultPersistenceService` before the AppDomain is torn down, so a CLI that started before the reload can observe the result after the daemon comes back online.
- `compile-request` now propagates a per-request `requestId` end to end (CLI → daemon → result file), eliminating the previous shared-in-memory state field that could be trampled by overlapping callers.
- Domain-reload state is now tracked via Unity `SessionState`, so the daemon can distinguish "never compiled" from "compiled and just reloaded" after the AppDomain comes back.

### Background
Compile completion is now signalled through files on disk plus Unity's own lock-file markers rather than relying on the daemon HTTP socket, which can be torn down by the domain reload that follows a successful compile. Polling the on-disk markers lets the CLI observe completion across the reload boundary instead of seeing connection-refused at the moment the answer matters most.
