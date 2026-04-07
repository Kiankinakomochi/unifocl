---
bump: minor
---

### Added
- `project.clone` ExecV2 operation (`PrivilegedExec`) — clones a Unity project to an isolated path, seeding the Library cache for fast startup. Agents blocked by `E_PROJECT_LOCKED` can now provision an isolated copy entirely via the MCP interface without needing the source-repo `agent-worktree.sh` script.
  - Args: `sourcePath` (required), `destPath` (required), `seedLibrary` (default: true)
  - Returns: `clonedPath`, `libraryCopied`, `totalBytesCopied`, plus a ready-to-use `/open` hint
  - Available as `/project clone <source> <dest> [--no-library]` in project and one-shot exec modes

### Fixed
- Multi-agent isolation: `exec /open` on a project already owned by another agentic session now returns `E_PROJECT_LOCKED` (exit 5) instead of silently sharing the same Unity daemon. The `E_PROJECT_LOCKED` hint now points to `project.clone` rather than the source-repo-only `agent-worktree.sh` script, making the recovery path actionable for published binary users.
