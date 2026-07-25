# Test Orchestration

unifocl's `test` commands invoke Unity's built-in test runner as a **direct subprocess**, separate from the daemon and safe to call from CI pipelines or any headless environment.

Because the subprocess is a real second Unity instance, the project must not be open in another editor while tests run — see [Unity project lock](#unity-project-lock).

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
| `unity-editmode.log` / `unity-playmode.log` | Full stdout/stderr captured from the run subprocess |
| `unity-list.log` | Full stdout/stderr captured from the list subprocess |

The previous run's XML is deleted before a new run starts, and a results file is only accepted if it was written by the run that produced it. A run that never got far enough to write results is reported as an **error** — never as a zero-count success — with the tail of Unity's output included in the message and the full log at the paths above.

---

## Unity Project Lock

Unity holds an exclusive lock on `Temp/UnityLockfile` for as long as a project is open, and refuses to open the same project twice:

```
Aborting batchmode due to fatal error:
It looks like another Unity instance is running with this project open.
Multiple Unity instances cannot open the same project.
```

Because `test` commands launch their own Unity, they cannot run while the project is open — including when it is open **by unifocl itself**. Both `/open` modes hold the lock:

- **Host mode** launches `Unity -projectPath <path> -batchmode -nographics -executeMethod ...`
- **Bridge mode** attaches to your GUI editor

unifocl checks for a held lock before launching and fails immediately with a resolution hint. The check probes the lock file for an exclusive open rather than testing for its existence, so a stale file left behind by a crashed editor does not block a run that would otherwise succeed.

**To run tests, pick one:**

- `/close` first (stops the unifocl daemon), run the tests, then `/open` again
- Quit the Unity editor holding the project
- Point the run at a separate clone or git worktree (`unifocl exec "test run editmode" --project <other-path>`)

---

## Multi-Agent Safety

- `test.list` is `SafeRead` and carries no approval gate — agents can call it freely.
- `test.run` is `PrivilegedExec` to prevent agents from silently launching expensive player builds.
- Concurrent `test` invocations against the **same project path** do **not** work: the first Unity to start takes the project lock and every other one aborts. They also share one `Library` cache and one artifacts directory, so results would overwrite each other regardless.
- For parallel runs, give each agent its own clone or git worktree. That is the only fully isolated arrangement.

---

## Exit Code Behavior

| Scenario | `test run` result |
| --- | --- |
| All tests pass | `ok: true`, `failed: 0` |
| Some tests fail | `ok: false`, `failed: N > 0`, failures populated |
| Project open in another Unity | `error` naming the lock, with resolution steps |
| Unity crashes / XML missing | `error` with Unity's exit code and output tail |
| Timeout | Unity killed; partial XML is reported if written, otherwise `error` |
| No editor found for the project | `error` with resolution hint |
