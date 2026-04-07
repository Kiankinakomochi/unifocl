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
- `project.clone` now works in boot mode (no open project) — the one-shot handler bypasses the project router gate and calls `ProjectCloneService` directly, so agents can clone immediately after a failed `/open`.
- `project.clone` guards against destination paths inside the source tree (prevents recursive copy loops) and wraps all file I/O in structured error handling (returns a `Failed` response instead of crashing on permission errors or long paths).
- Session-seed daemon-ownership enforcement is now skipped for plain one-shot CLI calls that do not supply `--session-seed` or `--agentic`, preventing false `E_PROJECT_LOCKED` blocks for non-agentic users.
- Agentic session snapshot lookup is now deterministic when multiple snapshots reference the same port — the most recently written snapshot wins, preventing the wrong session from being flagged as the owner.
- Agentic session snapshots are stored under `~/.unifocl-runtime/agentic/` (stable per-user location) rather than `<cwd>/.unifocl-runtime/agentic/`, so ownership checks are reliable regardless of the working directory from which the CLI is invoked.
- User-provided paths in `project.clone` output are Spectre markup-escaped, preventing rendering corruption when paths contain `[` or `]`.
