# unifocl

![unifocl logo](https://github.com/user-attachments/assets/1bae1b33-b120-4ba2-bb34-77aa1250e7f7)

**The programmable operations layer for Unity—built for keyboard-driven developers and autonomous AI agents.**

Unity is a powerful engine, but its graphical, mouse-driven Editor can introduce friction for automation, LLM workflows, and developers who prefer the terminal. unifocl solves this by providing a structured, deterministic way to interact with, navigate, and mutate your Unity projects without relying on the GUI.

Whether you are a developer looking for a snappy Terminal User Interface (TUI) to manipulate scenes, or you are hooking up an AI agent via the Model Context Protocol (MCP) to autonomously write code and edit prefabs, unifocl provides the bridge.

unifocl is an independent project and is not associated with, affiliated with, or endorsed by Unity Technologies.

## Why unifocl?

- **Native Agentic Tooling (MCP):** Comes with a built-in MCP server. AI agents (like Claude) can seamlessly read your hierarchy, inspect components, and safely mutate assets using strict JSON/YAML response envelopes.
- **Lean & Token-Efficient:** LLMs struggle with massive, unstructured context windows. unifocl is specifically designed to keep its API surface streamlined, feeding agents exactly the project state they need. This saves tokens, reduces costs, and keeps your agents focused.
- **Safe, Deterministic Mutations:** Never let an AI break your project. Every single mutation command features mandatory dry-run capabilities and transactional safety (Undo/Redo integration), ensuring predictability before anything touches the disk.
- **Instantly Extensible:** Need a custom tool for your agent? Add the `[UnifoclCommand]` attribute to your C# editor methods. unifocl automatically discovers them and exposes them as live MCP tools with built-in dry-run sandboxing.
- **Dual-Interface:** A clean, keyboard-driven Spectre.Console TUI for humans, alongside a stateless, headless execution path for multi-agent workflows.
- **Debug Artifact Reports:** Collect tiered snapshots of your project state—console logs, validation results, profiler data, recorder output—into a single structured JSON file. Feed it to agents for automated bug reports or pipe it straight to Jira/Wrike.
- **Zero-Touch Compilation:** Deploy new editor scripts and let unifocl trigger Unity recompilation automatically—no manual window focusing required.

## Installation

### macOS (Apple Silicon & Intel)

**Shell Installer:**

```sh
curl -fsSL https://raw.githubusercontent.com/Kiankinakomochi/unifocl/main/scripts/install.sh | sh
```

**Homebrew:**

```sh
brew tap Kiankinakomochi/unifocl
brew install unifocl
```

### Windows (x64)

**Winget:**

```
winget install unifocl
```

**PowerShell Installer:**

```powershell
iwr -useb https://raw.githubusercontent.com/Kiankinakomochi/unifocl/main/scripts/install.ps1 | iex
```

### Manual Download

Download pre-built archives from the [latest GitHub release](https://github.com/Kiankinakomochi/unifocl/releases/latest) and place the binary anywhere in your `PATH`.

### Agent Plugins (Codex + Claude Code)

unifocl provides a built-in installer for agent integrations:

```sh
# Codex
unifocl agent install codex

# Claude Code
unifocl agent install claude
```

This replaces manual MCP JSON edits and plugin management. Same command works for Homebrew, Winget, and release-binary users. Idempotent install/update flow versioned with the CLI lifecycle.

## Quick Start

### For Humans (The TUI)

Launch the interactive shell to navigate your project at the speed of thought.

```sh
# Start the unifocl bridge in your project
unifocl

> /open ./MyUnityProject
> /hierarchy
> f PlayerController        # Fuzzy find
> mk Cube                   # Create a GameObject
> /inspect 12               # Inspect the object
> set speed 5               # Change a component field
```

### For AI (The MCP Server & Agentic Execution)

Agents can use the built-in MCP server or the one-shot `exec` path to read deterministic state and make safe changes.

```sh
# Run an agentic dry-run to see what will change
unifocl exec "rename 3 PlayerController --dry-run" --agentic --format json
```

unifocl returns a structured diff payload, letting the LLM verify the change before committing it:

```json
{
  "status": "success",
  "action": "rename",
  "diff": {
    "format": "unified",
    "summary": "Rename GameObject index 3",
    "lines": ["--- before", "+++ after", "-  name: \"Cube\"", "+  name: \"PlayerController\""]
  }
}
```

Every mutation command supports `--dry-run`. The operation executes inside a Unity Undo group, captures a before/after diff, and immediately reverts—nothing touches the disk until you confirm.

### Custom MCP Tools with `[UnifoclCommand]`

Expose your own C# editor methods as live MCP tools:

```csharp
[UnifoclCommand("myteam.reset-player", "Reset player to spawn point")]
public static void ResetPlayer(UnifoclCommandContext ctx)
{
    var player = GameObject.FindWithTag("Player");
    player.transform.position = Vector3.zero;
    ctx.Return("Player reset to origin");
}
```

unifocl discovers these at runtime and makes them available as MCP tools—complete with automatic dry-run sandboxing. See [`docs/custom-commands.md`](docs/custom-commands.md) for the full guide.

### Dynamic C# Eval

Execute arbitrary C# directly in the Unity Editor context—no script files needed:

```sh
# Simple read query
unifocl eval 'return Application.productName;'

# Dry-run: execute and revert all Unity Undo-tracked changes
unifocl eval 'Undo.RecordObject(Camera.main, "t"); Camera.main.name = "CHANGED";' --dry-run
```

Eval uses a dual-compiler strategy (Unity `AssemblyBuilder` in Bridge mode, bundled Roslyn in Host mode) and supports `async`/`await`, custom declarations, timeout protection, and `--dry-run` sandboxing. The entry point is always `async Task<object>`, so `await` works naturally.

### Asset Describe — Let Agents See Without Vision Tokens

AI agents working with Unity often need to understand what an asset *looks like* — is this sprite a character? A tileset? A UI icon? Normally this means sending the image to a multimodal LLM and burning tokens on cross-modal comprehension.

`asset.describe` solves this by running a local vision model (BLIP or CLIP) on your machine. Unity exports a thumbnail, the CLI captions it locally, and the agent receives a compact text description — zero vision tokens spent.

```sh
unifocl exec '{"operation":"asset.describe","args":{"assetPath":"Assets/Sprites/hero.png"},"requestId":"r1"}'
```

```json
{
  "status": "Completed",
  "result": {
    "description": "a cartoon character with a blue hat",
    "assetType": "Texture2D",
    "engine": "blip",
    "model": "Salesforce/blip-image-captioning-base@82a37760"
  }
}
```

Choose between two engines:
- **`blip`** (default) — open-ended natural language captions
- **`clip`** — zero-shot classification against game-asset labels (sprite, mesh, UI, material, etc.)

**Dependencies:** Requires `python3` (>= 3.10) and [`uv`](https://docs.astral.sh/uv/) — run `unifocl init` to install them automatically. The Python script pulls [`transformers`](https://pypi.org/project/transformers/), [`torch`](https://pypi.org/project/torch/) (CPU), and [`Pillow`](https://pypi.org/project/Pillow/) via `uv run --script` (auto-cached, no manual pip install needed). The first invocation also downloads the model weights (~990 MB for BLIP, ~600 MB for CLIP) from HuggingFace; subsequent runs load entirely from cache with no network access. Model revisions are pinned to exact commit SHAs for supply-chain safety.

See the [Command Reference — Asset Describe](docs/command-reference.md#6-asset-describe-local-vision) for full details.

## Documentation Reference

To keep this README clean, detailed technical specifications and command lists have been split into dedicated documents:

| Document | Description |
| --- | --- |
| [Command & TUI Reference](docs/command-reference.md) | Full list of slash commands, contextual operations, keyboard shortcuts, dry-run mechanics, and eval details. |
| [Agentic Workflow & Architecture](docs/agentic-workflow.md) | Deep dive into the daemon architecture, JSON envelopes, ExecV2 endpoints, concurrent worktrees, and the Persistence Safety Contract. |
| [Custom Commands Guide](docs/custom-commands.md) | How to expose your C# methods as MCP tools with `[UnifoclCommand]`. |
| [MCP Server Architecture](docs/mcp-server-architecture.md) | Built-in MCP server setup, agent JSON configuration, and multi-client guide. |
| [Editor Compilation](docs/editor-compilation.md) | Details on headless/CI compilation behavior and zero-touch recompilation. |
| [Validate & Build Workflow](docs/validate-build-workflow.md) | Project validation checks and build workflow commands. |
| [Test Orchestration](docs/test-orchestration.md) | Unity test runner integration (EditMode/PlayMode). |
| [Project Diagnostics](docs/project-diagnostics.md) | Assembly graphs, scene deps, compile errors, and other read-only introspection. |
| [Debug Artifact Workflow](docs/debug-artifact-workflow.md) | Tiered debug report collection (prep → playmode → collect) for agents and CI. |

## Contributing & License

External contributions are accepted for version 0.3.0 and later.

Unless explicitly stated otherwise, any Contribution intentionally submitted for inclusion in version 0.3.0 and later is licensed under the Apache License 2.0.

Apache License 2.0 applies to version 0.3.0 and all later versions.

All content before version 0.3.0 is proprietary and all rights reserved.
