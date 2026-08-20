---
bump: patch
---

### Fixed
- Daemon startup no longer classifies `Tundra build failed` as a compile error (#121).
  Transient build-bootstrap failures are now surfaced as recoverable build warnings in a
  dedicated `RecoverableBuildWarnings` startup-diagnostics field, aligned with
  `CliAgenticIssueService`'s severity mapping, and still trigger the startup warmup retry.
  Compile-error diagnosis is reserved for concrete compiler diagnostics (`error CSxxxx`,
  `Scripts have compiler errors`, `Script Compilation Error`).
