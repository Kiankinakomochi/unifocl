# unifocl

A terminal-first Unity development companion. **unifocl** provides a structured way to interact with and navigate your Unity projects directly from the command line.

It is not designed to replace the Unity Editor. Instead, it serves as a supplementary tool for developers who prefer managing project structure, assets, and hierarchies via a CLI or TUI (Terminal User Interface).

## Features

Instead of relying solely on the Unity Editor's graphical interface, unifocl offers:
* **Mode-based navigation:** Context-aware environments for navigating the Hierarchy, Project, and Inspector.
* **Deterministic manipulation:** Command-driven file and object operations.
* **Focused interface:** A clean CLI/TUI experience built with Spectre.Console.

## Architecture


unifocl is a .NET console application built for cross-platform compatibility (Windows, macOS, Linux). The application is divided into four primary layers:

1.  **CLI Layer:** Handles commands and structured user interaction.
2.  **Mode System:** Manages the context-aware environments (Hierarchy, Project, Inspector).
3.  **Daemon Layer:** A persistent background coordinator that tracks project state.
4.  **Unity Bridge:** The communication interface between the daemon and the Unity Editor/runtime.

## The unifocl Daemon

The daemon is a foundational part of the architecture. Rather than tying structural project operations directly to the Editor process, unifocl uses a persistent background daemon to separate project manipulation from the GUI state. 

This enables two operational modes:

* **Headless Project Mode:** When Unity is closed, the daemon operates directly on project files. It maintains structural consistency and handles deterministic filesystem-level edits. This is primarily useful for CI/CD, remote manipulation, and script-driven workflows. Unity will detect and reimport these changes upon its next launch.
* **Live Editor Mode:** When Unity is open, the daemon establishes a bridge connection to synchronize with the Editor's state. It executes hierarchy and component operations safely through Unity's APIs to respect the runtime context.

## Installation

unifocl is currently distributed as source code and requires a modern .NET runtime. Future distribution methods (like a .NET Global Tool, Homebrew, or Winget) are planned but not yet implemented.

### Clone & Build (Debug)

'''bash
git clone https://github.com/Kiankinakomochi/unifocl.git
cd unifocl
dotnet build
dotnet run --project src/unifocl
'''
*Debug build output is located in:* `src/unifocl/bin/Debug/`

### Release Build

'''bash
dotnet build -c Release
'''
*Release output is located in:* `src/unifocl/bin/Release/`. You can run the generated binary directly from this directory.

---

## Command & Feature Guide

When you launch unifocl, you will be greeted by a boot screen. From here, the CLI operates as an interactive shell using **slash commands** (e.g., `/open`) for system and lifecycle operations, and **standard commands** (e.g., `ls`, `cd`) for contextual project operations.

### 1. System & Lifecycle Commands
These commands manage your session, project loading, and CLI configuration. They are prefixed with a slash (`/`).

| Command | Alias | Description |
| :--- | :--- | :--- |
| `/open <path> [--allow-unsafe]` | `/o` | Open a Unity project. Starts/attaches to the daemon and loads metadata. |
| `/close` | `/c` | Detach from the current project and stop the attached daemon. |
| `/quit` | `/q`, `/exit` | Exit the CLI client (leaves the daemon running). |
| `/new <name> [version]` | | Bootstrap a new Unity project. |
| `/clone <git-url>` | | Clone a repository and setup local CLI bridge config. |
| `/recent [idx]` | | List recent projects or open one by index. |
| `/config <get/set/list/reset>`| `/cfg` | Manage CLI preferences (e.g., themes). |
| `/status` | `/st` | Show daemon, editor, project, and session status summary. |
| `/init [path]` | | Generate bridge config and install editor-side dependencies. |
| `/clear` | | Clear and redraw the boot screen and log. |
| `/help [topic]` | `/?` | Show help by topic. |

> **Note:** Additional diagnostic commands (`/doctor`, `/logs`, `/scan`, `/info`, `/unity detect`) are available but may be in active development.

### 2. Daemon Management
The daemon maintains a persistent connection to the project. Manage it using the `/daemon` (or `/d`) command suite.

| Subcommand | Description |
| :--- | :--- |
| `start` | Start a daemon. Accepts flags: `--port`, `--unity <path>`, `--project <path>`, `--headless`, `--allow-unsafe`. |
| `stop` | Stop the daemon instance controlled by this CLI. |
| `restart` | Restart the currently attached daemon. |
| `ps` | List running daemon instances, ports, uptimes, and associated projects. |
| `attach <port>` | Attach the CLI to an existing daemon at the specified port. |
| `detach` | Detach the CLI but keep the daemon alive in the background. |

### 3. Mode Switching
Once a project is opened, use these commands to switch your active context.

| Command | Alias | Description |
| :--- | :--- | :--- |
| `/project` | `/p` | Switch to Project mode (asset structure navigation). |
| `/hierarchy` | `/h` | Switch to Hierarchy mode (scene structure TUI). |
| `/inspect <idx/path>`| `/i` | Switch to Inspector mode and focus a target. |

### 4. Contextual Operations (Non-Slash Commands)
When inside a specific mode (Project, Hierarchy, or Inspector), omit the slash to interact directly with the active environment. Mutating operations are safely routed through the Unity Editor bridge via the daemon.

| Command | Alias | Description |
| :--- | :--- | :--- |
| `list` | `ls` | List entries in the current active context. |
| `enter <idx>` | `cd` | Enter the selected node, folder, or component by index. |
| `up` | `..` | Navigate up one level to the parent. |
| `make <type> <name>`| `mk` | Create an item (e.g., `mk script Player`, `mk gameobject`). |
| `load <idx/name>` | | Load/open a scene or script. |
| `remove <idx>` | `rm` | Remove the selected item. |
| `rename <idx> <new>`| `rn` | Rename the selected item. |
| `set <field> <val>` | `s` | Set a field or property value. |
| `toggle <target>` | `t` | Toggle boolean/active/enabled flags. |
| `move <...>` | `mv` | Move, reparent, or reorder an item. |

### 5. Fuzzy Search & Intellisense
unifocl features a composer with Intellisense. 
* Type `/` to open the slash-command suggestion palette.
* Type any standard text to receive project-mode suggestions.

**Fuzzy Finding:**
Use the `f` or `ff` command to trigger fuzzy search across your project or inspector. You can scope searches using the `t:<type>` filter.
* **Syntax:** `f t:<type> <query>`
* **Supported Types:** `script`, `scene`, `prefab`, `material`, `animation`
* **Example:** `f t:script PlayerController`

### 6. Keybindings & Focus Modes
The CLI provides keyboard-driven navigation for interacting with lists and structures without typing out indices.

**Global Keybinds**
* **`F7`**: Toggle focus for Hierarchy TUI, Project navigator, or Recent projects list.
* **`F8`**: Toggle focus for Inspector.
* **`Esc`**: Dismiss Intellisense, or clear input if already dismissed.
* **`↑` / `↓`**: Navigate fuzzy/Intellisense candidates.
* **`Enter`**: Insert selected suggestion or commit input.

**Context-Specific Focus Navigation**
Once focused (`F7` or `F8`), the arrow keys and tab behave contextually:

| Action | Hierarchy Focus | Project Focus | Inspector Focus |
| :--- | :--- | :--- | :--- |
| **`↑` / `↓`** | Move highlighted GameObject | Move highlighted file/folder | Move highlighted component/field |
| **`Tab`** | Expand selected node | Reveal/open selected entry | Inspect selected component |
| **`Shift+Tab`**| Collapse selected node | Move to parent folder | Back to component list |
| **Exit Focus** | `Esc` or `F7` | `Esc` or `F7` | `Esc` or `F8` |

---

## Roadmap

Current development priorities include:
* Daemon stabilization and Unity bridge hardening.
* Command syntax stabilization.
* Packaging and distribution (Global Tool, Homebrew).
* Implementation of a script execution layer.

## Contributing & License

unifocl is currently maintained solely by the creator. External contributions are not being accepted at this time. 

All rights reserved by the Maintainer. No redistribution, modification, or reuse without explicit permission.
