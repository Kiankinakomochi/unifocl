---
bump: minor
---

### Added
- **Timeline command suite (lazy-loaded `timeline` category)**: 5 new commands for semantic Unity Timeline authoring — `timeline.track.add`, `timeline.clip.add`, `timeline.clip.ease`, `timeline.clip.preset`, `timeline.bind`. All Unity Timeline API access uses reflection so `com.unity.timeline` is not a hard compile dependency; commands return a descriptive error when the package is absent.
- **Semantic clip placement**: `timeline.clip.add` accepts a `placement` object with directives `start|end|after|with|at` so agents never need to compute absolute timestamps manually.
- **CSS easing**: `timeline.clip.ease` maps `linear|ease-in|ease-out|ease-in-out|step` to `AnimationCurve` for mix-in/mix-out blending.
- **Procedural motion presets**: `timeline.clip.preset` generates and caches `scale-in|scale-out|fade-in|fade-out|bounce-in` `AnimationClip` assets at `Assets/.unifocl/Presets/`; dry-run safe (skips asset creation and returns a preview).
- **PlayableDirector scene binding**: `timeline.bind` with automatic `Animator` component resolution for `AnimationTrack`s.
- **TUI support**: `/timeline track add`, `/timeline clip add|ease|preset`, and `/timeline bind` subcommands accessible from the interactive REPL.
