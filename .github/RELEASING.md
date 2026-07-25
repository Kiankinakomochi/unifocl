# Releasing unifocl

## Credential policy

CI holds **no Personal Access Tokens**. Long-lived, account-scoped tokens are the
highest-value thing a compromised build step can steal, and recent supply-chain
incidents (a hijacked action tag, a worming npm postinstall) all had the same
payload: dump the runner environment and exfiltrate whatever it finds. So every
credential here is either short-lived, scoped to a single repository, or absent.

| Need | Mechanism | Lifetime |
| --- | --- | --- |
| Push the version bump to `main` | built-in `GITHUB_TOKEN` (`contents: write`) | one job |
| Create the GitHub Release | built-in `GITHUB_TOKEN` via `gh release create` | one job |
| Push to `homebrew-unifocl` | GitHub App installation token | ~1 hour, one repo |
| Publish to npm | OIDC trusted publishing | one job, no stored secret |
| Submit to WinGet | **manual, from a workstation** | not stored in CI |

Additional hardening in `ci-release.yml`:

- Every action is pinned to a full commit SHA, with the version in a trailing
  comment. Dependabot (`.github/dependabot.yml`) moves the pins forward weekly.
- The only third-party action is gone — releases use the runner's `gh` CLI, so
  no external code runs inside the job holding `contents: write` and
  `id-token: write`.
- `persist-credentials: false` on every checkout except the one job that pushes,
  so a later step cannot read a token back out of `.git/config`.
- Registry credentials are **environment** secrets on the `publish` environment,
  not repository secrets, so no other job can read them.
- `run:` blocks take values through `env:` rather than `${{ }}` interpolation,
  which keeps expression content out of the generated shell script.

## Automatic flow

Merging a PR that contains a `changelog.d/*.md` fragment into `main` runs
`ci-release.yml` end to end, in a single run:

1. **aggregate** — consumes the fragments, bumps `CliVersion.cs`, prepends to
   `CHANGELOG.md`, and pushes the bump commit to `main`.
2. **release** — checks out that new commit, builds the macOS and Windows
   binaries, attests provenance, and creates the GitHub Release.
3. **publish** — pauses for approval on the `publish` environment, then updates
   the Homebrew tap and publishes the npm plugins.

A `GITHUB_TOKEN` push deliberately does not start a new workflow run, which is
why steps 1 and 2 live in one run rather than two workflows.

## One-time setup

### GitHub App for the Homebrew tap

1. **Settings → Developer settings → GitHub Apps → New GitHub App.**
   Name it something like `unifocl-release`. Uncheck **Webhook → Active**.
2. Under **Permissions → Repository permissions**, set **Contents: Read and
   write**. Leave everything else at *No access*.
3. Create the app, then **Generate a private key** (downloads a `.pem`).
4. **Install App** → install it on **`homebrew-unifocl` only**. It does not need
   access to `unifocl` itself.
5. Copy the App's **Client ID** from its settings page.
6. Register them on this repository:

   ```sh
   # Client ID is not sensitive — a repository variable is fine.
   gh variable set TAP_APP_CLIENT_ID --repo Kiankinakomochi/unifocl --body 'Iv1.xxxxxxxx'

   # Private key goes to the `publish` environment, not the repo secret store.
   gh secret set TAP_APP_PRIVATE_KEY --repo Kiankinakomochi/unifocl \
     --env publish < ~/Downloads/unifocl-release.private-key.pem
   ```

If `TAP_APP_CLIENT_ID` is unset the tap update is skipped with a warning; the
rest of the release still completes.

### npm trusted publishing

Already configured. Each package on npmjs.com has this repository and
`ci-release.yml` registered as a trusted publisher, so `npm publish --provenance`
authenticates over OIDC. There is no npm token to rotate.

### Retiring the old tokens

Once a release has gone green on the new workflow, delete the leftovers:

```sh
gh secret delete RELEASE_PAT        --repo Kiankinakomochi/unifocl
gh secret delete HOMEBREW_TAP_TOKEN --repo Kiankinakomochi/unifocl
gh secret delete WINGET_TOKEN       --repo Kiankinakomochi/unifocl
gh secret delete NPM_TOKEN          --repo Kiankinakomochi/unifocl
```

Then revoke the underlying PATs at
<https://github.com/settings/tokens>. Deleting the repository secret does not
revoke the token itself.

## Manual step: WinGet

`wingetcreate` forks `microsoft/winget-pkgs` and opens a pull request as *you*.
That requires a classic PAT with `public_repo`, which grants write access to
every public repository you can push to — too much standing authority to leave
sitting in CI for a step that was already manually triggered. So it runs locally.

After the GitHub Release is published:

```sh
# macOS/Linux: install via `dotnet tool install --global wingetcreate`
# Windows:     winget install Microsoft.WingetCreate
VERSION=3.16.0
wingetcreate update KinichiAnjuMakino.unifocl \
  --version "$VERSION" \
  --urls "https://github.com/Kiankinakomochi/unifocl/releases/download/v${VERSION}/unifocl-${VERSION}-win-x64.zip" \
  --submit
```

Omitting `--token` makes `wingetcreate` authenticate interactively through the
GitHub device flow, so no token is written to disk or to a secret store.

## Republishing an existing version

`workflow_dispatch` on **CI And Release** with a `version` input re-runs the
registry publish steps for an already-released version. It skips the binary
build and the GitHub Release creation.
