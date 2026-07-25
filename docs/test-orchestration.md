# Test Orchestration

unifocl runs Unity's built-in test runner one of two ways, chosen automatically:

- **In the attached editor** when a project is open — EditMode only, no second Unity, no lock conflict.
- **As a direct subprocess** when nothing is attached — EditMode and PlayMode, suitable for CI or any headless environment.

Both produce the same NUnit v3 XML and the same structured output. See [Execution Model](#execution-model).

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

There are two execution paths. unifocl picks one automatically by probing the project's daemon port:

| Situation | Path |
| --- | --- |
| A project is open (`/open`, either mode) | **In-editor** — EditMode only |
| No daemon answering | **Subprocess** — EditMode and PlayMode |

Both write the same NUnit v3 XML to the same location, so the output contract is identical either way.

### In-editor (attached daemon)

EditMode tests run inside the editor that already holds the project, via the Test Framework's `TestRunnerApi`. No second Unity is launched, so the project lock is never contended.

- The CLI sends a `test-run` / `test-list` project command and gets back a tracking id.
- The editor runs the tests and writes NUnit XML with `TestRunnerApi.SaveResultToFile`.
- Completion is signalled by a marker at `Temp/unifocl/test-results/<requestId>.json`, which the CLI polls.

Results travel through disk rather than the response body because a test run can trigger a domain reload, which tears down the daemon socket and every in-memory continuation with it — the same handoff `/compile` uses.

`test list` also benefits: names and assemblies come from the test tree, so they are exact, where the subprocess path had to scrape them from stdout.

**Requires** `com.unity.test-framework` in the project. The adapter ships as its own assembly (`UniFocl.EditorBridge.TestRunner`) constrained to `UNITY_TESTS_FRAMEWORK`, so Unity skips it entirely when the package is absent and unifocl reports that rather than failing obscurely.

**PlayMode is not supported in-editor** and always takes the subprocess path — which means it still requires the project to be closed. See [Unity project lock](#unity-project-lock).

### Subprocess

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

The `unity-*.log` files are written by the subprocess path only. The in-editor path additionally writes a completion marker to `Temp/unifocl/test-results/<requestId>.json`, which is transient and safe to delete.

The previous run's XML is deleted before a new run starts, and a results file is only accepted if it was written by the run that produced it. A run that never got far enough to write results is reported as an **error** — never as a zero-count success — with the tail of Unity's output included in the message and the full log at the paths above.

---

## Unity Project Lock

Unity holds an exclusive lock on `Temp/UnityLockfile` for as long as a project is open, and refuses to open the same project twice:

```
Aborting batchmode due to fatal error:
It looks like another Unity instance is running with this project open.
Multiple Unity instances cannot open the same project.
```

Both `/open` modes hold that lock:

- **Host mode** launches `Unity -projectPath <path> -batchmode -nographics -executeMethod ...`
- **Bridge mode** attaches to your GUI editor

**This does not affect EditMode runs**, which execute inside the attached editor and never launch a second Unity. It applies to the subprocess path only:

- **PlayMode**, always
- **EditMode**, when no daemon is answering but a Unity still holds the project (for example an editor open without unifocl attached, or a second agent racing the same project)

In those cases unifocl probes the lock before launching and fails immediately with a resolution hint. The probe attempts an exclusive open rather than testing for the file's existence, so a stale file left behind by a crashed editor does not block a run that would otherwise succeed.

**To run a subprocess-path test suite, pick one:**

- Quit the Unity editor holding the project, then run the tests
- In **host mode**, `/close` releases the lock — the headless daemon exits with it
- In **bridge mode**, `/close` does **not** release the lock: it detaches the session and restarts the bridge listener, leaving your GUI editor running. Quit the editor itself
- Point the run at a separate clone or git worktree (`unifocl exec "test run playmode" --project <other-path>`)

---

## Multi-Agent Safety

- `test.list` is `SafeRead` and carries no approval gate — agents can call it freely.
- `test.run` is `PrivilegedExec` to prevent agents from silently launching expensive player builds.
- Concurrent `test` invocations against the **same project path** do **not** work. On the in-editor path the editor refuses a second job while one is in flight; on the subprocess path the first Unity takes the project lock and the rest abort. Both share one artifacts directory, so results would overwrite each other regardless.
- For parallel runs, give each agent its own clone or git worktree. That is the only fully isolated arrangement.

---

## Exit Code Behavior

| Scenario | `test run` result |
| --- | --- |
| All tests pass | `ok: true`, `failed: 0` |
| Some tests fail | `ok: false`, `failed: N > 0`, failures populated |
| Test framework package missing (in-editor) | `error` naming `com.unity.test-framework` |
| Editor never reports completion (in-editor) | `error` after the timeout, suggesting a domain reload aborted the run |
| Project open in another Unity (subprocess) | `error` naming the lock, with resolution steps |
| Unity crashes / XML missing (subprocess) | `error` with Unity's exit code and output tail |
| Timeout (subprocess) | Unity killed; partial XML is reported if written, otherwise `error` |
| No editor found for the project | `error` with resolution hint |
