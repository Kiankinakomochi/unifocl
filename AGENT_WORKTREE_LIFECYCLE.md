# Agent Worktree Lifecycle

This document defines the strict lifecycle for autonomous AI agents that mutate Unity projects through isolated git worktrees.

## Pipeline

1. Initialize
- Provision a dedicated branch worktree using `git worktree add <path> -b <agent-branch> origin/main`.
- Never share a mutable branch/worktree between agents.

2. Seed
- Copy warmed Unity cache into the new worktree to avoid cold import:
  - macOS/Linux: `cp -a <MainProject>/Library <Worktree>/Library`
  - PowerShell: `Copy-Item <MainProject>\Library <Worktree>\Library -Recurse`
- Seeding happens immediately after worktree creation and before daemon boot.

3. Boot Daemon
- Allocate an open localhost port dynamically.
- Start daemon from inside the provisioned worktree with explicit project path and selected port:
  - `unifocl /daemon start --project <path> --port <dynamic-port> --headless`
- Validate readiness via `http://127.0.0.1:<dynamic-port>/ping` before mutation commands.

4. Execute
- Run planned CLI automation steps only after daemon is healthy.
- Keep all file mutations scoped to the provisioned worktree.

5. Commit/Push
- Commit only the agent branch changes.
- Push branch and open/prepare review artifacts.

6. Teardown
- Stop daemon tied to the worktree.
- Remove worktree via `git worktree remove --force <path>` and run `git worktree prune`.

## Operating Boundaries

- No cross-worktree file edits.
- No port reuse assumptions across agents.
- No shared mutable daemon state between projects.
- Teardown is mandatory after each completed run.

## Reference Scripts

- Bash: `src/unifocl/scripts/agent-worktree.sh`
- PowerShell: `src/unifocl/scripts/agent-worktree.ps1`
