---
bump: minor
---

### Added
- The unifocl Editor bridge now registers its operations as Unity Pipeline commands, so the official Unity CLI (`unity command`, `unity mcp`) can reach them:
  - **`unifocl.bridge`:** one command per endpoint of unifocl's HTTP bridge, with the same JSON request and response. These cover the hierarchy snapshot, find and command; the inspector; project commands with their status; durable mutation submit, status, result and cancel; custom tools; build status and log; compile status; the asset index; and a ping that detects Editor restarts. The unifocl CLI moves onto these in the next phase.
  - **`unifocl`:**
    - `validate` returns the validator report as its result and exits non-zero when the report has errors.
    - `tool_list` lists the project's `[UnifoclCommand]` tools.
    - `tool_preview` dry-runs a tool and reverts every change Unity's Undo tracks. It is marked read-only, so it stays available under `unity mcp --exclude-annotations destructive`.
  - **`unifocl.tool.<prefix> <name>`:** one command per `[UnifoclCommand]` tool. It uses Pipeline's convention: it refuses to run without `--confirm`, and `--dry-run` previews it.
- These commands compile into their own assembly, which is built only when the project has `com.unity.pipeline`.
