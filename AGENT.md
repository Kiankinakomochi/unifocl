## name: unifocl-cli-backend-best-practices
description: >
  Use this when working in the unifocl .NET CLI project. Enforces strict coding standards, stateless daemon-safe architecture rules, Host/Bridge mode integration safety, and .NET 10 console app conventions.

# unifocl CLI Skill (Strict Team Conventions)

## Mandatory Worktree Bootstrap (Do First)

Before any edits or worktree actions, execute these steps in order:

1. Create a working branch with `codex/` prefix from latest `main`.
2. Pull latest `origin/main`.
3. Sync and initialize submodules recursively:
   - `git submodule sync --recursive`
   - `git submodule update --init --recursive`
4. Bump the minor version in `src/unifocl/Services/CliVersion.cs` and start the dev cycle with `DevCycle = "a1"`.
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
- **Unity Type Reference Strategy (Primary):** For types defined in Unity-side assemblies, use mirrored POCO contracts for compile-safe boundaries and HTTP communication. Avoid reflection-based type access as a default approach.
- **Contract Pipeline Strategy (Primary):** Shared transport contracts must be defined in protobuf `.proto` files under the `external/unifocl-protobuf` submodule and consumed via generated C# classes (`Unifocl.Shared`). CLI code must reference only generated protobuf DTOs for transport boundaries; Unity bridge code maps Unity types to/from these stable protobuf contracts.
- **Plugin Sync:** After protobuf contract updates, run `./scripts/sync-protobuf-unity-plugin.sh` to rebuild `Unifocl.Shared.dll` and copy it into `src/unifocl.unity/EditorScripts/Plugins/`.
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
