---
bump: minor
---

### Added
- `set_field` and `asset set` now support nested/complex serialized types (Generic non-array) via JSON object values — enables setting `UnityEvent`, struct fields, and other composite properties recursively.
- `asset.refresh` command — triggers `AssetDatabase.Refresh()` or targeted `ImportAsset()` to force reimport after external file writes.
- `timeline.marker.add` command — adds `SignalEmitter` markers to a TimelineAsset's marker track with optional `SignalAsset` assignment.
- `signalasset` type support in `asset.create` — creates `SignalAsset` files (`.signal`) via the Timeline package.
