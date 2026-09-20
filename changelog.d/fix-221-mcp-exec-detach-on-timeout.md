---
bump: patch
---

### Fixed
- MCP `exec` no longer kills a command that outlives the 30-second budget. Killing the CLI
  mid-operation left the work it had dispatched running inside the Editor while the agent was
  told the operation failed, so the natural response — retrying — duplicated it. Every command
  is now detached on timeout and harvested by a later `exec` call, not just the five project
  lifecycle commands (`/open`, `/o`, `/new`, `/clone`, `/recent`). Non-lifecycle commands report
  `status: "running"` with `elapsedSeconds` and the in-flight command list; lifecycle commands
  keep reporting `status: "booting"`. (#221)
- While a detached run is in flight, every `exec` call now reports its progress instead of
  spawning a competing `unifocl` process against the same daemon. `/close` still aborts it.
  MCP `exec` calls are serialized so that invariant holds under concurrent tool calls — they
  already shared one session seed, one remembered project, and one pending-run slot.
- MCP command classification matched on a bare prefix, so `/opened-elsewhere` was read as
  `/open`. Triggers now have to be followed by end of input or whitespace.

### Added
- `get_agent_workflow_guide(section='long_running')` — what `booting` and `running` mean, how to
  poll a detached command, and why polling cannot duplicate the work.
