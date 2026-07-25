---
bump: patch
---

### Changed
- CI no longer uses Personal Access Tokens. The changelog aggregation step moved
  into `ci-release.yml` as a job so the version bump can be pushed with the
  built-in `GITHUB_TOKEN`, and the Homebrew tap push now uses a short-lived
  GitHub App installation token scoped to the tap repository.
- All GitHub Actions are pinned to full commit SHAs and kept current by
  Dependabot. Release creation uses the `gh` CLI instead of a third-party action.
- WinGet submission moved out of CI to a documented local command, removing the
  `public_repo`-scoped token from the repository. See `.github/RELEASING.md`.

### Removed
- `.github/workflows/changelog-aggregate.yml`, folded into `ci-release.yml`.
