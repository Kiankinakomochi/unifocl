#!/usr/bin/env bash
# tests/test-stale-daemon-runner.sh — Unit tests for run-testcases.sh check_daemon_freshness()
#
# Runs entirely in a temp directory; no Unity project or live daemon required.
# Exit 0 = all tests passed.  Exit 1 = at least one failure.
set -euo pipefail

# ── Setup ─────────────────────────────────────────────────────────────────────

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
RUNNER="${SCRIPT_DIR}/../scripts/run-testcases.sh"

if [[ ! -f "$RUNNER" ]]; then
    echo "ERROR: runner not found at $RUNNER" >&2
    exit 2
fi

TMPROOT="$(mktemp -d)"
trap 'rm -rf "$TMPROOT"' EXIT

# The function uses ${HOME}/.unifocl-runtime/daemons — mirror that layout.
FAKE_HOME="${TMPROOT}/home"
RUNTIME_DIR="${FAKE_HOME}/.unifocl-runtime/daemons"
UNITY_SRC="${TMPROOT}/src/unifocl.unity"
PROJECT_PATH="${TMPROOT}/MyProject"

mkdir -p "$RUNTIME_DIR" "$UNITY_SRC" "$PROJECT_PATH"

# ── Extract and define check_daemon_freshness in this shell ───────────────────
#
# We write the extracted function body to a temp file, then source it.
# This avoids process-substitution issues on macOS bash 3.2.

_fn_tmp="$(mktemp)"
sed -n '/^check_daemon_freshness()/,/^}/p' "$RUNNER" > "$_fn_tmp"
# shellcheck source=/dev/null
source "$_fn_tmp"
rm -f "$_fn_tmp"

# ── Test helpers ──────────────────────────────────────────────────────────────

passed=0
failed=0

check() {
    local label="$1"
    local result="$2"   # "pass" or "fail"
    if [[ "$result" == "pass" ]]; then
        echo "[PASS] $label"
        (( passed++ )) || true
    else
        echo "[FAIL] $label"
        (( failed++ )) || true
    fi
}

assert_contains() {
    local label="$1" needle="$2" haystack="$3"
    if echo "$haystack" | grep -qF -- "$needle"; then
        check "$label" pass
    else
        check "$label" fail
        echo "       expected to find:  $needle"
        echo "       in output:"
        echo "$haystack" | sed 's/^/         /'
    fi
}

assert_not_contains() {
    local label="$1" needle="$2" haystack="$3"
    if ! echo "$haystack" | grep -qF -- "$needle"; then
        check "$label" pass
    else
        check "$label" fail
        echo "       expected NOT to find: $needle"
        echo "       in output:"
        echo "$haystack" | sed 's/^/         /'
    fi
}

assert_file_missing() {
    local label="$1" path="$2"
    if [[ ! -f "$path" ]]; then
        check "$label" pass
    else
        check "$label" fail
        echo "       expected file to be gone: $path"
    fi
}

assert_file_exists() {
    local label="$1" path="$2"
    if [[ -f "$path" ]]; then
        check "$label" pass
    else
        check "$label" fail
        echo "       expected file to exist: $path"
    fi
}

write_lockfile() {
    local port="$1" pid="$2" started_at="$3" project="$4"
    cat > "${RUNTIME_DIR}/${port}.json" <<EOF
{
  "port": ${port},
  "pid": ${pid},
  "startedAtUtc": "${started_at}",
  "unityPath": null,
  "headless": true,
  "projectPath": "${project}",
  "lastHeartbeatUtc": "${started_at}"
}
EOF
}

write_cs() {
    local name="$1"
    echo "// dummy" > "${UNITY_SRC}/${name}"
}

touch_past() {
    # Set file mtime to N seconds in the past.
    local file="$1" seconds="$2"
    local ts
    # macOS
    ts=$(date -v "-${seconds}S" "+%Y%m%d%H%M.%S" 2>/dev/null) \
        || ts=$(date -d "${seconds} seconds ago" "+%Y%m%d%H%M.%S" 2>/dev/null)
    touch -t "$ts" "$file"
}

run_check() {
    # Wrapper that sets HOME to FAKE_HOME so the function finds our fake daemons dir.
    local out
    HOME="$FAKE_HOME" out="$(check_daemon_freshness "$PROJECT_PATH" "$UNITY_SRC" 2>&1)" || true
    echo "$out"
}

# ── Tests ─────────────────────────────────────────────────────────────────────

echo "--- check_daemon_freshness unit tests ---"

# 1. No lockfiles → no output
rm -f "${RUNTIME_DIR}"/*.json 2>/dev/null || true
force_restart_daemon=""
out="$(run_check)"
assert_not_contains "no lockfiles → silent" "WARN" "$out"


# 2. Daemon is FRESH (source older than StartedAtUtc) → no warning
rm -f "${RUNTIME_DIR}"/*.json 2>/dev/null || true
write_lockfile 18080 99999 "2099-12-31T23:59:59Z" "$PROJECT_PATH"
write_cs "OldScript.cs"
touch_past "${UNITY_SRC}/OldScript.cs" 300   # mtime 5 min in the past
force_restart_daemon=""
out="$(run_check)"
assert_not_contains "fresh daemon → silent" "WARN" "$out"
rm -f "${RUNTIME_DIR}"/*.json


# 3. Daemon is STALE (source newer than StartedAtUtc) → warn
rm -f "${RUNTIME_DIR}"/*.json 2>/dev/null || true
write_lockfile 18080 99999 "2000-01-01T00:00:00Z" "$PROJECT_PATH"
write_cs "NewScript.cs"
# file just written, mtime is "now" > year-2000 daemon
force_restart_daemon=""
out="$(run_check)"
assert_contains     "stale daemon → WARN printed" "WARN" "$out"
assert_contains     "stale daemon → hints --force-restart-daemon" "--force-restart-daemon" "$out"
assert_contains     "stale daemon → mentions port" "18080" "$out"
rm -f "${RUNTIME_DIR}"/*.json


# 4. --force-restart-daemon removes lockfile
rm -f "${RUNTIME_DIR}"/*.json 2>/dev/null || true
write_lockfile 18080 99999 "2000-01-01T00:00:00Z" "$PROJECT_PATH"
lockfile="${RUNTIME_DIR}/18080.json"
assert_file_exists "lockfile present before force-restart" "$lockfile"
force_restart_daemon=1
HOME="$FAKE_HOME" check_daemon_freshness "$PROJECT_PATH" "$UNITY_SRC" >/dev/null 2>&1 || true
force_restart_daemon=""
assert_file_missing "force-restart → lockfile removed" "$lockfile"


# 5. Project path mismatch → no warning
rm -f "${RUNTIME_DIR}"/*.json 2>/dev/null || true
write_lockfile 18080 99999 "2000-01-01T00:00:00Z" "/completely/different/project"
force_restart_daemon=""
out="$(run_check)"
assert_not_contains "project mismatch → silent" "WARN" "$out"
rm -f "${RUNTIME_DIR}"/*.json


# 6. Unity src dir has no .cs files → no warning
rm -f "${RUNTIME_DIR}"/*.json "${UNITY_SRC}"/*.cs 2>/dev/null || true
write_lockfile 18080 99999 "2000-01-01T00:00:00Z" "$PROJECT_PATH"
force_restart_daemon=""
out="$(run_check)"
assert_not_contains "no .cs files → silent" "WARN" "$out"
rm -f "${RUNTIME_DIR}"/*.json


# ── Summary ───────────────────────────────────────────────────────────────────

echo "---"
echo "Results: ${passed}/$(( passed + failed )) passed"
[[ $failed -eq 0 ]] && exit 0 || exit 1
