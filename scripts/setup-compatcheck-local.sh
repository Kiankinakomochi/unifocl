#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
local_config_path="${repo_root}/local.config.json"
benchmark_project_path="${BENCHMARK_UNITY_PROJECT_PATH:-${repo_root}/.local/compatcheck-benchmark}"

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
  if [[ -z "${latest}" ]]; then
    return 1
  fi

  echo "${latest}"
}

unity_editor_app="$(resolve_unity_editor_app || true)"
if [[ -z "${unity_editor_app}" ]]; then
  echo "[x] Unity editor was not found. Set UNITY_EDITOR_PATH to .../Unity.app and retry."
  exit 1
fi

if [[ -d "${unity_editor_app}/Contents/Resources/Scripting/Managed" ]]; then
  # Unity 6+ moved the managed assemblies under Resources/Scripting/Managed
  unity_editor_managed_dir="${unity_editor_app}/Contents/Resources/Scripting/Managed"
else
  unity_editor_managed_dir="${unity_editor_app}/Contents/Managed"
fi
unity_editor_executable="${unity_editor_app}/Contents/MacOS/Unity"
unity_version="$(basename "$(dirname "${unity_editor_app}")")"
unity_script_assemblies_dir="${benchmark_project_path}/Library/ScriptAssemblies"

mkdir -p "${benchmark_project_path}/Assets" \
         "${benchmark_project_path}/Packages" \
         "${benchmark_project_path}/ProjectSettings" \
         "${repo_root}/.local"

cat > "${benchmark_project_path}/Packages/manifest.json" <<'EOF'
{
  "dependencies": {
    "com.unity.collab-proxy": "2.7.2",
    "com.unity.ide.rider": "3.0.35",
    "com.unity.ide.visualstudio": "2.0.24",
    "com.unity.test-framework": "1.4.5",
    "com.unity.textmeshpro": "3.0.6",
    "com.unity.timeline": "1.8.9",
    "com.unity.ugui": "1.0.0"
  }
}
EOF

cat > "${benchmark_project_path}/ProjectSettings/ProjectVersion.txt" <<EOF
m_EditorVersion: ${unity_version}
m_EditorVersionWithRevision: ${unity_version} (local-compatcheck-bootstrap)
EOF

echo "[*] Bootstrapping Unity benchmark project: ${benchmark_project_path}"
"${unity_editor_executable}" \
  -batchmode \
  -nographics \
  -quit \
  -projectPath "${benchmark_project_path}" \
  -logFile "${benchmark_project_path}/unity-bootstrap.log" || true

cat > "${local_config_path}" <<EOF
{
  "unityEditorPath": "${unity_editor_app}",
  "unityEditorManagedDir": "${unity_editor_managed_dir}",
  "unityProjectPath": "${benchmark_project_path}",
  "unityScriptAssembliesDir": "${unity_script_assemblies_dir}",
  "compatcheckCommand": "dotnet build src/unifocl.unity.compatcheck/unifocl.unity.compatcheck.csproj --disable-build-servers -v minimal -p:UnityEditorManagedDir=\\"${unity_editor_managed_dir}\\" -p:UnityProjectPath=\\"${benchmark_project_path}\\""
}
EOF

echo "[=] Wrote local config: ${local_config_path}"
if [[ -d "${unity_script_assemblies_dir}" ]]; then
  echo "[=] Unity script assemblies detected: ${unity_script_assemblies_dir}"
else
  echo "[!] Unity script assemblies are missing: ${unity_script_assemblies_dir}"
  echo "[!] Open the project once in Unity to finish package compilation, then run compatcheck."
fi

echo "[*] Running compatcheck with generated paths"
dotnet build "${repo_root}/src/unifocl.unity.compatcheck/unifocl.unity.compatcheck.csproj" \
  --disable-build-servers \
  -v minimal \
  -p:UnityEditorManagedDir="${unity_editor_managed_dir}" \
  -p:UnityProjectPath="${benchmark_project_path}"
