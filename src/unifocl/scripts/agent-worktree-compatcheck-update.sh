#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
benchmark_project_path="${BENCHMARK_UNITY_PROJECT_PATH:-${repo_root}/.local/compatcheck-benchmark}"

print_usage() {
    cat <<'USAGE'
Usage:
  agent-worktree-compatcheck-update.sh [--run-compatcheck]

Behavior:
  - Resolves Unity editor path (UNITY_EDITOR_PATH or latest under /Applications/Unity/Hub/Editor/*/Unity.app)
  - Verifies UnityEditorManagedDir exists
  - Prints export hints for UNIFOCL_UNITY_EDITOR_MANAGED_DIR / UNIFOCL_UNITY_PROJECT_PATH
  - Optionally runs compatcheck with the resolved path(s)
USAGE
}

resolve_unity_editor_app() {
    if [[ -n "${UNITY_EDITOR_PATH:-}" ]]; then
        echo "${UNITY_EDITOR_PATH}"
        return 0
    fi

    local latest
    latest="$(
        ls -d /Applications/Unity/Hub/Editor/*/Unity.app 2>/dev/null \
            | sort -V \
            | tail -n 1 || true
    )"
    [[ -n "${latest}" ]] || return 1
    echo "${latest}"
}

run_compatcheck=false
while [[ $# -gt 0 ]]; do
    case "$1" in
        --run-compatcheck)
            run_compatcheck=true
            shift
            ;;
        -h|--help|help)
            print_usage
            exit 0
            ;;
        *)
            echo "error: unknown option: $1" >&2
            print_usage
            exit 1
            ;;
    esac
done

unity_editor_app="$(resolve_unity_editor_app || true)"
if [[ -z "${unity_editor_app}" ]]; then
    echo "error: Unity editor was not found. Set UNITY_EDITOR_PATH to .../Unity.app and retry." >&2
    exit 1
fi

unity_editor_managed_dir="${unity_editor_app}/Contents/Managed"
if [[ ! -f "${unity_editor_managed_dir}/UnityEditor.dll" ]]; then
    echo "error: UnityEditor.dll not found under: ${unity_editor_managed_dir}" >&2
    exit 1
fi

echo "[=] Unity editor app: ${unity_editor_app}"
echo "[=] UnityEditorManagedDir: ${unity_editor_managed_dir}"
echo "export UNIFOCL_UNITY_EDITOR_MANAGED_DIR=\"${unity_editor_managed_dir}\""

project_arg=()
if [[ -d "${benchmark_project_path}" ]]; then
    echo "[=] Unity project path: ${benchmark_project_path}"
    echo "export UNIFOCL_UNITY_PROJECT_PATH=\"${benchmark_project_path}\""
    project_arg=(-p:UnityProjectPath="${benchmark_project_path}")
else
    echo "[!] Unity project path not found (optional): ${benchmark_project_path}"
fi

if [[ "${run_compatcheck}" == true ]]; then
    dotnet build "${repo_root}/src/unifocl.unity.compatcheck/unifocl.unity.compatcheck.csproj" \
        --disable-build-servers \
        -v minimal \
        -p:UnityEditorManagedDir="${unity_editor_managed_dir}" \
        "${project_arg[@]:-}"
fi
