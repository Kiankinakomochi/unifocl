#!/usr/bin/env bash
# build-full-docs.sh — Regenerate full-documentation.md from README + docs/*.md
# Usage: scripts/build-full-docs.sh [--check]
#   --check  Exit 1 if the output would change (CI dry-run mode)
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUTPUT="$REPO_ROOT/full-documentation.md"
TMPFILE="$(mktemp)"

trap 'rm -f "$TMPFILE"' EXIT

{
  cat "$REPO_ROOT/README.md"
  for f in "$REPO_ROOT"/docs/*.md; do
    [ "$(basename "$f")" = "full-documentation.md" ] && continue
    printf '\n---\n\n'
    cat "$f"
  done
} > "$TMPFILE"

if [ "${1:-}" = "--check" ]; then
  if ! diff -q "$TMPFILE" "$OUTPUT" >/dev/null 2>&1; then
    echo "full-documentation.md is out of date. Run: scripts/build-full-docs.sh" >&2
    exit 1
  fi
  echo "full-documentation.md is up to date."
  exit 0
fi

cp "$TMPFILE" "$OUTPUT"
echo "Updated $OUTPUT ($(wc -l < "$OUTPUT") lines)"
