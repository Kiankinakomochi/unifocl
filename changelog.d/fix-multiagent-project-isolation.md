---
bump: patch
---

### Fixed
- Multi-agent isolation: `exec /open` on a project already owned by another agentic session now returns `E_PROJECT_LOCKED` (exit 5) instead of silently attaching to the same Unity daemon. Previously two agents with different `--session-seed` values could share a single Unity instance, causing mutation cross-contamination. The fast-path now checks session ownership via persisted session snapshots before allowing a re-attach.
