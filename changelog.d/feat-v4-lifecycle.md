---
bump: minor
---

### Changed
- `/open`, `/close` and `/daemon start|stop` now manage the Unity Editor through the official Unity CLI.
  - An Editor that already has the project open (GUI or batchmode) is used as it is.
  - Otherwise `unity open --batchmode --until-ready` starts one. This replaces the old Host mode launch through `-executeMethod`.
  - `/close` shuts down only batchmode Editors. A GUI Editor stays open and the session just detaches, as before. Scenes whose unsaved changes the Editor discarded are listed as a warning.
- Batchmode Editors that unifocl starts quit after 600 s with no unifocl command. Set `UNIFOCL_EDITOR_IDLE_TIMEOUT_SECONDS` to change this.
- Startup compile errors are read from the Editor log at `Library/unifocl/editor.log`.
- Requests that have a `unifocl.bridge` command no longer fall back to the HTTP bridge.

### Removed
- Unsafe mode no longer passes `-noUpm`, because the Pipeline package is itself a UPM package. A batchmode Editor with compile errors now fails to start even with `-ignoreCompileErrors`.
