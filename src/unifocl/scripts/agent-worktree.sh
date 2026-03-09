#!/usr/bin/env bash
set -euo pipefail

print_usage() {
    cat <<'USAGE'
Usage:
  agent-worktree.sh provision --repo-root <path> --worktree-path <path> --branch <branch> [--source-project <path>] [--seed-library]
  agent-worktree.sh seed --source-project <path> --worktree-path <path>
  agent-worktree.sh start-daemon --worktree-path <path> --project-path <path> [--port-start <n>] [--port-end <n>]
  agent-worktree.sh teardown --repo-root <path> --worktree-path <path>

Commands:
  provision     Create isolated git worktree branch and optionally seed Library cache.
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
    else
        local base
        base="$(dirname "$value")"
        local name
        name="$(basename "$value")"
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
