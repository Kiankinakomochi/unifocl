Check unifocl daemon and project status.

Optional project path or flags: $ARGUMENTS

Steps:

1. Get current status:
   `unifocl exec "/status" --agentic --format json $ARGUMENTS`

2. Report from the response:
   - `data.daemon` — running port and uptime
   - `data.mode` — `bridge` (Unity Editor connected) or `host` (headless/batch)
   - `data.project` — loaded project path and name
   - `data.editor` — Unity Editor version in use
   - `data.session` — active session seed if set

3. If daemon is not running:
   `unifocl exec "/daemon ps" --agentic --format json`
   Lists all running daemon instances with ports, uptime, and associated projects.

4. If no project is open, use `/init` to initialize the bridge, then `/context` to load scene state.

5. If environment issues are suspected (missing Unity Editor path, incompatible protocol version, etc.):
   `unifocl exec "/doctor" --agentic --format json`

MCP server tools available when daemon is up:
  - `ListCommands(scope, query, limit)` — discover all unifocl commands by scope (root/project/inspector/all)
  - `LookupCommand(command, scope)` — exact or fuzzy command lookup with signature and description
  - `GetMutateSchema()` — full /mutate op schema with all supported fields and types
  - `ValidateMutateBatch(opsJson)` — pre-validate a mutation batch without executing
  - `GetAgentWorkflowGuide()` — complete agentic workflow reference, version-matched to this binary
  - `GetCategories()` — list custom tool categories from the project manifest
  - `LoadCategory(name)` — register a category's [UnifoclCommand] tools as live MCP tools
