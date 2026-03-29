# unifocl Codex Plugin

This folder defines the Codex-side equivalent of `@unifocl/claude-plugin`.

## What the Codex plugin should do

Unlike Claude Code's npm plugin format, Codex integration is primarily:

1. MCP server registration (`unifocl --mcp-server`)
2. Reusable workflow guidance (Codex skill/playbook)

So the Codex plugin is a **distribution bundle** made of:

- MCP setup automation
- Codex skill files for `/init`, `/status`, `/context`, `/mutate`, `/workflow`-equivalent flows

## Functional Scope

The Codex plugin must provide the same workflow guarantees as the Claude plugin:

- `/init` equivalent: project bridge setup + post-checks
- `/status` equivalent: daemon/mode/project/editor/session checks
- `/context` equivalent: hierarchy + project + inspector hydration
- `/mutate` equivalent: schema -> validate -> dry-run -> execute -> verify sequence
- `/workflow` equivalent: canonical multi-step agent workflow

The low-level behavior continues to live in:

- `unifocl --mcp-server`
- `unifocl exec "..."`

No mutation logic is duplicated in this package.

## Installation Strategy (No source checkout required)

Not all users clone this repository, so distribution should be release-based:

1. **Primary channel: built-in CLI command**
   - `unifocl agent install codex`
   - Also available: `unifocl agent install claude`
   - Benefit: works for Homebrew/winget/release-binary users with no Node.js requirement.

2. **npm package (publisher and advanced fallback path)**
   - Publish: `@unifocl/codex-plugin`
   - Optional user flow:
     - `npm install -g @unifocl/codex-plugin`
     - `unifocl-codex-plugin install`

3. **GitHub release asset fallback**
   - Ship a small cross-platform installer script in release assets:
     - `install-codex-plugin.sh`
     - `install-codex-plugin.ps1`
   - Users download from release page and run once.

## Versioning & Compatibility

- Keep plugin version aligned with unifocl CLI minor version.
- On protocol-affecting changes, include migration guidance (for example: rerun `/init`).
- Installer should be idempotent and safe to rerun.

## File Layout

```text
src/unifocl.codex-plugin/
  README.md
  package.json
  bin/unifocl-codex-plugin.js
  skills/unifocl/SKILL.md
  skills/unifocl/references/init.md
  skills/unifocl/references/status.md
  skills/unifocl/references/context.md
  skills/unifocl/references/mutate.md
  skills/unifocl/references/workflow.md
```

## Minimal install behavior

`unifocl agent install codex` should execute equivalent of:

```bash
scripts/setup-mcp-agents.sh --workspace <detected-or-provided-workspace> --codex
```

When using the npm package installer, copy bundled skill files to:

```text
$CODEX_HOME/skills/unifocl/
```

and print verification steps:

1. Restart Codex session.
2. Confirm MCP tools include `ListCommands` and `LookupCommand`.
3. Run a smoke check with `unifocl exec "/status" --agentic --format json`.

## Publish From CLI

From this directory:

```bash
cd src/unifocl.codex-plugin
npm publish --access public
```

If this is your first publish for the scope:

```bash
npm login
npm publish --access public
```
