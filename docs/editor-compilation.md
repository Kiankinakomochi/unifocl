# Editor Compilation & Window Activation

This document covers how unifocl triggers Unity Editor recompilation after deploying `.cs` files, why manual window focus is not required, and how to configure the behaviour for CI/headless environments.

## Background: The "Two Compiles" Problem

Unity's file-watcher only ticks when the Editor window has OS-level focus. If unifocl copies new `.cs` files into the project (e.g. on `/init` or when deploying custom tools) while Unity is running in the background, Unity will not detect the new files until the user manually clicks the Editor window.

Previously, the `UnifoclManifestGenerator` subscribed to `CompilationPipeline.compilationFinished` from its `[InitializeOnLoad]` static constructor. This created a subtle timing race:

1. First compile (after the `.cs` files land) → `compilationFinished` fires, but the `[InitializeOnLoad]` static constructor has not run yet, so the event handler is not subscribed → manifest is not written.
2. User triggers a second compile manually → `compilationFinished` fires again, the handler is now subscribed → manifest is finally written.

## Permanent Fix

`UnifoclManifestGenerator` now calls `GenerateManifest()` directly inside the `[InitializeOnLoad]` static constructor, in addition to subscribing to `compilationFinished`:

```csharp
[InitializeOnLoad]
internal static class UnifoclManifestGenerator
{
    static UnifoclManifestGenerator()
    {
        CompilationPipeline.compilationFinished += OnCompilationFinished;
        // Regenerate immediately on every domain reload — eliminates the "two compiles" race.
        GenerateManifest();
    }
}
```

After every compile Unity performs a domain reload. `[InitializeOnLoad]` types are constructed during that reload, so `GenerateManifest()` is guaranteed to run once per compile cycle, whether or not the event fired first.

## UnifoclCompilationService

`UnifoclCompilationService` is a global editor-side utility that combines two mechanisms:

1. **OS-level editor window activation** — causes Unity's file-watcher to tick and detect newly copied `.cs` files immediately, without requiring manual user focus.
2. **`CompilationPipeline.RequestScriptCompilation()`** — queues a script-only recompile. This is significantly cheaper than `AssetDatabase.Refresh()` on large projects because it skips asset reimporting entirely.

### API

```csharp
UnifoclCompilationService.RequestRecompile();
```

Call this after writing any `.cs` files that Unity must pick up. unifocl's `/init` flow calls this automatically.

### Platform-Specific Window Activation

| Platform | Mechanism |
|---|---|
| macOS | `osascript -e 'tell application "Unity" to activate'` |
| Windows | PowerShell `Shell.Application.AppActivate("Unity")` (COM, no P/Invoke) |
| Linux | `xdotool search --name 'Unity' windowactivate` with `wmctrl -a Unity` fallback |

Window activation is fire-and-forget: if it fails (e.g. Unity is not running as a windowed process) the error is logged as a non-fatal warning and `RequestScriptCompilation()` is still called.

## UnifoclEditorConfig

Per-project editor configuration is stored at `<projectRoot>/.unifocl/editor-config.json` and loaded by `UnifoclEditorConfig.Load()`.

| Field | Type | Default | Description |
|---|---|---|---|
| `allowWindowGrab` | `bool` | `true` | When `false`, the OS window-activation step is skipped. |

### Disabling Window Grab for CI / Headless Runners

On headless CI agents there is no windowed Unity process to activate, and the activation attempt may produce noisy warnings. Set `allowWindowGrab` to `false` in your project config:

```json
// <projectRoot>/.unifocl/editor-config.json
{
  "allowWindowGrab": false
}
```

`RequestScriptCompilation()` is still called even when window grab is disabled, so compilation is triggered normally on headless batch-mode Unity processes.

### Committing the Config

`editor-config.json` is safe to commit. The default (`allowWindowGrab: true`) is appropriate for all developer machines. Override it to `false` only on CI-specific config branches or via environment-based scripting.
