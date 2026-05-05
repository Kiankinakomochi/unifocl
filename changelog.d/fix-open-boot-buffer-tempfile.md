---
bump: patch
---

### Fixed
- MCP host no longer accumulates the entire `unifocl exec /open` subprocess stdout/stderr
  in RAM during long Unity boots. Output now streams to temp files and only the tail is
  loaded back when the boot completes, eliminating the multi-hundred-megabyte memory
  growth that could OOM the `dotnet` MCP host on cold-project opens.
