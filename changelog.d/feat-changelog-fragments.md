---
bump: minor
---

### Added
- **Changelog fragment system**: Agents and PRs now write `changelog.d/<name>.md` fragments instead of editing `CHANGELOG.md` or `CliVersion.cs` directly. After merge to `main`, the `changelog-aggregate` CI workflow collects all fragments, computes the highest bump level, updates `CliVersion.cs` and `CHANGELOG.md`, and pushes a release-ready commit — eliminating version conflicts across parallel branches.
- **`scripts/aggregate-changelog.sh`**: Standalone script to collect fragments, bump version, prepend changelog section, and optionally commit. Supports `--dry-run` and `--commit` flags. Works on macOS and Linux.
- **`.github/workflows/changelog-aggregate.yml`**: Post-merge automation triggered when `changelog.d/*.md` files land on `main`. Requires `RELEASE_PAT` secret (`contents: write` scope) to push the version bump commit that triggers the existing `ci-release.yml`.
