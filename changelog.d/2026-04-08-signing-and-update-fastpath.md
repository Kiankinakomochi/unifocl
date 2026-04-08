---
bump: minor
---

### Added
- Release artifacts now publish SHA256 checksum manifests and GitHub build provenance attestations.
- Installers (`install.sh`, `install.ps1`) and `/update` now enforce checksum verification before install/update.
- Optional attestation verification via `gh attestation verify` is supported in installers and `/update`.

### Changed
- Added strict attestation mode via `UNIFOCL_REQUIRE_ATTESTATION=1` (fails if verification cannot be completed).
- `unifocl update` now has a quick startup path (like `--version`) and does not require full TUI boot.
- Python asset-describe runtime dependencies are now hash-locked through a requirements lock file.

