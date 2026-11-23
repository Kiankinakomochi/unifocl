---
bump: minor
---

### Changed
- The Editor no longer runs an HTTP listener. All of the CLI's Editor traffic, including the runtime relay (`/runtime target|attach|status|detach|manifest|exec|job|stream|watch`), goes through the official Unity CLI as `unifocl.bridge` Pipeline commands.
- `/init` (and the package sync on `/open`) adds `com.unity.pipeline` to the project with `unity pipeline install` when it is missing.
- `.unifocl/bridge.json` no longer records a daemon port. The port the CLI shows for a project is a link id derived from the project path; nothing listens on it.
- The Editor session id reported to the CLI now survives domain reloads, so it changes only when the Editor restarts.
- When an Editor runs but never answers unifocl's commands, `/open` reports the compile errors from the Editor log if there are any.

### Removed
- The Editor-side HTTP bridge: the InitializeOnLoad listener, host-mode `StartServer`, and the `/touch`, `/stop` and `/agent/exec` endpoints. The batchmode idle timeout added in the previous phase replaces host mode's inactivity TTL.
- The `UNIFOCL_EDITOR_LINK=http` switch. Requests to an Editor never fall back to HTTP; a path with no Pipeline command gets a 404.
- The Editor-side UPM batch installer (`InstallRequiredMcpPackageBatch`), which nothing called.
- The prompt and TUI headers say `unifocl` instead of `UnityCLI`, which now reads as Unity's own CLI.
