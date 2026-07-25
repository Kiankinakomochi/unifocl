---
bump: minor
---

### Added

- **EditMode tests now run inside the attached editor.** With a project open, `test run editmode`
  and `test list` drive Unity's `TestRunnerApi` in the editor that already holds the project
  instead of launching a second Unity, so the project lock is never contended. Previously these
  commands could not succeed at all while a project was open — Unity refuses to open the same
  project twice, and every `/open` mode holds the lock.
- Test names and assemblies from `test list` are now read from the test tree when running
  in-editor, replacing the stdout-scraping heuristic that could report Unity banner lines
  (e.g. `Branch: 6000.4/staging`) as test cases.
- The editor bridge ships a new `UniFocl.EditorBridge.TestRunner` assembly, constrained to
  `UNITY_TESTS_FRAMEWORK`. Unity skips it when `com.unity.test-framework` is absent, and unifocl
  reports that explicitly rather than failing obscurely.

### Fixed

- The agentic log scanner no longer treats `failed` inside an identifier as a command failure.
  `test list` prints one line per test case, so names such as
  `Purchase_GrantFailure_ReturnsGrantFailed` (trailing fragment) or `Foo.FailedStateTests`
  (leading fragment) reported a successful listing as `status: error` with exit code 2. The match
  is now anchored to word boundaries on both sides. This classifier runs over every agentic exec,
  not just `test list`.
- Diagnostics for a failed subprocess run now search both output streams for Unity's fatal
  batchmode banner. Previously the first non-empty stream won outright, so any licensing noise on
  stderr suppressed the actual cause — which Unity writes to stdout.
- A failure reported by the editor without a message no longer falls through to a green
  zero-count summary.
- `DispatchTestJobAsync` reports cancellation as a structured error instead of throwing
  `OperationCanceledException` out of a method whose callers promise ExecV2 a structured result.

### Changed

- `test run editmode` / `test list` select their execution path automatically: in-editor when a
  daemon is answering for the project, subprocess otherwise. Both write the same NUnit v3 XML to
  `Logs/unifocl-test/`, so the structured output contract is unchanged.
- PlayMode continues to use the subprocess path and therefore still requires the project to be
  closed.
- Completion is signalled through `Temp/unifocl/test-results/<requestId>.json` rather than the
  daemon response, so a domain reload during a run cannot swallow the result — the same on-disk
  handoff `/compile` uses. Markers older than six hours are swept on the next job so they cannot
  accumulate one file per run.
- The editor-side `ICallbacks` implementation registers once per domain rather than once per run.
  Unity's `CallbacksHolder` appends without deduplicating, so the previous code left `RunFinished`
  firing once for every run ever started, and leaked a `TestRunnerApi` ScriptableObject each time.

### Documentation

- `docs/test-orchestration.md` documents both execution paths, and records that `/close` releases
  the project lock in host mode but not in bridge mode, where it detaches the session and leaves
  the GUI editor running.
