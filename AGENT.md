## name: unifocl-cli-backend-best-practices
description: >
  Use this when working in the unifocl .NET CLI project. Enforces strict coding standards, stateless daemon-safe architecture rules, Host/Bridge mode integration safety, and .NET 10 console app conventions.

# unifocl CLI Skill (Strict Team Conventions)

## Mandatory Worktree Bootstrap (Do First)

Before any edits or worktree actions, run this one-shot bootstrap command from the repository root:

1. `src/unifocl/scripts/agent-worktree.sh setup --worktree-path . --branch <agent-name>/<task-name>`
2. This command automatically:
   - Creates/switches to the requested branch from `origin/main`
   - Syncs and initializes submodules recursively
   - Bumps `src/unifocl/Services/CliVersion.cs` minor and resets `DevCycle` to `a1`
   - Scaffolds `.local/compatcheck-benchmark/`
   - Writes `local.config.json`
   - Runs compatcheck with resolved Unity paths
3. For smoke `/init` prep in agentic workflows, use the non-interactive helper command (not the interactive TUI shell):
   - `src/unifocl/scripts/agent-worktree.sh init-smoke-agentic --worktree-path . --project-path .local/compatcheck-benchmark --format json`
4. Configure MCP clients early so command-lookup tools (`ListCommands`, `LookupCommand`) are available in Codex/Claude/Cursor:
   - `scripts/setup-mcp-agents.sh --workspace . --codex`
   - `scripts/setup-mcp-agents.sh --workspace . --cursor-config ~/.cursor/mcp.json --claude-config ~/.claude/mcp.json`
   - Use `--dry-run` first when validating paths.
5. For each development build, increment `CliVersion.DevCycle` (`a1`, `a2`, `a3`, ...). Auto-increment on Debug build is expected and must remain enabled.

## Goal

Maintain this repository with **scalability, security, statelessness, and maintainability** as absolute priorities.

The agent must treat the following as **non-negotiable principles**:

- **Runtime:** .NET 10 console app (`src/unifocl/unifocl.csproj`)
- **Architecture:** CLI + daemon control + Bridge mode payload
- **Security:** Never commit secrets or machine-local credentials/tokens
- **Build Command:** Run builds or tests on `src/unifocl/unifocl.csproj` with `--disable-build-servers -v minimal`
- **Editor Change Validation:** When changing any code under `/Editor` (for example `src/unifocl.unity/EditorScripts/**`), run compatcheck before finalizing:
  - `dotnet build src/unifocl.unity.compatcheck/unifocl.unity.compatcheck.csproj --disable-build-servers -v minimal`
  - Preferred bootstrap command (one-shot AGENT setup including compatcheck):
    - `src/unifocl/scripts/agent-worktree.sh setup --worktree-path . --branch <agent-name>/<task-name>`
  - If branch/setup is already complete and only compatcheck paths need refresh:
    - `src/unifocl/scripts/agent-worktree-compatcheck-update.sh --project-path .local/compatcheck-benchmark --write-local-config --run-compatcheck`
  - The setup command writes local-only artifacts:
    - `local.config.json`
    - `.local/compatcheck-benchmark/`
  - Keep those artifacts uncommitted.
- **Unity Type Reference Strategy (Primary):** For types defined in Unity-side assemblies, use mirrored POCO contracts for compile-safe boundaries and HTTP communication. Avoid reflection-based type access as a default approach.
- **Contract Strategy (Primary):** Shared transport contracts between CLI and Unity are plain C# records serialized as JSON. No protobuf dependency in the CLI build. Hierarchy `mk` types are defined as a CLI-local string catalog; the Unity side uses a normalized switch for built-in types with `TypeCache.GetTypesDerivedFrom<MonoBehaviour>()` as the open-ended fallback for custom types.
- **Build Versioning Rule:** Development builds must use `aX` incremental suffixes in `CliVersion.SemVer` (for example: `0.3.2a1` -> `0.3.2a2`)
- **PR Finalization Version Rule:** When finalizing changes for PR (push/PR creation), harden the version by closing the active dev-cycle suffix (set `CliVersion.DevCycle` to empty) and add an `Officialized` release entry in `CHANGELOG.md`.
- **Bridge Protocol Rule:** If a change requires users to re-run `/init` (for example editor payload/package content changes), bump `CliVersion.Protocol` and include `/init` re-run guidance in the task summary/PR description.
- **Repository Rules:** Always branch from `main` before any actions and create PRs using `.github/pull_request_template.md` in English
- **Mainline Sync Rule:** Before finalizing work (push/PR), merge latest `main` (or `origin/main`) to detect upstream leading changes early and resolve any conflicts before continuing
- **Deployment:** NEVER deploy or publish artifacts without permission

---

# Scope Restriction (Critical Rule)

⚠️ **The agent may ONLY analyze and modify files under:**

- `src/unifocl/`
- `src/unifocl.unity/`
- Repository docs directly related to this project task (for example: `AGENT.md`, `DEVELOPMENT_PLAN.md`, `.github/pull_request_template.md`)

- Do NOT hardcode credentials.
- Ignore `bin` and `obj` artifacts.

---

# 1) Core Principles (Highest Priority)

- **Statelessness is King:** Runtime operations must not rely on in-memory global state for correctness.
- **Readability > Brevity:** Code is read more often than written.
- **Single Responsibility Principle:**
  - One command handler/service should have one clear responsibility.
  - Command wiring belongs in entrypoints; behavior belongs in Services.
- **Async All the Things:** Pure `async/await`. Blocking calls (`.Result`, `.Wait()`) are prohibited.
- **Security First:** Validate all external inputs (CLI args, paths, bridge payloads).

---

# 2) Naming Conventions

## 2.1 Language Rules

- All identifiers must be **English**.
- Avoid abbreviations unless standard (ID, URL, API, DTO).

## 2.2 Case Rules

| **Target** | **Rule** |
| --- | --- |
| Namespace / Class / Struct / Enum / Method | PascalCase |
| Local variables / Parameters | camelCase |
| Private fields | _camelCase |
| Const | SNAKE_CASE |
| Interface | I prefix |
| Async Methods | Suffix with `Async` |

---

# 3) Folder & Architecture Structure

Namespaces should follow the existing project style under `src/unifocl`.

## 3.1 Root Structure

`src/unifocl/
├── Program.cs    # CLI bootstrapping, command palette loop
├── Services/     # Runtime behavior, lifecycle, daemon/process, routing
├── Models/       # Contracts and model types
└── unifocl.csproj`

`src/unifocl.unity/
├── EditorScripts/ # Unity editor-side daemon bridge
└── SharedModels/  # Shared bridge DTO/contracts`

---

# 4) File & Class Rules

- **Filename = Public Type Name**.
- Keep classes `sealed` by default unless extension/inheritance is required.
- Prefer immutable models (records) for DTO-like contracts when practical.

---

# 5) Coding Style

- Indentation: **4 spaces**.
- Line length guideline: **120 characters**.
- Braces: **Allman style**.

## var Usage Rules

✅ Allowed:

`var response = await service.ExecuteAsync();`

❌ Forbidden:

`var result = GetThing(); // when type is unclear from context`

---

# 6) Null / Exceptions / Guard Clauses

- Use **guard clauses** early for arguments, filesystem paths, and command preconditions.
- Do not swallow exceptions silently; map errors to actionable CLI output.

---

# 7) unifocl Implementation Rules

## 7.1 .NET 10 Console App

- `Program.cs` is the source of truth for command wiring.
- Keep command parsing deterministic and testable.
- Avoid legacy frameworks not required by this app.

## 7.2 Unity Bridge Integration

- Bridge payload files are embedded from `src/unifocl.unity`.
- Do not break payload extraction path contracts used by initializer services.
- Keep protocol/model changes synchronized between CLI and Unity shared models.

## 7.3 Configuration

- Prefer explicit configuration points over magic constants.
- Environment-variable overrides are allowed for sandbox-safe execution.

---

# 8) Dependency / Service Design

- Prefer constructor injection patterns for service dependencies when applicable.
- Keep services stateless where possible.
- Encapsulate external process and filesystem operations behind service boundaries.

---

# 9) Async / Await Rules

- Avoid `Task.Run` unless there is a concrete need.
- `async void` is forbidden except true event handlers.
- Propagate cancellation for I/O-bound operations where supported.

---

# 10) Logging & Observability

- Keep logs structured and machine-searchable.
- Include context keys (project path, daemon port, command) in log messages when possible.
- Avoid logging secrets or sensitive local paths unless necessary for debugging.

---

# 11) Documentation

- Public command behaviors should stay aligned with help text and docs.
- Comments should explain business/flow complexity, not obvious syntax.

---

# 12) Testing

- Service logic in `src/unifocl/Services/` should be testable in isolation.
- Prefer unit-level validation for parsing, lifecycle transitions, and path resolution.

---

# 13) Prohibited Practices

🚫 **Forbidden:**

- Blocking async with `.Result` / `.Wait()`
- Hardcoded credentials/tokens/secrets
- Direct edits to generated artifacts under `bin/` or `obj/`
- Destructive repository operations without explicit instruction

---

# Agent Workflow Rules

At task start:

1. Confirm `.NET 10` context in `src/unifocl/unifocl.csproj`.
2. Validate relevant directories exist (`src/unifocl`, optionally `src/unifocl.unity`).
3. Ensure no secrets are present in proposed code.
4. Produce reviewable diffs.

---

# Known Sandbox Problems (Important)

## 1) Home-directory write blocked (`workspace-write` scope)

- **Failure:** `/init` smoke test can fail when writing global payload to `~/.unifocl/daemon-package` with access denied.
- **Cause:** sandbox writes are limited to workspace + configured writable roots.
- **Required handling:**
  - Support override env var: `UNIFOCL_GLOBAL_PAYLOAD_ROOT`.
  - Keep default real behavior unchanged (`~/.unifocl/...`) outside sandbox.
  - For sandbox/local verification, use:
    - `UNIFOCL_GLOBAL_PAYLOAD_ROOT=/tmp/unifocl-global`

## 1.1) Agentic init command route mismatch

- **Failure:** Agent attempts `/init` through interactive shell piping, causing flaky/non-deterministic setup flow for agentic tests.
- **Required handling:**
  - Use one-shot agentic execution for setup verification:
    - `dotnet run --project src/unifocl/unifocl.csproj --disable-build-servers -v minimal -- exec "/init \"<project-path>\"" --agentic --project "<project-path>" --mode project --format json`
  - Preferred helper wrapper:
    - `src/unifocl/scripts/agent-worktree.sh init-smoke-agentic --worktree-path . --project-path .local/compatcheck-benchmark --format json`

## 1.2) Unity editor store write warning in sandbox

- **Failure:** `/init` logs warning saving `~/.unifocl/unity-editors.json` with access denied.
- **Required handling:**
  - Use workspace-local override:
    - `UNIFOCL_CONFIG_ROOT=<worktree>/.local/unifocl-config`
  - The `init-smoke-agentic` helper configures this by default.

## 1.3) Unity UPM socket permission denied in sandbox

- **Failure:** Unity batch install can fail with `listen EPERM ... /tmp/Unity-Upm-*.sock`.
- **Cause:** sandbox restrictions around local socket binding.
- **Required handling:**
  - Retry the same `init-smoke-agentic` command with escalated permissions (`require_escalated`) and approval.
  - Helper behavior: `src/unifocl/scripts/agent-worktree.sh init-smoke-agentic` now emits:
    - `[agent-worktree] escalation-required: ...`
    - `[agent-worktree] rerun-command: ...`
    - exits with code `86` when sandbox/network denial patterns are detected.

## 2) Network/API access blocked for GitHub PR creation

- **Failure:** `gh pr create` can fail to connect to `api.github.com`.
- **Cause:** sandbox network restriction.
- **Required handling:**
  - Retry `gh pr create` with escalated permissions (`require_escalated`) and user approval prompt when needed.

---

# Absolute Priorities

Always protect:

✅ Security (secrets management)  
✅ Scalability (stateless behavior)  
✅ Error Handling (graceful failures)  
✅ Type Safety

Never introduce:

❌ Blocking calls  
❌ Global mutable state  
❌ Hardcoded credentials
