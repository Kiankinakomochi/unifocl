Full agentic workflow guide for unifocl Unity operations.

Task or workflow to plan: $ARGUMENTS

Start by calling `GetAgentWorkflowGuide` via the unifocl MCP server. It returns the authoritative,
version-matched workflow reference — always prefer it over static docs for exact flags and field names.

Standard session pattern:

1. Setup
   Install: `brew install unifocl` (macOS) or run `scripts/install.ps1` (Windows).
   Initialize bridge: `/init <project-path>` (see `/init` slash command).
   The MCP server starts automatically when Claude Code loads this plugin.

2. Open project
   `unifocl exec "/open <project-path>" --agentic --format json`
   Wait for `data.ok: true` before proceeding.

3. Hydrate context
   Use `/context` to dump hierarchy + project structure before any mutations.
   Always read context before planning mutations — paths are case-sensitive.

4. Discover commands
   `ListCommands(category="all")` via MCP for full catalog, or filter by category:
     core (default), setup, build, validate, diag, test, upm, addressable, asset,
     scene, compile, eval, profiling, prefab, animation.
   `LookupCommand(command="...")` for a specific command's signature.
   Prefer MCP lookups over reading README — lower token cost.

5. Mutate scenes
   Always follow dry-run-first. Use `/mutate` for guided mutation.
   For [UnifoclCommand] custom tools:
     a. `GetCategories()` — list available tool categories from project manifest
     b. `LoadCategory(name)` — register category tools as live MCP tools
     c. Call the custom tool via MCP
     d. `ReloadManifest()` after Unity recompiles new [UnifoclCommand] methods

6. Multi-agent / worktrees
   Provision isolated worktrees: `src/unifocl/scripts/agent-worktree.sh setup --worktree-path <path> --branch <name>/<task>`
   Never share mutable worktrees across concurrent agents.

7. Session efficiency
   Pass `--session-seed <id>` to reuse project context across multiple exec calls.
   Eliminates repeated /open overhead in multi-step workflows.

Apply the above principles to accomplish: $ARGUMENTS
