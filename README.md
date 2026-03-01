# Unifocl - TUI Application

## Overview
Unifocl is a Terminal User Interface (TUI) application built on .NET 10, designed to streamline CLI workflows with a focus on scalability, security, and maintainability. The application integrates seamlessly with Unity Editor via a daemon bridge architecture, bridging the gap between console-based CLI operations and Unity Editor tasks.

## Key Features
- **CLI + Daemon Control:** Lightweight stateless runtime that efficiently processes commands without relying on memory-intensive operations.
- **Unity Editor Integration:** Safely communicates with Unity Editor using a shared payload system, ensuring robustness and security.
- **Scalable Architecture:** Modular structure ensures that the application can be extended or scaled with minimal changes.
- **Written in .NET 10:** Leveraging advanced features for building a robust and performant application.

## Repository Structure
```plaintext
src/unifocl/
├── Program.cs    # CLI bootstrapping, command palette loop
├── Services/     # Runtime behavior, lifecycle, daemon/process, routing
├── Models/       # Contracts and model types
└── unifocl.csproj

src/unifocl.unity/
├── EditorScripts/ # Unity editor-side daemon bridge
└── SharedModels/  # Shared bridge DTO/contracts
```

## Development Workflow
- Always start work by creating a new branch from `main`.
- Commit changes and create PRs adhering to strict policies as outlined in `AGENT.md`.
- Before pushing, ensure the latest changes from `main` are merged into your branch to resolve conflicts early.

## Contribution Guidelines
- Follow coding standards detailed in `AGENT.md`, e.g., use `async/await`, validate external inputs, maintain statelessness.
- Add appropriate comments and documentation for all changes.
- Never commit credentials or sensitive information.

For detailed guidelines, check [AGENT.md](https://github.com/Kiankinakomochi/unifocl/blob/main/AGENT.md).

## Build and Debug Commands
Run builds/tests via:
```
dotnet build --project src/unifocl/unifocl.csproj --disable-build-servers -v minimal
```

## Contact
For more information or troubleshooting, please contact the repository maintainers.
