#!/usr/bin/env bash
# aggregate-changelog.sh — Collect changelog.d/*.md fragments, bump CliVersion.cs,
# prepend the aggregated section to CHANGELOG.md, and delete the consumed fragments.
#
# Usage:
#   scripts/aggregate-changelog.sh [--dry-run] [--commit]
#
#   --dry-run   Show what would change; write nothing.
#   --commit    Stage and commit the result after aggregation.
#
# Fragment format (changelog.d/<name>.md):
#   ---
#   bump: patch   # patch | minor | major  (default: patch)
#   ---
#
#   ### Added
#   - Your changelog entry here.
#
# Exit codes:
#   0  Success (including "nothing to do")
#   1  Error (bad version file, parse failure, etc.)

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

DRY_RUN=false
DO_COMMIT=false

for arg in "$@"; do
  case "$arg" in
    --dry-run) DRY_RUN=true ;;
    --commit)  DO_COMMIT=true ;;
    *) echo "Unknown argument: $arg" >&2; exit 1 ;;
  esac
done

# ── 1. Collect fragment files ────────────────────────────────────────────────

fragments=()
while IFS= read -r -d '' f; do
  fragments+=("$f")
done < <(find changelog.d -maxdepth 1 -name "*.md" ! -name ".gitkeep" -print0 2>/dev/null | sort -z)

if [[ ${#fragments[@]} -eq 0 ]]; then
  echo "No changelog fragments found — nothing to do."
  exit 0
fi

echo "Found ${#fragments[@]} fragment(s): ${fragments[*]}"

# ── 2. Determine the highest bump level ──────────────────────────────────────

bump="patch"
for f in "${fragments[@]}"; do
  level="$(awk '
    BEGIN { in_fm=0; past_fm=0 }
    /^---$/ {
      if (!past_fm) {
        if (in_fm) { past_fm=1 } else { in_fm=1 }
        next
      }
    }
    in_fm && !past_fm && /^bump:/ { print $2; exit }
  ' "$f" | head -n1)"

  case "${level:-patch}" in
    major) bump="major" ;;
    minor) [[ "$bump" == "major" ]] || bump="minor" ;;
    patch) ;; # keep current level
    *)    echo "Warning: unknown bump level '$level' in $f — treating as patch." >&2 ;;
  esac
done

echo "Bump level: $bump"

# ── 3. Read current version from CliVersion.cs ───────────────────────────────

version_file="src/unifocl/Services/CliVersion.cs"
if [[ ! -f "$version_file" ]]; then
  echo "Error: $version_file not found." >&2
  exit 1
fi

major="$(sed -En 's/.*Major = ([0-9]+);/\1/p' "$version_file" | head -n1)"
minor="$(sed -En 's/.*Minor = ([0-9]+);/\1/p' "$version_file" | head -n1)"
patch="$(sed -En 's/.*Patch = ([0-9]+);/\1/p' "$version_file" | head -n1)"

if [[ -z "$major" || -z "$minor" || -z "$patch" ]]; then
  echo "Error: failed to parse version from $version_file." >&2
  exit 1
fi

old_version="${major}.${minor}.${patch}"

# ── 4. Compute new version ────────────────────────────────────────────────────

case "$bump" in
  major) major=$((major + 1)); minor=0; patch=0 ;;
  minor) minor=$((minor + 1)); patch=0 ;;
  patch) patch=$((patch + 1)) ;;
esac

new_version="${major}.${minor}.${patch}"
today="$(date -u +"%Y-%m-%d")"

echo "Version: $old_version → $new_version  ($today)"

if $DRY_RUN; then
  echo "[dry-run] Would update $version_file to $new_version"
fi

# ── 5. Build the new CHANGELOG section ────────────────────────────────────────

section_header="## ${new_version} - ${today}"
section_body=""

for f in "${fragments[@]}"; do
  body="$(awk '
    BEGIN { in_fm=0; past_fm=0 }
    /^---$/ {
      if (!past_fm) {
        if (in_fm) { past_fm=1 } else { in_fm=1 }
        next
      }
    }
    past_fm { print }
  ' "$f")"
  # Strip leading blank lines from each fragment body
  body="$(echo "$body" | sed '/./,$!d')"
  if [[ -n "$body" ]]; then
    section_body="${section_body}${body}"$'\n'
  fi
done

new_section="${section_header}"$'\n'"${section_body}"

if $DRY_RUN; then
  echo ""
  echo "── Would prepend to CHANGELOG.md ──────────────────────────────────────────"
  echo "$new_section"
  echo "────────────────────────────────────────────────────────────────────────────"
  echo "[dry-run] Would delete: ${fragments[*]}"
  exit 0
fi

# ── 6. Update CliVersion.cs ───────────────────────────────────────────────────

# Portable in-place substitution (works on macOS + Linux)
perl -i -pe "s/(Major\\s*=\\s*)\\d+/\${1}${major}/" "$version_file"
perl -i -pe "s/(Minor\\s*=\\s*)\\d+/\${1}${minor}/" "$version_file"
perl -i -pe "s/(Patch\\s*=\\s*)\\d+/\${1}${patch}/" "$version_file"
perl -i -pe 's/(DevCycle\s*=\s*")[^"]*"/$1"/' "$version_file"

echo "Updated $version_file"

# ── 7. Prepend section to CHANGELOG.md ────────────────────────────────────────

changelog_file="CHANGELOG.md"
if [[ ! -f "$changelog_file" ]]; then
  echo "# Changelog" > "$changelog_file"
  echo "" >> "$changelog_file"
fi

tmpfile="$(mktemp)"
section_file="$(mktemp)"
printf '%s\n' "$new_section" > "$section_file"

# Structure: header line, blank, new section, rest of file (skipping leading blank on line 2)
{
  head -n1 "$changelog_file"
  echo ""
  cat "$section_file"
  tail -n +2 "$changelog_file" | sed '/./,$!d'  # skip leading blank lines from existing content
} > "$tmpfile"

rm -f "$section_file"
mv "$tmpfile" "$changelog_file"

echo "Updated $changelog_file"

# ── 8. Delete consumed fragments ─────────────────────────────────────────────

for f in "${fragments[@]}"; do
  rm -f "$f"
  echo "Deleted $f"
done

# ── 9. Optionally commit ──────────────────────────────────────────────────────

if $DO_COMMIT; then
  git config --local user.name  "github-actions[bot]"
  git config --local user.email "github-actions[bot]@users.noreply.github.com"

  git add "$version_file" "$changelog_file" changelog.d/
  git commit -m "chore: bump version to ${new_version} and aggregate changelog"
  echo "Committed version bump to ${new_version}"
fi

echo "Done. New version: ${new_version}"
