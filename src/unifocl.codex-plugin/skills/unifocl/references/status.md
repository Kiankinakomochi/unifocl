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

4. If no project is open, run init workflow first.

5. If environment issues are suspected:
   `unifocl exec "/doctor" --agentic --format json`

MCP tools available when daemon is up:
  - `ListCommands(category, scope, query, limit)` — category filter: core (default), setup, build, validate, diag, test, upm, addressable, asset, scene, compile, eval, profiling, prefab, animation, or 'all'
  - `LookupCommand(command, scope)` — command lookup with signature and description
  - `GetMutateSchema()` — full /mutate op schema
  - `ValidateMutateBatch(opsJson)` — pre-validate a mutation batch
  - `GetAgentWorkflowGuide()` — agentic workflow reference
  - `UseCategory(name)` — load manifest + register category tools in one step
  - `GetCategories()` — list available categories from the project manifest
  - `LoadCategory(name)` — register a category's tools as live MCP tools
  - `UnloadCategory(name)` — remove a loaded category's tools
  - `ReloadManifest()` — refresh manifest after Unity recompiles
