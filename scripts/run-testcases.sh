#!/usr/bin/env bash
# scripts/run-testcases.sh — unifocl project wrapper for the global test runner
#
# Delegates to ~/.claude/scripts/run-testcases.sh with unifocl defaults pre-set.
# Overrides: --project, --no-build (passed through)
#
# Usage:
#   ./scripts/run-testcases.sh <suite.json> [--project <path>] [--no-build]
#
set -euo pipefail

global_runner="${HOME}/.claude/scripts/run-testcases.sh"

if [[ ! -x "$global_runner" ]]; then
    echo "ERROR: Global runner not found or not executable: $global_runner" >&2
    exit 2
fi

exec "$global_runner" "$@"
