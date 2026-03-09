# Milestone: Worktree Isolation and Multi-Agent Daemon Safety

This repository tracks the milestone as local execution steps (not GitHub Issues).

## Step 1: Implement Git Worktree Provisioning Script

Status: Implemented

- Bash implementation: `src/unifocl/scripts/agent-worktree.sh` (`provision`, `teardown`)
- PowerShell implementation: `src/unifocl/scripts/agent-worktree.ps1` (`provision`, `teardown`)

Acceptance criteria mapping:
- Creates isolated branch directory via `git worktree add <path> -b <branch> origin/main`.
- Includes teardown using `git worktree remove --force <path>`.

## Step 2: Implement Library Cache Seeding Strategy

Status: Implemented

- Bash implementation: `seed` command and `--seed-library` option in `provision`.
- PowerShell implementation: `seed` command and `-SeedLibrary` option in `provision`.

Acceptance criteria mapping:
- Seeds `<source-project>/Library` to `<worktree>/Library` immediately post-provisioning.
- Enables warm-start Unity boot path for headless daemon startup.

## Step 3: Dynamic unifocl Daemon Port Assignment

Status: Implemented

- Bash implementation: `start-daemon` selects an open localhost port in range and runs:
  - `/daemon start --project <path> --port <dynamic-port> --headless`
- PowerShell implementation: same behavior with TCP listener-based port probing.

Acceptance criteria mapping:
- Orchestrator starts daemon in provisioned worktree and validates connection via `/ping`.

## Step 4: Agent Lifecycle Orchestration Documentation

Status: Implemented

- Lifecycle and boundaries doc: `AGENT_WORKTREE_LIFECYCLE.md`

Acceptance criteria mapping:
- Documents strict pipeline:
  - Initialize -> Seed -> Boot Daemon -> Execute -> Commit/Push -> Teardown
