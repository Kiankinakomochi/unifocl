---
bump: major
---

### Added
- Pipeline transport layer for v4. unifocl now reaches the Unity Editor through Unity's official CLI. For one-shot CLI calls it runs a `unity command` process per call. For agent and MCP sessions it keeps one `unity mcp` process per project, so each call takes milliseconds instead of a process start. The `unity` binary is found through `UNIFOCL_UNITY_CLI`, then `PATH`, then `~/.unity/bin`. Later v4 phases move Editor operations onto this layer and remove unifocl's own Editor listener.
