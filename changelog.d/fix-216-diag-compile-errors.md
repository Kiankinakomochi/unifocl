---
bump: patch
---

### Fixed
- `/diag compile-errors` no longer reports `✓ 0 error(s)` with `ok: true` while the
  project is in a broken compile state (#216). The command now cross-checks the
  event-captured compiler messages against `EditorUtility.scriptCompilationFailed`
  and diffs the assemblies `CompilationPipeline.GetAssemblies()` expects against
  the DLLs actually present in `Library/ScriptAssemblies`; expected-but-missing
  output is reported as a compile failure. When the project does not compile the
  response carries `ok: false` (failed exec status) plus new `compilationFailed`
  and `missingAssemblies` payload fields, and the CLI renders the failure with
  the captured or synthesized error messages.
- Debug artifact collection now preserves a diagnostic's content payload even
  when the daemon reports `ok: false` for it, so compile-error details from a
  broken project land in the artifact instead of being dropped.
