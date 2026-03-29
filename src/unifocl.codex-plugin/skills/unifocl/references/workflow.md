Full agentic workflow guide for unifocl Unity operations.

Task or workflow to plan: $ARGUMENTS

Start by calling `GetAgentWorkflowGuide` via the unifocl MCP server.

Standard session pattern:

1. Setup
   - Ensure `unifocl` is installed.
   - Install Codex integration: `unifocl-codex-plugin install`

2. Open project
   - `unifocl exec "/open <project-path>" --agentic --format json`
   - Wait for `data.ok: true`

3. Hydrate context
   - Follow context workflow before planning mutations.

4. Discover commands
   - Use `ListCommands(scope="all")` and `LookupCommand(command="...")`.

5. Mutate safely
   - Follow mutate workflow (validate + dry-run first).

6. Session efficiency
   - Reuse stable `--session-seed <id>` across related exec calls.
