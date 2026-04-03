Guided scene mutation with mandatory dry-run validation first.

Describe what to mutate: $ARGUMENTS

Follow this order exactly — never skip the dry-run step.

Step 1 — Plan
  Based on "$ARGUMENTS", determine the /mutate op array.
  Call `GetMutateSchema` via the unifocl MCP server to review all supported ops and required fields.
  Supported ops: create, rename, remove, move, toggle_active, add_component, remove_component,
                 set_field, toggle_field, toggle_component.

Step 2 — Validate (static schema check, no Unity needed)
  Call `ValidateMutateBatch` via MCP with your planned JSON array.
  Fix any validation errors before proceeding.

Step 3 — Dry-run (preview without applying)
  `unifocl exec "/mutate --dry-run <json-array>" --agentic --format json`
  Inspect the `diff` in the response. Verify object paths match the scene hierarchy from `/context`.
  Do NOT proceed if `data.allOk` is false — fix ops first.

Step 4 — Execute (apply for real)
  `unifocl exec "/mutate <json-array>" --agentic --format json`
  Confirm `data.allOk: true` and `data.succeeded == data.total`.
  If `--continue-on-error` was used, check `data.errors` for partial failures.

Step 5 — Verify
  Re-run `/context` (or a targeted dump) to confirm the scene reflects the expected changes.

Path format rules:
- Scene root: `/`
- Top-level object: `/ObjectName`
- Nested: `/Parent/Child/Grandchild`
- Names are case-sensitive and must match exactly as shown in `/dump hierarchy`

Mode availability:
- All ops work in both Host mode (batch/headless daemon) and Bridge mode (interactive Unity Editor).
- No op is restricted to Bridge-only.

For multi-step workflows, pass `--session-seed <id>` across exec calls to share project context without re-specifying --project every time.
