# unifocl Codex Skill

Use this skill when working with Unity projects through `unifocl` in Codex.

## Purpose

Provide a low-token workflow that mirrors the Claude plugin prompts:

- init
- status
- context
- mutate
- workflow

## MCP Prerequisite

This skill assumes Codex MCP is configured:

- server name: `unifocl`
- command: `unifocl --mcp-server`

If not configured, run:

```bash
unifocl-codex-plugin install
```

## Workflow Shortcuts

Read these references and follow them strictly:

- `references/init.md`
- `references/status.md`
- `references/context.md`
- `references/mutate.md`
- `references/workflow.md`

## Guardrails

- Always prefer `GetAgentWorkflowGuide` over stale docs.
- Always run mutation validation + dry-run before execution.
- Use stable `--session-seed` values across multi-step flows.
