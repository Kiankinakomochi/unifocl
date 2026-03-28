Initialize the unifocl bridge in a Unity project.

Project path (optional — defaults to current working directory if omitted): $ARGUMENTS

Steps:

1. Run the init command to install the editor-side bridge package:
   `unifocl exec "/init $ARGUMENTS" --agentic --format json`

2. If `ok` is false, inspect the `message` field and surface the error. Common causes:
   - Path does not contain `ProjectSettings/ProjectVersion.txt` (not a Unity project root)
   - Unity Editor is not installed or not detected
   - Write permission denied on `Packages/` directory

3. After a successful `/init`, confirm the bridge is ready:
   `unifocl exec "/status" --agentic --format json --project "$ARGUMENTS"`
   Check that `data.mode` is `bridge` (Unity Editor connected) or `host` (headless/batch).

4. If `protocolMismatch` appears in the response, the editor package is outdated — re-run step 1 to redeploy the updated version.

Next steps after a successful init:
- Use `/context` to hydrate scene state before any mutations.
- Use `/mutate` for guided scene mutations with dry-run validation.
- For concurrent multi-agent workflows, use `src/unifocl/scripts/agent-worktree.sh setup` instead of this command.
