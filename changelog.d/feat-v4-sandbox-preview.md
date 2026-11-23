---
bump: minor
---

### Added
- `/preview <group> <command> [argsJson]` (ExecV2 `preview.run`) runs any Unity Pipeline command in a sandbox. That includes Unity's built-in commands and third-party ones without a dry-run of their own. The report lists the scene objects and files the command changed and the AssetDatabase writes it attempted, then the command is undone. Anything that could not be undone is reported as still in place.
- `unity command unifocl.sandbox begin|end|status` exposes the sandbox to agents directly, so a sequence of commands can be previewed as one unit. The session survives domain reloads and ends by itself after its TTL.

### Changed
- JSON string arguments from a `JsonNode` now reach `unity command` without their quotes.
