#!/usr/bin/env bash
set -euo pipefail

print_usage() {
    cat <<'USAGE'
Usage:
  setup-mcp-agents.sh [options]

Options:
  --workspace <path>            Workspace root (default: current directory).
  --server-name <name>          MCP server key (default: unifocl).
  --config-root <path>          UNIFOCL_CONFIG_ROOT (default: <workspace>/.local/unifocl-config).
  --codex                       Configure Codex via `codex mcp add`.
  --cursor-config <path>        Merge MCP server into Cursor JSON config file.
  --claude-config <path>        Merge MCP server into Claude Code JSON config file.
  --json-config <path>          Merge MCP server into any JSON config file.
  --dry-run                     Print planned changes without writing.
  --help                        Show this help.

Notes:
  - You can pass multiple targets in one run (for example: --codex + --cursor-config).
  - JSON targets are updated idempotently at mcpServers.<server-name>.
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

workspace="."
server_name="unifocl"
config_root=""
use_codex=false
dry_run=false
declare -a json_targets=()

while [ "$#" -gt 0 ]; do
    case "$1" in
        --workspace)
            workspace="$2"
            shift 2
            ;;
        --server-name)
            server_name="$2"
            shift 2
            ;;
        --config-root)
            config_root="$2"
            shift 2
            ;;
        --codex)
            use_codex=true
            shift
            ;;
        --cursor-config|--claude-config|--json-config)
            json_targets+=("$2")
            shift 2
            ;;
        --dry-run)
            dry_run=true
            shift
            ;;
        --help|-h)
            print_usage
            exit 0
            ;;
        *)
            die "unknown option: $1"
            ;;
    esac
done

if [ "$use_codex" != true ] && [ "${#json_targets[@]}" -eq 0 ]; then
    die "no targets selected; pass at least one of --codex, --cursor-config, --claude-config, --json-config"
fi

workspace="$(abs_path "$workspace")"
[ -d "$workspace" ] || die "workspace path does not exist: $workspace"

if [ -z "$config_root" ]; then
    config_root="$workspace/.local/unifocl-config"
fi
config_root="$(abs_path "$config_root")"

mkdir -p "$config_root"

launch_command=""
declare -a launch_args=()
if command -v unifocl >/dev/null 2>&1; then
    launch_command="unifocl"
    launch_args=(--mcp-server)
else
    require_command dotnet
    local_csproj="$workspace/src/unifocl/unifocl.csproj"
    [ -f "$local_csproj" ] || die "could not find csproj for dotnet launch: $local_csproj"
    launch_command="dotnet"
    launch_args=(
        run
        --project
        "$local_csproj"
        --disable-build-servers
        -v
        minimal
        --
        --mcp-server
    )
fi

apply_json_target() {
    local target_path="$1"
    local target_abs
    target_abs="$(abs_path "$target_path")"
    mkdir -p "$(dirname "$target_abs")"

    if [ "$dry_run" = true ]; then
        echo "[dry-run] would update JSON MCP config: $target_abs"
        return 0
    fi

    if [ -f "$target_abs" ]; then
        cp "$target_abs" "$target_abs.bak"
        echo "backup: $target_abs.bak"
    fi

    python3 - "$target_abs" "$server_name" "$workspace" "$config_root" "$launch_command" "${launch_args[@]}" <<'PY'
import json
import os
import sys

target = sys.argv[1]
server_name = sys.argv[2]
workspace = sys.argv[3]
config_root = sys.argv[4]
command = sys.argv[5]
args = sys.argv[6:]

root = {}
if os.path.exists(target):
    with open(target, "r", encoding="utf-8") as f:
        raw = f.read().strip()
        if raw:
            parsed = json.loads(raw)
            if not isinstance(parsed, dict):
                raise SystemExit(f"existing JSON root must be an object: {target}")
            root = parsed

mcp_servers = root.get("mcpServers")
if mcp_servers is None:
    mcp_servers = {}
    root["mcpServers"] = mcp_servers
if not isinstance(mcp_servers, dict):
    raise SystemExit(f"mcpServers must be an object: {target}")

mcp_servers[server_name] = {
    "command": command,
    "args": args,
    "cwd": workspace,
    "env": {
        "UNIFOCL_CONFIG_ROOT": config_root
    }
}

with open(target, "w", encoding="utf-8") as f:
    json.dump(root, f, indent=2, ensure_ascii=True)
    f.write("\n")
PY

    echo "configured: $target_abs"
}

if [ "$use_codex" = true ]; then
    require_command codex
    if [ "$dry_run" = true ]; then
        echo "[dry-run] would configure Codex MCP server '$server_name'"
        printf '[dry-run] command: %s' "$launch_command"
        for arg in "${launch_args[@]}"; do
            printf ' %q' "$arg"
        done
        printf '\n'
    else
        codex mcp remove "$server_name" >/dev/null 2>&1 || true
        codex mcp add "$server_name" --env "UNIFOCL_CONFIG_ROOT=$config_root" -- \
            "$launch_command" "${launch_args[@]}"
        echo "configured: Codex MCP '$server_name'"
    fi
fi

for target in "${json_targets[@]}"; do
    apply_json_target "$target"
done

echo "done."
