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

4. **Changelog fragment** — Write a fragment to `changelog.d/<branch-slug>.md` describing what changed.
   Do **not** edit `CHANGELOG.md` or `CliVersion.cs` directly — CI handles version bumping after merge.
   ```sh
   # Example: changelog.d/feat-my-feature.md
   ---
   bump: patch   # patch | minor | major
   ---

   ### Added
   - Brief description of the change.
   ```

## Quick Reference

- **Build CLI:** `dotnet build src/unifocl/unifocl.csproj --disable-build-servers -v minimal`
- **Build compatcheck:** `dotnet build src/unifocl.unity.compatcheck/unifocl.unity.compatcheck.csproj --disable-build-servers -v minimal`
- **Regenerate full docs:** `scripts/build-full-docs.sh`
- **Check full docs freshness (CI):** `scripts/build-full-docs.sh --check`
- **Branch from:** `main` (or `origin/main`)
- **PR template:** `.github/pull_request_template.md`

## Compile Warning Policy

**Zero-warning builds are mandatory.** Every build (CLI and compatcheck) must produce 0 warnings.

- **Best-effort fix first:** Resolve warnings by adding null guards, initializing fields, updating
  obsolete API calls, or adjusting signatures. Prefer concrete fixes over suppression.
- **Suppress only as last resort:** Use `#pragma warning disable` only when the warning cannot be
  fixed without breaking semantics (e.g. obsolete API with no drop-in replacement). Always include
  a comment explaining why.
- **Pre-existing warnings are your responsibility too.** If the build already has warnings before
  your changes, fix them in the same branch. Do not leave warnings for someone else.
- **Nullable declarations are acceptable when semantically correct** (a method genuinely returns
  null), but prefer non-nullable designs — initialize fields, use `?? default`, add guards.

## Scope

Agent edits are restricted to:
- `src/unifocl/`
- `src/unifocl.unity/`
- `docs/`, `README.md`, `full-documentation.md`
- Repository meta files (`AGENT.md`, `CLAUDE.md`, `.github/`)
- `changelog.d/` — write fragment files here; **never edit `CHANGELOG.md` or `CliVersion.cs` directly**
