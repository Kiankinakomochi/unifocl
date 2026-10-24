---
bump: patch
---

### Fixed
- `/console dump --type warning` now returns `Debug.LogWarning` entries. The log-mode classifier used the wrong Unity flag bits, so script warnings (ScriptingWarning, bit 9) were reported as `log`. Exceptions, compile errors/warnings, asset-import errors/warnings and assertions are now classified correctly too.
- Multi-line quoted arguments in `exec` input (e.g. a multi-line `/eval '...'` snippet) are no longer split into separate commands at each newline. Newlines inside a `'...'` or `"..."` span stay part of the command.
