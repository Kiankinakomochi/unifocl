---
bump: patch
---

### Fixed

- `test run` and `test list` no longer report a silent success when Unity never started. A run that
  produces no results file is now an error carrying Unity's exit code and the tail of its output,
  instead of a green `total=0 passed=0 failed=0` result.
- `test` commands detect a held Unity project lock (`Temp/UnityLockfile`) before launching and fail
  with a resolution hint. Unity refuses to open a project twice, so a run started while the project
  is open — including when opened by unifocl's own daemon — aborted before executing any test.
- A stale results XML from an earlier run can no longer be reported as the current run's result; the
  file is cleared first and only accepted if the run that started actually wrote it.
- Unity's stdout/stderr is persisted to `Logs/unifocl-test/unity-<platform>.log` and drained before
  the process handle is released, so fatal startup errors are no longer discarded.

### Changed

- `test run` returns `ok: false` whenever tests failed, matching the documented contract (previously
  a non-zero Unity exit code combined with zero parsed failures could report `ok: true`).
- Test history no longer records runs that produced no results file.

### Documentation

- `docs/test-orchestration.md` gains a **Unity Project Lock** section and corrects the multi-agent
  safety guidance: concurrent `test` runs against the same project path do not work, and the exit
  code table now matches the implementation.
