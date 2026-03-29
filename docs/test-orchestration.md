# Test Orchestration

unifocl's `test` commands invoke Unity's built-in test runner as a **direct subprocess** — separate from the daemon, independent of any running editor, and safe to call from CI pipelines, parallel agent sessions, or any headless environment.

---

## Commands

### `test list`

Lists all available EditMode tests in the project.

```
/test list
unifocl exec "test list" --agentic --format json --project <path>
```

**Output:**

```json
[
  { "testName": "MyTests.MathTests.AdditionWorks", "assembly": "MyTests" },
  { "testName": "MyTests.MathTests.SubtractionWorks", "assembly": "MyTests" }
]
```

- Assembly is derived from the first segment of the fully-qualified test name.
- Unity log noise (initialization lines, warnings) is filtered from the output.
- Uses `-testPlatform EditMode -listTests` internally. PlayMode test listing is not supported by Unity's CLI.

---

### `test run editmode`

Runs all EditMode tests and returns a structured result.

```
/test run editmode [--timeout <seconds>]
unifocl exec "test run editmode" --agentic --format json --project <path>
unifocl exec "test run editmode --timeout 300" --agentic --format json --project <path>
```

**Flags:**

| Flag | Default | Description |
| --- | --- | --- |
| `--timeout <seconds>` | `600` | Hard kill timeout. The Unity subprocess is killed (entire process tree) when exceeded. |

**Output:**

```json
{
  "total": 42,
  "passed": 40,
  "failed": 2,
  "skipped": 0,
  "durationMs": 8340.5,
  "artifactsPath": "<project>/Logs/unifocl-test",
  "failures": [
    {
      "testName": "MyTests.SomeTest.FailingCase",
      "message": "Expected 1 but was 2.",
      "stackTrace": "at MyTests.SomeTest.FailingCase () [0x00001] in <...>:0",
      "durationMs": 12.3
    }
  ]
}
```

---

### `test run playmode`

Runs all PlayMode tests. PlayMode may trigger a player build before running, which can significantly extend the runtime — set `--timeout` accordingly.

```
/test run playmode [--timeout <seconds>]
unifocl exec "test run playmode" --agentic --format json --project <path>
unifocl exec "test run playmode --timeout 3600" --agentic --format json --project <path>
```

**Flags:**

| Flag | Default | Description |
| --- | --- | --- |
| `--timeout <seconds>` | `1800` | Hard kill timeout. Increase for projects with heavy player build steps. |

Output contract is identical to `test run editmode`.

---

## ExecV2 Operations

Both operations are available via the structured `POST /agent/exec` endpoint.

| Operation | Risk level | Args |
| --- | --- | --- |
| `test.list` | `SafeRead` | _(none)_ |
| `test.run` | `PrivilegedExec` | `platform` (`EditMode` or `PlayMode`), `timeoutSeconds` (optional) |

`test.run` is `PrivilegedExec` because it launches an external process against your project. It returns `ApprovalRequired` on first call; confirm by re-sending with the approval token.

**ExecV2 request examples:**

```json
{ "operation": "test.list", "requestId": "req-tl-01" }
```

```json
{
  "operation": "test.run",
  "requestId": "req-tr-01",
  "args": { "platform": "EditMode", "timeoutSeconds": 300 }
}
```

```json
{
  "operation": "test.run",
  "requestId": "req-tr-01",
  "args": { "platform": "EditMode" },
  "intent": { "approvalToken": "<token-from-ApprovalRequired-response>" }
}
```

---

## Execution Model

unifocl resolves the Unity editor for the project via `UnityEditorPathService` (same path used by `/open` and `build.run`), then launches Unity with:

```
Unity -projectPath <path> -runTests -testPlatform <EditMode|PlayMode> -testResults <artifacts/test-results.xml> -batchmode -nographics
```

For `test list`, `-listTests` replaces `-batchmode -nographics` and results come from stdout.

**Subprocess lifecycle:**

- stdout and stderr are captured concurrently via async `OutputDataReceived` / `ErrorDataReceived` handlers.
- A linked `CancellationTokenSource` combines the user-supplied `CancellationToken` with a timeout token.
- On cancellation or timeout, `process.Kill(entireProcessTree: true)` is called to ensure no orphaned Unity instances.

**Artifacts:**

All run artifacts land in `<projectPath>/Logs/unifocl-test/`:

| File | Content |
| --- | --- |
| `test-results-editmode.xml` | NUnit v3 XML from EditMode runs |
| `test-results-playmode.xml` | NUnit v3 XML from PlayMode runs |
| `test-list.txt` | Raw `-testResults` output from list runs |

If Unity crashes before writing the XML file, `test run` returns a zero-count result with an empty `failures` array rather than an error, so callers can distinguish a clean zero-test project from a hard crash by checking `total`.

---

## Multi-Agent Safety

Because `test` commands run as isolated subprocesses with no shared daemon state:

- Multiple agents can invoke `test list` or `test run` against the **same project path** concurrently without locking conflicts.
- Each subprocess gets its own Unity instance with its own `Library` cache; heavy concurrent runs may contend on Unity's project lock file. Use separate git worktrees for fully isolated parallel runs.
- `test.list` is `SafeRead` and carries no approval gate — agents can call it freely.
- `test.run` is `PrivilegedExec` to prevent agents from silently launching expensive player builds.

---

## Exit Code Behavior

| Scenario | `test run` result |
| --- | --- |
| All tests pass | `ok: true`, `failed: 0` |
| Some tests fail | `ok: false`, `failed: N > 0`, failures populated |
| Unity crashes / XML missing | `ok: false`, all counters zero, empty failures |
| Timeout | Unity killed, result is whatever XML was written before kill |
| Project not open / no editor found | ExecV2 returns `error` with resolution hint |
