Guided scene mutation with mandatory dry-run validation first.

Describe what to mutate: $ARGUMENTS

Follow this order exactly.

Step 1 — Plan
- Determine the `/mutate` op array.
- Call `GetMutateSchema` via MCP to verify supported ops and fields.

Step 2 — Validate
- Call `ValidateMutateBatch` via MCP with the planned JSON array.
- Fix all validation errors.

Step 3 — Dry-run
- `unifocl exec "/mutate --dry-run <json-array>" --agentic --format json`
- Review returned diff and paths.
- Do not proceed if `data.allOk` is false.

Step 4 — Execute
- `unifocl exec "/mutate <json-array>" --agentic --format json`
- Confirm `data.allOk: true`.

Step 5 — Verify
- Re-run context workflow or targeted dumps to confirm state.

Path rules:
- Root: `/`
- Top-level: `/ObjectName`
- Nested: `/Parent/Child`
- Case-sensitive names.
