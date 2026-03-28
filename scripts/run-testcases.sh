#!/usr/bin/env bash
# scripts/run-testcases.sh — Agentic test-case suite runner
#
# Executes a test suite JSON file against any agentic CLI that speaks the
# AgenticResponseEnvelope format (status, meta.exitCode, data, errors).
#
# Usage:
#   ./scripts/run-testcases.sh <suite.json> [OPTIONS]
#
# Options:
#   --project <path>          Override project path from suite file
#   --runner <cmd>            Override runner command from suite file
#   --seed <seed>             Use a fixed session seed for all cases (skips per-case seeds)
#   --no-build                Pass through to runner (skip build step)
#   --unity-src <dir>         Directory of Unity .cs source files to check for staleness
#                             (default: <script-dir>/../src/unifocl.unity if it exists)
#   --force-restart-daemon    Kill and remove lockfile for any stale daemon before running
#
# Suite file format: see schema at .codex/testcase.schema.json
#
# Placeholders resolved in `runner` and `project` fields:
#   {suite_dir}   — absolute path of the directory containing the suite file
#
set -euo pipefail

# ── Prerequisites ─────────────────────────────────────────────────────────────

if ! command -v jq &>/dev/null; then
    echo "ERROR: 'jq' is required. Install with: brew install jq" >&2
    exit 2
fi

# ── Argument parsing ──────────────────────────────────────────────────────────

suite_file=""
override_project=""
override_runner=""
pass_no_build=""
override_seed=""
override_unity_src=""
force_restart_daemon=""

while [[ $# -gt 0 ]]; do
    case "$1" in
        --project) override_project="$2"; shift 2 ;;
        --runner)  override_runner="$2";  shift 2 ;;
        --no-build) pass_no_build="--no-build"; shift ;;
        --seed)    override_seed="$2"; shift 2 ;;
        --unity-src) override_unity_src="$2"; shift 2 ;;
        --force-restart-daemon) force_restart_daemon=1; shift ;;
        -*) echo "ERROR: Unknown option: $1" >&2; exit 2 ;;
        *) suite_file="$1"; shift ;;
    esac
done

if [[ -z "$suite_file" ]]; then
    echo "Usage: run-testcases.sh <suite.json> [--project <path>] [--runner <cmd>] [--seed <seed>] [--no-build] [--unity-src <dir>] [--force-restart-daemon]" >&2
    exit 2
fi

if [[ ! -f "$suite_file" ]]; then
    echo "ERROR: Suite file not found: $suite_file" >&2
    exit 2
fi

suite_file="$(cd "$(dirname "$suite_file")" && pwd)/$(basename "$suite_file")"
suite_dir="$(dirname "$suite_file")"

# ── Resolve placeholders ──────────────────────────────────────────────────────

resolve_placeholders() {
    local val="$1"
    echo "${val//\{suite_dir\}/$suite_dir}"
}

# ── Suite metadata ────────────────────────────────────────────────────────────

suite_name="$(jq -r '.suite' "$suite_file")"
suite_mode="$(jq -r '.mode // "project"' "$suite_file")"
total_cases="$(jq '.cases | length' "$suite_file")"

raw_runner="$(jq -r '.runner // ""' "$suite_file")"
runner_cmd="${override_runner:-$(resolve_placeholders "$raw_runner")}"

if [[ -z "$runner_cmd" ]]; then
    echo "ERROR: No runner command. Set 'runner' in suite file or pass --runner." >&2
    exit 2
fi

raw_project="$(jq -r '.project // ""' "$suite_file")"
project_path="${override_project:-$(resolve_placeholders "$raw_project")}"

if [[ -z "$project_path" ]]; then
    echo "ERROR: No project path. Set 'project' in suite file or pass --project." >&2
    exit 2
fi

# ── Assert evaluator ──────────────────────────────────────────────────────────

evaluate_assert() {
    local response_file="$1"
    local assert_json="$2"
    local step_label="$3"

    if [[ "$assert_json" == "null" || -z "$assert_json" ]]; then
        assert_json='{ ".status": "ok", ".meta.exitCode": 0 }'
    fi

    local failed=0
    local failure_msg=""

    while IFS= read -r key; do
        local raw_expected
        raw_expected="$(echo "$assert_json" | jq -c --arg k "$key" '.[$k]')"

        local actual
        actual="$(jq -c "$key" "$response_file" 2>/dev/null || echo "null")"

        local op
        op="$(echo "$raw_expected" | jq -r 'if type == "object" then keys[0] else "" end' 2>/dev/null || echo "")"

        local ok=0
        local expected_display="$raw_expected"

        case "$op" in
            "\$exists")
                local want_exists
                want_exists="$(echo "$raw_expected" | jq -r '."$exists"')"
                if [[ "$want_exists" == "true" ]]; then
                    [[ "$actual" != "null" ]] && ok=1
                    expected_display="exists"
                else
                    [[ "$actual" == "null" ]] && ok=1
                    expected_display="null/missing"
                fi
                ;;
            "\$ne")
                local ne_val
                ne_val="$(echo "$raw_expected" | jq -c '."$ne"')"
                [[ "$actual" != "$ne_val" ]] && ok=1
                expected_display="!= ${ne_val}"
                ;;
            "\$gt")
                local gt_val
                gt_val="$(echo "$raw_expected" | jq -r '."$gt"')"
                (( $(echo "$(echo "$actual" | jq -r '.') > $gt_val" | bc -l 2>/dev/null || echo 0) )) && ok=1
                expected_display="> ${gt_val}"
                ;;
            "\$gte")
                local gte_val
                gte_val="$(echo "$raw_expected" | jq -r '."$gte"')"
                (( $(echo "$(echo "$actual" | jq -r '.') >= $gte_val" | bc -l 2>/dev/null || echo 0) )) && ok=1
                expected_display=">= ${gte_val}"
                ;;
            "\$contains")
                local needle
                needle="$(echo "$raw_expected" | jq -r '."$contains"')"
                if echo "$actual" | jq -e --arg n "$needle" \
                    'if type == "array" then contains([$n]) elif type == "string" then contains($n) else false end' \
                    &>/dev/null; then
                    ok=1
                fi
                expected_display="contains(\"${needle}\")"
                ;;
            "")
                [[ "$actual" == "$raw_expected" ]] && ok=1
                ;;
        esac

        if [[ $ok -eq 0 ]]; then
            failed=1
            failure_msg="${step_label}: ${key}  expected=${expected_display}  actual=${actual}"
            break
        fi
    done < <(echo "$assert_json" | jq -r 'keys[]')

    if [[ $failed -eq 1 ]]; then
        echo "$failure_msg"
        return 1
    fi
    return 0
}

# ── Daemon freshness check ────────────────────────────────────────────────────
#
# Scans ~/.unifocl-runtime/daemons/*.json for a daemon whose projectPath matches
# the suite's project. If the daemon was started before the newest .cs file in
# $unity_src_dir, it is considered stale.
#
# With --force-restart-daemon: kills the stale process and removes its lockfile.
# Without the flag:            prints a warning but continues.
#
check_daemon_freshness() {
    local project="$1"
    local unity_src_dir="$2"
    local runtime_dir="${HOME}/.unifocl-runtime/daemons"

    [[ ! -d "$runtime_dir" ]] && return 0
    [[ -z "$unity_src_dir" || ! -d "$unity_src_dir" ]] && return 0

    # Find the newest .cs mtime in the Unity source tree (seconds since epoch).
    local newest_src_epoch
    # macOS stat syntax
    newest_src_epoch=$(find "$unity_src_dir" -name "*.cs" -exec stat -f '%m' {} \; 2>/dev/null \
        | sort -rn | head -1)
    # Linux fallback
    if [[ -z "$newest_src_epoch" ]]; then
        newest_src_epoch=$(find "$unity_src_dir" -name "*.cs" -printf '%T@\n' 2>/dev/null \
            | sort -rn | head -1 | cut -d. -f1)
    fi
    [[ -z "$newest_src_epoch" || "$newest_src_epoch" == "0" ]] && return 0

    local norm_project
    norm_project=$(cd "$project" 2>/dev/null && pwd || echo "$project")

    for lockfile in "$runtime_dir"/*.json; do
        [[ -f "$lockfile" ]] || continue

        local lf_project lf_started lf_pid lf_port
        lf_project=$(jq -r '.projectPath // ""' "$lockfile" 2>/dev/null)
        lf_started=$(jq -r '.startedAtUtc // ""' "$lockfile" 2>/dev/null)
        lf_pid=$(jq -r '.pid // 0' "$lockfile" 2>/dev/null)
        lf_port=$(jq -r '.port // 0' "$lockfile" 2>/dev/null)

        # Normalize and compare project paths.
        local norm_lf
        norm_lf=$(echo "$lf_project" | sed 's|/$||')
        [[ "$norm_project" != "$norm_lf" ]] && continue

        # Convert ISO-8601 UTC timestamp to epoch.
        local daemon_epoch
        # macOS date
        daemon_epoch=$(date -j -f "%Y-%m-%dT%H:%M:%SZ" "${lf_started}" "+%s" 2>/dev/null || true)
        # GNU date fallback
        if [[ -z "$daemon_epoch" ]]; then
            daemon_epoch=$(date -d "${lf_started}" "+%s" 2>/dev/null || echo "0")
        fi
        [[ -z "$daemon_epoch" ]] && daemon_epoch=0

        if (( newest_src_epoch > daemon_epoch )); then
            if [[ -n "$force_restart_daemon" ]]; then
                echo "WARN: Stale daemon on port ${lf_port} (PID ${lf_pid}): Unity source changed after daemon start; force-restarting..."
                kill "$lf_pid" 2>/dev/null || true
                rm -f "$lockfile"
            else
                echo "WARN: Stale daemon detected (port ${lf_port}): Unity source files were modified after daemon start."
                echo "      Tests may execute against outdated compiled code."
                echo "      Re-run with --force-restart-daemon to force a clean restart."
            fi
        fi
    done
}

# ── Exec helper ───────────────────────────────────────────────────────────────

run_exec_step() {
    local cmd_text="$1"
    local session_seed="$2"
    local mode="$3"
    local tmp_out
    tmp_out="$(mktemp)"

    # Split runner_cmd into array for safe exec
    local runner_args
    IFS=' ' read -ra runner_args <<< "$runner_cmd"

    local extra_env=()
    [[ -n "${UNIFOCL_GLOBAL_PAYLOAD_ROOT:-}" ]] && extra_env+=("UNIFOCL_GLOBAL_PAYLOAD_ROOT=${UNIFOCL_GLOBAL_PAYLOAD_ROOT}")
    [[ -n "${UNIFOCL_CONFIG_ROOT:-}" ]] && extra_env+=("UNIFOCL_CONFIG_ROOT=${UNIFOCL_CONFIG_ROOT}")

    local exec_args=(
        exec "$cmd_text"
        --agentic --format json
        --session-seed "$session_seed"
        --project "$project_path"
        --mode "$mode"
    )

    if [[ ${#extra_env[@]} -gt 0 ]]; then
        env "${extra_env[@]}" "${runner_args[@]}" "${exec_args[@]}" > "$tmp_out" 2>/dev/null || true
    else
        "${runner_args[@]}" "${exec_args[@]}" > "$tmp_out" 2>/dev/null || true
    fi

    local envelope
    envelope="$(jq -c '.' "$tmp_out" 2>/dev/null || echo '{}')"
    rm -f "$tmp_out"
    echo "$envelope"
}

# ── Pre-suite: stale daemon detection ────────────────────────────────────────

_script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
_default_unity_src="${_script_dir}/../src/unifocl.unity"

_unity_src_dir="${override_unity_src}"
if [[ -z "$_unity_src_dir" && -d "$_default_unity_src" ]]; then
    _unity_src_dir="$(cd "$_default_unity_src" && pwd)"
fi

check_daemon_freshness "$project_path" "$_unity_src_dir"

# ── Main loop ─────────────────────────────────────────────────────────────────

passed=0
failed_count=0
results=()

for (( ci=0; ci < total_cases; ci++ )); do
    case_id="$(jq -r ".cases[$ci].id" "$suite_file")"
    predict="$(jq -r ".cases[$ci].predict // \"pass\"" "$suite_file")"
    step_count="$(jq ".cases[$ci].steps | length" "$suite_file")"

    if [[ -n "$override_seed" ]]; then
        session_seed="$override_seed"
    else
        raw_seed="${suite_name}-${case_id}"
        session_seed="${raw_seed:0:80}"
        session_seed="${session_seed//[^a-zA-Z0-9_-]/-}"
    fi

    case_failed=0
    case_fail_msg=""

    for (( si=0; si < step_count; si++ )); do
        step_cmd="$(jq -r ".cases[$ci].steps[$si].cmd" "$suite_file")"
        step_assert="$(jq -c ".cases[$ci].steps[$si].assert // null" "$suite_file")"
        step_mode="$(jq -r ".cases[$ci].steps[$si].mode // \"${suite_mode}\"" "$suite_file")"
        step_label="step-$((si+1))"

        tmp_env="$(mktemp)"
        run_exec_step "$step_cmd" "$session_seed" "$step_mode" > "$tmp_env"

        if ! jq -e '.' "$tmp_env" &>/dev/null; then
            case_failed=1
            case_fail_msg="${step_label}: runner returned invalid JSON"
            rm -f "$tmp_env"
            break
        fi

        fail_reason="$(evaluate_assert "$tmp_env" "$step_assert" "$step_label" 2>&1)" || {
            case_failed=1
            case_fail_msg="$fail_reason"
            rm -f "$tmp_env"
            break
        }
        rm -f "$tmp_env"
    done

    if [[ "$predict" == "fail" ]]; then
        if [[ $case_failed -eq 1 ]]; then
            results+=("[EXPECTED FAIL] ${case_id}")
            (( passed++ )) || true
        else
            results+=("[UNEXPECTED PASS] ${case_id}  (predict=fail but all assertions passed)")
            (( failed_count++ )) || true
        fi
    else
        if [[ $case_failed -eq 0 ]]; then
            step_word="$( [[ $step_count -eq 1 ]] && echo "step" || echo "steps" )"
            results+=("[PASS] ${case_id} (${step_count} ${step_word})")
            (( passed++ )) || true
        else
            results+=("[FAIL] ${case_id}  ${case_fail_msg}")
            (( failed_count++ )) || true
        fi
    fi
done

# ── Report ────────────────────────────────────────────────────────────────────

for line in "${results[@]}"; do
    echo "$line"
done

echo "---"
exit_code=0
[[ $failed_count -gt 0 ]] && exit_code=1
echo "Suite: ${suite_name} | ${passed}/${total_cases} passed | exit ${exit_code}"
exit $exit_code
