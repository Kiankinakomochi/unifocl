# Changelog

## 0.3.1 - 2026-03-08

### Added
- Added UPM install command support:
  - `/upm install <target>`
  - aliases: `/upm add <target>`, `/upm i <target>`
  - project-mode equivalents: `upm install`, `upm add`, `upm i`
- Added UPM remove command support:
  - `/upm remove <id>`
  - aliases: `/upm rm <id>`, `/upm uninstall <id>`
  - project-mode equivalents: `upm remove`, `upm rm`, `upm uninstall`
- Added UPM update command support:
  - `/upm update [id]`
  - alias: `/upm u [id]`
  - project-mode equivalents: `upm update [id]`, `upm u [id]`
  - `id` omitted updates all outdated packages safely (sequential execution with per-package error isolation)
- Added install target validation for:
  - registry IDs (e.g., `com.unity.addressables`)
  - Git URLs (e.g., `https://github.com/user/repo.git?path=/subfolder#v1.0.0`)
  - local package paths (e.g., `file:../local-pkg`)

### Changed
- Extended project command timeout handling for long-running `upm-install` operations.
- Updated UPM command intellisense suggestions to include install flows and target examples.

## 0.3.0 - 2026-03-06

### Added
- New `/build` command suite and aliases:
  - `/build run [target] [--dev] [--debug] [--clean] [--path <output-path>]`
  - `/build exec <Method>`
  - `/build scenes`
  - `/build addressables [--clean] [--update]`
  - `/build cancel`
  - `/build targets`
  - `/build logs`
  - Aliases: `/b`, `/bx`, `/ba`
- Interactive build-target selection when target is omitted.
- Target install-then-build flow for missing modules.
- Build target caching (refresh on explicit install flow).
- In-terminal live build monitor with pinned status/log header.
- Restartable build log tail and cancelled-build log snapshot view.
- Build diagnostics expansion:
  - heartbeat and stall hints
  - last diagnostic / last exception reporting
  - log file path shown in monitor and summary

### Changed
- Build cancellation now performs daemon teardown and returns to Project mode after keypress.
- Build output routing now supports explicit `--path`.
- Default build outputs are isolated in timestamped folders to avoid artifact collisions.
- Main-thread dispatch safety improved for build execution and daemon stop paths.
- Added spinner/status UX for heavy build-related steps.

### Fixed
- Resolved Unity main-thread violation during daemon stop (`EditorApplication.Exit` off main thread).
- Improved resilience for detach/reattach with ongoing builds.
