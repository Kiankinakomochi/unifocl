---
bump: minor
---

### Added
- `/open` and `/init` now detect missing unifocl `.gitignore` entries (`.unifocl/`, `.unifocl-runtime/`) in the Unity project.
  - Interactive sessions prompt the user once and remember the answer.
  - Agentic/MCP sessions receive a structured hint advising them to ask the user for consent before adding entries.
- New config key `setup.gitignore` (values: `auto` | `off` | `prompt`):
  - `auto` — silently add missing entries on every open/init.
  - `off` — never check.
  - `prompt` (default) — ask once interactively, then remember the answer.
  - Managed via `/config get|set|reset setup.gitignore`.
