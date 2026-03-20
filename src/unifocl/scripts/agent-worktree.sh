#!/usr/bin/env bash
set -euo pipefail

print_usage() {
    cat <<'USAGE'
Usage:
  agent-worktree.sh setup --worktree-path <path> --branch <codex/branch> [--project-path <path>] [--unity-version <version>] [--skip-version-bump] [--skip-compatcheck]
  agent-worktree.sh provision --repo-root <path> --worktree-path <path> --branch <branch> [--source-project <path>] [--seed-library]
  agent-worktree.sh setup-smoke-project --worktree-path <path> --project-path <path> [--unity-version <version>] [--force]
  agent-worktree.sh seed --source-project <path> --worktree-path <path>
  agent-worktree.sh start-daemon --worktree-path <path> --project-path <path> [--port-start <n>] [--port-end <n>]
  agent-worktree.sh teardown --repo-root <path> --worktree-path <path>

Commands:
  setup         One-shot AGENT bootstrap: branch from origin/main, sync submodules, bump CliVersion dev cycle, scaffold smoke project, write local.config, and run compatcheck.
  provision     Create isolated git worktree branch and optionally seed Library cache.
  setup-smoke-project  Scaffold a minimal Unity project for agentic smoke testing.
  seed          Copy source project's Library cache into provisioned worktree.
  start-daemon  Find an open localhost port and run: /daemon start --project <path> --port <port> --headless.
  teardown      Remove provisioned worktree via git worktree remove --force.
USAGE
}

die() {
    echo "error: $*" >&2
    exit 1
}

require_command() {
    command -v "$1" >/dev/null 2>&1 || die "required command not found: $1"
}

abs_path() {
    local value="$1"
    if [ -d "$value" ]; then
        (cd "$value" && pwd)
    elif [ -e "$value" ]; then
        local base
        base="$(dirname "$value")"
        local name
        name="$(basename "$value")"
        (cd "$base" && printf '%s/%s\n' "$(pwd)" "$name")
    else
        local base
        base="$(dirname "$value")"
        local name
        name="$(basename "$value")"
        mkdir -p "$base"
        (cd "$base" && printf '%s/%s\n' "$(pwd)" "$name")
    fi
}

seed_library_cache() {
    local source_project="$1"
    local worktree_path="$2"

    local source_library="$source_project/Library"
    local target_library="$worktree_path/Library"

    if [ ! -d "$source_library" ]; then
        die "source Library does not exist: $source_library"
    fi

    if [ -e "$target_library" ]; then
        die "target Library already exists: $target_library"
    fi

    cp -a "$source_library" "$target_library"
    echo "seeded Library cache: $source_library -> $target_library"
}

find_open_port() {
    local start_port="$1"
    local end_port="$2"

    require_command python3

    local port
    for ((port = start_port; port <= end_port; port++)); do
        if python3 - "$port" <<'PY'
import socket
import sys

port = int(sys.argv[1])
sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
try:
    sock.bind(("127.0.0.1", port))
except OSError:
    sys.exit(1)
finally:
    sock.close()

sys.exit(0)
PY
        then
            echo "$port"
            return 0
        fi
    done

    return 1
}

resolve_unity_version() {
    local explicit_version="$1"
    if [ -n "$explicit_version" ]; then
        echo "$explicit_version"
        return 0
    fi

    if [ -n "${UNITY_EDITOR_VERSION:-}" ]; then
        echo "${UNITY_EDITOR_VERSION}"
        return 0
    fi

    local detected=""
    detected="$(
        ls -d /Applications/Unity/Hub/Editor/* 2>/dev/null \
            | sed -E 's#.*/##' \
            | sort -V \
            | tail -n 1 || true
    )"

    if [ -n "$detected" ]; then
        echo "$detected"
        return 0
    fi

    # Deterministic fallback when Unity Hub is not available in the environment.
    echo "6000.0.0f1"
}

bump_cli_version_dev_cycle() {
    local version_file="$1"
    [ -f "$version_file" ] || die "CliVersion file not found: $version_file"

    local current_minor
    current_minor="$(
        sed -nE 's/^[[:space:]]*public const int Minor = ([0-9]+);/\1/p' "$version_file" \
            | head -n 1
    )"
    [ -n "$current_minor" ] || die "failed to parse CliVersion minor from: $version_file"

    local next_minor=$((current_minor + 1))
    local temp_file
    temp_file="$(mktemp -t cli-version.XXXXXX)"

    awk -v next_minor="$next_minor" '
        {
            line = $0
            if (line ~ /^[[:space:]]*public const int Minor = [0-9]+;/) {
                sub(/[0-9]+;/, next_minor ";", line)
                seen_minor = 1
            }
            if (line ~ /^[[:space:]]*public const string DevCycle = ".*";/) {
                sub(/".*";/, "\"a1\";", line)
                seen_dev = 1
            }
            print line
        }
        END {
            if (seen_minor != 1 || seen_dev != 1) {
                exit 2
            }
        }
    ' "$version_file" >"$temp_file" || {
        rm -f "$temp_file"
        die "failed to rewrite CliVersion file: $version_file"
    }

    mv "$temp_file" "$version_file"
    echo "cli-version-bootstrap: minor=${next_minor}, devcycle=a1"
}

run_setup() {
    local worktree_path="."
    local branch=""
    local project_path=".local/compatcheck-benchmark"
    local unity_version=""
    local skip_version_bump=false
    local skip_compatcheck=false

    while [ "$#" -gt 0 ]; do
        case "$1" in
            --worktree-path)
                worktree_path="$2"
                shift 2
                ;;
            --branch)
                branch="$2"
                shift 2
                ;;
            --project-path)
                project_path="$2"
                shift 2
                ;;
            --unity-version)
                unity_version="$2"
                shift 2
                ;;
            --skip-version-bump)
                skip_version_bump=true
                shift
                ;;
            --skip-compatcheck)
                skip_compatcheck=true
                shift
                ;;
            *)
                die "unknown setup option: $1"
                ;;
        esac
    done

    [ -n "$branch" ] || die "missing --branch"

    worktree_path="$(abs_path "$worktree_path")"
    require_command git
    git -C "$worktree_path" rev-parse --is-inside-work-tree >/dev/null 2>&1 \
        || die "not a git worktree: $worktree_path"
    git -C "$worktree_path" fetch origin main >/dev/null 2>&1 || true

    if git -C "$worktree_path" show-ref --verify --quiet "refs/heads/$branch"; then
        git -C "$worktree_path" switch "$branch"
        git -C "$worktree_path" merge --ff-only origin/main \
            || die "failed to fast-forward $branch to origin/main"
    else
        git -C "$worktree_path" switch -c "$branch" origin/main
    fi

    git -C "$worktree_path" submodule sync --recursive
    git -C "$worktree_path" submodule update --init --recursive

    if [ "$skip_version_bump" != true ]; then
        bump_cli_version_dev_cycle "$worktree_path/src/unifocl/Services/CliVersion.cs"
    fi

    local smoke_setup_args=(
        --worktree-path "$worktree_path"
        --project-path "$project_path"
        --force
    )
    if [ -n "$unity_version" ]; then
        smoke_setup_args+=(--unity-version "$unity_version")
    fi

    run_setup_smoke_project "${smoke_setup_args[@]}"

    local compat_script="$worktree_path/src/unifocl/scripts/agent-worktree-compatcheck-update.sh"
    [ -f "$compat_script" ] || die "compatcheck helper script not found: $compat_script"

    local compat_args=(
        --project-path "$project_path"
        --write-local-config
    )
    if [ "$skip_compatcheck" != true ]; then
        compat_args+=(--run-compatcheck)
    fi

    "$compat_script" "${compat_args[@]}"

    echo "setup-complete: $worktree_path"
    echo "active-branch: $(git -C "$worktree_path" rev-parse --abbrev-ref HEAD)"
}

run_provision() {
    local repo_root=""
    local worktree_path=""
    local branch=""
    local source_project=""
    local should_seed=false

    while [ "$#" -gt 0 ]; do
        case "$1" in
            --repo-root)
                repo_root="$2"
                shift 2
                ;;
            --worktree-path)
                worktree_path="$2"
                shift 2
                ;;
            --branch)
                branch="$2"
                shift 2
                ;;
            --source-project)
                source_project="$2"
                shift 2
                ;;
            --seed-library)
                should_seed=true
                shift
                ;;
            *)
                die "unknown provision option: $1"
                ;;
        esac
    done

    [ -n "$repo_root" ] || die "missing --repo-root"
    [ -n "$worktree_path" ] || die "missing --worktree-path"
    [ -n "$branch" ] || die "missing --branch"

    repo_root="$(abs_path "$repo_root")"
    worktree_path="$(abs_path "$worktree_path")"

    require_command git

    git -C "$repo_root" fetch origin main >/dev/null 2>&1 || true
    git -C "$repo_root" worktree add "$worktree_path" -b "$branch" origin/main

    if [ "$should_seed" = true ]; then
        [ -n "$source_project" ] || die "--seed-library requires --source-project"
        source_project="$(abs_path "$source_project")"
        seed_library_cache "$source_project" "$worktree_path"
    fi

    echo "provisioned worktree: $worktree_path"
    echo "branch: $branch"
}

run_seed() {
    local source_project=""
    local worktree_path=""

    while [ "$#" -gt 0 ]; do
        case "$1" in
            --source-project)
                source_project="$2"
                shift 2
                ;;
            --worktree-path)
                worktree_path="$2"
                shift 2
                ;;
            *)
                die "unknown seed option: $1"
                ;;
        esac
    done

    [ -n "$source_project" ] || die "missing --source-project"
    [ -n "$worktree_path" ] || die "missing --worktree-path"

    source_project="$(abs_path "$source_project")"
    worktree_path="$(abs_path "$worktree_path")"

    seed_library_cache "$source_project" "$worktree_path"
}

run_setup_smoke_project() {
    local worktree_path=""
    local project_path=""
    local unity_version=""
    local force=false

    while [ "$#" -gt 0 ]; do
        case "$1" in
            --worktree-path)
                worktree_path="$2"
                shift 2
                ;;
            --project-path)
                project_path="$2"
                shift 2
                ;;
            --unity-version)
                unity_version="$2"
                shift 2
                ;;
            --force)
                force=true
                shift
                ;;
            *)
                die "unknown setup-smoke-project option: $1"
                ;;
        esac
    done

    [ -n "$worktree_path" ] || die "missing --worktree-path"
    [ -n "$project_path" ] || die "missing --project-path"

    worktree_path="$(abs_path "$worktree_path")"
    local project_abs
    if [[ "$project_path" = /* ]]; then
        project_abs="$(abs_path "$project_path")"
    else
        project_abs="$(abs_path "$worktree_path/$project_path")"
    fi

    local resolved_unity_version
    resolved_unity_version="$(resolve_unity_version "$unity_version")"

    if [ -d "$project_abs" ] && [ "$force" != true ] && [ -n "$(ls -A "$project_abs" 2>/dev/null)" ]; then
        die "project path already exists and is not empty: $project_abs (use --force to update files in place)"
    fi

    mkdir -p \
        "$project_abs/Assets" \
        "$project_abs/Packages" \
        "$project_abs/ProjectSettings"

    cat > "$project_abs/Packages/manifest.json" <<'EOF'
{
  "dependencies": {
    "com.unity.ide.rider": "3.0.35",
    "com.unity.modules.imageconversion": "1.0.0",
    "com.unity.test-framework": "1.4.5",
    "com.unity.textmeshpro": "3.0.6",
    "com.unity.timeline": "1.8.9",
    "com.unity.ugui": "1.0.0"
  }
}
EOF

    cat > "$project_abs/ProjectSettings/ProjectVersion.txt" <<EOF
m_EditorVersion: ${resolved_unity_version}
m_EditorVersionWithRevision: ${resolved_unity_version} (agentic-smoke)
EOF

    cat > "$project_abs/README.smoke-agentic.md" <<'EOF'
# Agentic Smoke Project

This Unity project was generated by `src/unifocl/scripts/agent-worktree.sh setup-smoke-project`
for deterministic agentic-mode smoke tests.
EOF

    echo "smoke-project-ready: $project_abs"
    echo "unity-version: $resolved_unity_version"
}

run_start_daemon() {
    local worktree_path=""
    local project_path=""
    local port_start=18080
    local port_end=21999

    while [ "$#" -gt 0 ]; do
        case "$1" in
            --worktree-path)
                worktree_path="$2"
                shift 2
                ;;
            --project-path)
                project_path="$2"
                shift 2
                ;;
            --port-start)
                port_start="$2"
                shift 2
                ;;
            --port-end)
                port_end="$2"
                shift 2
                ;;
            *)
                die "unknown start-daemon option: $1"
                ;;
        esac
    done

    [ -n "$worktree_path" ] || die "missing --worktree-path"
    [ -n "$project_path" ] || die "missing --project-path"

    worktree_path="$(abs_path "$worktree_path")"
    local project_abs
    if [[ "$project_path" = /* ]]; then
        project_abs="$(abs_path "$project_path")"
    else
        project_abs="$(abs_path "$worktree_path/$project_path")"
    fi

    local selected_port
    selected_port="$(find_open_port "$port_start" "$port_end")" || die "failed to find open port in range $port_start-$port_end"

    local daemon_cmd
    daemon_cmd="/daemon start --project \"$project_abs\" --port $selected_port --headless"

    local startup_log
    startup_log="$(mktemp -t unifocl-daemon-start.XXXXXX.log)"

    (
        cd "$worktree_path"
        printf '%s\n/quit\n' "$daemon_cmd" | dotnet run --project src/unifocl/unifocl.csproj --disable-build-servers -v minimal >"$startup_log" 2>&1
    )

    require_command curl

    local ready=false
    for _ in $(seq 1 40); do
        if curl -fsS "http://127.0.0.1:$selected_port/ping" >/dev/null 2>&1; then
            ready=true
            break
        fi

        sleep 0.25
    done

    if [ "$ready" != true ]; then
        echo "daemon startup log: $startup_log" >&2
        die "daemon did not become ready on port $selected_port"
    fi

    echo "daemon-ready-port: $selected_port"
    echo "daemon-start-command: $daemon_cmd"
}

run_teardown() {
    local repo_root=""
    local worktree_path=""

    while [ "$#" -gt 0 ]; do
        case "$1" in
            --repo-root)
                repo_root="$2"
                shift 2
                ;;
            --worktree-path)
                worktree_path="$2"
                shift 2
                ;;
            *)
                die "unknown teardown option: $1"
                ;;
        esac
    done

    [ -n "$repo_root" ] || die "missing --repo-root"
    [ -n "$worktree_path" ] || die "missing --worktree-path"

    repo_root="$(abs_path "$repo_root")"
    worktree_path="$(abs_path "$worktree_path")"

    require_command git
    git -C "$repo_root" worktree remove --force "$worktree_path"
    git -C "$repo_root" worktree prune

    echo "removed worktree: $worktree_path"
}

main() {
    [ "$#" -gt 0 ] || {
        print_usage
        exit 1
    }

    local command="$1"
    shift

    case "$command" in
        provision)
            run_provision "$@"
            ;;
        setup-smoke-project)
            run_setup_smoke_project "$@"
            ;;
        setup)
            run_setup "$@"
            ;;
        seed)
            run_seed "$@"
            ;;
        start-daemon)
            run_start_daemon "$@"
            ;;
        teardown)
            run_teardown "$@"
            ;;
        -h|--help|help)
            print_usage
            ;;
        *)
            die "unknown command: $command"
            ;;
    esac
}

main "$@"
