#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SUBMODULE_DIR="$ROOT_DIR/external/unifocl-protobuf"
PROJECT_FILE="$SUBMODULE_DIR/src/Unifocl.Shared/Unifocl.Shared.csproj"
OUTPUT_DLL="$SUBMODULE_DIR/src/Unifocl.Shared/bin/Debug/netstandard2.1/Unifocl.Shared.dll"
TARGET_DIR="$ROOT_DIR/src/unifocl.unity/EditorScripts/Plugins"
TARGET_DLL="$TARGET_DIR/Unifocl.Shared.dll"

if [[ ! -f "$PROJECT_FILE" ]]; then
  echo "[x] Missing protobuf project: $PROJECT_FILE"
  echo "[i] Run: git submodule update --init --recursive"
  exit 1
fi

echo "[*] Building protobuf shared project..."
dotnet build "$PROJECT_FILE" --disable-build-servers -v minimal

if [[ ! -f "$OUTPUT_DLL" ]]; then
  echo "[x] Build succeeded but DLL not found: $OUTPUT_DLL"
  exit 1
fi

mkdir -p "$TARGET_DIR"
cp "$OUTPUT_DLL" "$TARGET_DLL"

echo "[+] Synced protobuf plugin -> $TARGET_DLL"
