---
bump: patch
---

### Fixed
- Editor bridge now compiles on Unity 6.5+ (verified on 6000.5.2f1 and 6000.6.6f1), where `GetInstanceID`, `InstanceIDToObject`, `IsLoadingAssetPreview(int)` and the `SceneHandle`/`EntityId` int conversions are hard errors (CS0619). Object ids keep their int wire format via a new `DaemonObjectIds` helper: instance ids before 6.5, the low 32 bits of the `EntityId` from 6.5 on. Older Unity versions keep the original code paths.
- `unifocl init` now installs `DaemonDryRunFileIo.cs` and `Timeline/DaemonTimelineService.cs` into the embedded package. They were missing from the payload allowlist, so the profiler/recorder services failed to compile (CS0103 `DaemonDryRunFileIo`) and the Timeline commands never shipped.
