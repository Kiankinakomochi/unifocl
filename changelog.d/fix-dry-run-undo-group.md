---
bump: patch
---

### Fixed
- Dry-runs (`/mutate --dry-run`, hierarchy and inspector dry-runs, batch dry-runs, `/eval --dry-run`) no longer revert earlier daemon mutations and then save the reverted scene. Each dry-run now opens its own Undo group and reverts only that group (#223).
- Hierarchy create, remove and duplicate no longer fold earlier agent requests into their Undo group, so one Editor undo reverts one operation and a failed request rolls back only its own changes.
