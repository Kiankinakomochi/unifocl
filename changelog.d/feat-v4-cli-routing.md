---
bump: minor
---

### Changed
- The CLI and MCP server now reach the Unity Editor through the official Unity CLI (`unity mcp`) instead of unifocl's own HTTP bridge. Hierarchy, inspector, project commands, durable mutations, custom tools, and build/compile/asset-index polling each map to a `unifocl.bridge` Pipeline command with the same JSON in and out.
- Requests with no Pipeline command yet (`/runtime/*`, `/stop`), and Editors whose Pipeline server doesn't answer, still use the HTTP bridge. Set `UNIFOCL_EDITOR_LINK=http` to force HTTP for every request.
