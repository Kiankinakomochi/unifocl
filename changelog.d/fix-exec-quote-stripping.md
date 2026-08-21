---
bump: patch
---

### Fixed
- Quote characters are no longer stripped from project-mode commands routed through exec/MCP: contextual alias normalization now rewrites command words in place instead of re-joining quote-stripped tokens, so quoted paths containing spaces (e.g. `asset remove "Assets/Asset Packs/Foo"`) reach the handler as a single argument (#213).
- `/asset remove` and `/asset rename` now refuse surplus arguments instead of silently operating on a truncated path — an unquoted path containing spaces produces a loud usage error rather than a wrong-target destructive operation (#213).
- `/eval` snippets are now extracted verbatim from the command (interior string literals, quotes and spaces preserved), support full statement bodies including `return`, auto-wrap bare expressions in `return (...);`, and strip one symmetric pair of outer quotes — making `/eval` usable over MCP for any snippet containing a string literal (#214).
- Profiler summary export, memory snapshot and recorder screenshot commands now skip their file writes during a dry-run instead of writing files the Undo sandbox cannot revert (fixes the UNIFOCL001 analyzer warnings).
