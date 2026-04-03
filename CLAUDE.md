# unifocl — Claude Code Project Instructions

This file contains project-level instructions for Claude Code sessions working in this repository.
For full coding conventions and agent workflow rules, see `AGENT.md`.

## PR Checklist (Required Before Push)

Every PR branch must pass these steps before pushing or creating a pull request:

1. **Compatcheck build** — Verify Unity editor compatibility:
   ```sh
   dotnet build src/unifocl.unity.compatcheck/unifocl.unity.compatcheck.csproj --disable-build-servers -v minimal
   ```

2. **Full documentation regeneration** — Keep `full-documentation.md` in sync with README and docs/:
   ```sh
   scripts/build-full-docs.sh
   ```

3. **Commit any changes** produced by step 2 (updated `full-documentation.md`) into the PR branch.

## Quick Reference

- **Build CLI:** `dotnet build src/unifocl/unifocl.csproj --disable-build-servers -v minimal`
- **Build compatcheck:** `dotnet build src/unifocl.unity.compatcheck/unifocl.unity.compatcheck.csproj --disable-build-servers -v minimal`
- **Regenerate full docs:** `scripts/build-full-docs.sh`
- **Check full docs freshness (CI):** `scripts/build-full-docs.sh --check`
- **Branch from:** `main` (or `origin/main`)
- **PR template:** `.github/pull_request_template.md`

## Scope

Agent edits are restricted to:
- `src/unifocl/`
- `src/unifocl.unity/`
- `docs/`, `README.md`, `full-documentation.md`
- Repository meta files (`AGENT.md`, `CLAUDE.md`, `CHANGELOG.md`, `.github/`)
