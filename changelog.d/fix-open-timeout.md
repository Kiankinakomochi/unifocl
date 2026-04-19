---
bump: minor
---

### Changed
- `/open` (and `/new`, `/clone`, `/recent`) no longer block on the 30-second MCP timeout when Unity takes minutes to boot. The process is now detached after 30 s and the agent receives a `"booting"` status it can poll — retrying the same `/open` returns progress until the daemon is ready, at which point the real result is returned automatically.
