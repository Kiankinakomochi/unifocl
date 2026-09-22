---
bump: minor
---

### Added
- Optional `probuilder` tool category for ProBuilder level geometry. Agents load it with
  `use_category('probuilder')`; humans use `/probuilder <shape|mesh|face|edge> ...`. Tools:
  `probuilder.shape.create` (12 primitives with bounding-box size, local position/rotation,
  center/bottom pivot, material, segments/steps, optional MeshCollider), `probuilder.mesh.info`
  (counts, bounds, per-face normal/center/direction), `probuilder.face.extrude`,
  `probuilder.face.move`, `probuilder.face.set_material`, `probuilder.face.flip_normals`,
  `probuilder.face.subdivide`, `probuilder.edge.bevel`, `probuilder.face.delete`,
  `probuilder.mesh.merge`, `probuilder.mesh.probuilderize`, and `probuilder.mesh.export`.
  Faces are selected by index or by world direction (`up`, `left`, ...), objects by hierarchy
  path. Every mutating tool supports dry-run previews.
- The tools compile into a separate `UniFocl.EditorBridge.ProBuilder` assembly gated on
  `com.unity.probuilder` 5.0+, so projects without ProBuilder are unaffected and simply do not
  see the category. `use_category('probuilder')` explains how to enable it when the package is
  missing. Re-run `/init` (or `/open`) after updating to install the new bridge files.

### Fixed
- A `dryRun` call to any `[UnifoclCommand]` custom tool reverted every earlier daemon mutation
  since the last user input in the editor, not just its own changes, and the scene-restore step
  then saved that reverted state. The editor only advances the Undo group on user input, so the
  group captured before the dry-run still held that earlier work. The dry-run now opens its own
  group first and reverts only that one. (The hierarchy, inspector and eval dry-runs share the
  old pattern and are tracked in #223.)
- Custom-tool failures (`/profiler`, `/recorder`, `/probuilder`, ...) were logged without a
  leading `error`, so `unifocl exec --agentic` reported them as `status: success`. They now
  surface as agentic errors.
- Agentic logs lost the brackets of any Spectre-escaped text (`[[`/`]]`), which turned JSON arrays
  in custom-tool results into unparseable fragments such as `"faces": ],`. Custom-tool results
  are also rendered without `\uXXXX` escapes for quotes and non-ASCII text.
- Custom-tool arguments now decode `\uXXXX` escapes, so string arguments containing non-ASCII
  text (e.g. Japanese GameObject names) reach `[UnifoclCommand]` methods intact. Array and
  object arguments are passed through as raw JSON instead of being truncated at the first comma,
  and a JSON `null` now counts as an omitted argument.
