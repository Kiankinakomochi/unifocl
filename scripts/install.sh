#!/bin/sh
# unifocl installer for macOS (Apple Silicon)
# Usage:
#   curl -fsSL https://raw.githubusercontent.com/Kiankinakomochi/unifocl/main/scripts/install.sh | sh
#   # or with a specific version:
#   VERSION=2.3.0 curl -fsSL ... | sh
#   # strict attestation verification (requires gh CLI):
#   UNIFOCL_REQUIRE_ATTESTATION=1 curl -fsSL ... | sh

set -e

REPO="Kiankinakomochi/unifocl"
INSTALL_DIR="/usr/local/bin"

# ── Architecture check ──────────────────────────────────────────────────────
ARCH=$(uname -m)
case "$ARCH" in
  arm64) PLATFORM="macos-arm64" ;;
  x86_64)
    echo "Intel (x86_64) Macs are not currently supported by the direct installer."
    echo "Please use Homebrew instead (works on Intel via Rosetta 2):"
    echo "  brew tap Kiankinakomochi/unifocl && brew install unifocl"
    exit 1
    ;;
  *)
    echo "Unsupported architecture: $ARCH"
    exit 1
    ;;
esac

# ── Resolve version ──────────────────────────────────────────────────────────
if [ -z "${VERSION:-}" ]; then
  echo "Fetching latest release version..."
  VERSION=$(curl -fsSL "https://api.github.com/repos/${REPO}/releases/latest" \
    | grep '"tag_name"' \
    | sed 's/.*"v\([^"]*\)".*/\1/')
  if [ -z "$VERSION" ]; then
    echo "Failed to resolve latest version. Set VERSION= to install a specific version."
    exit 1
  fi
fi

URL="https://github.com/${REPO}/releases/download/v${VERSION}/unifocl-${VERSION}-${PLATFORM}.tar.gz"
CHECKSUM_URL="https://github.com/${REPO}/releases/download/v${VERSION}/unifocl-${VERSION}-checksums.txt"
TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT

# ── Download ─────────────────────────────────────────────────────────────────
echo "Downloading unifocl ${VERSION} (${PLATFORM})..."
curl -fsSL --progress-bar "$URL" -o "$TMP/unifocl.tar.gz"
curl -fsSL --progress-bar "$CHECKSUM_URL" -o "$TMP/checksums.txt"

# ── Verify checksum (required) ──────────────────────────────────────────────
EXPECTED_SHA=$(awk '/unifocl-'"${VERSION}"'-'"${PLATFORM}"'\.tar\.gz$/ {print $1}' "$TMP/checksums.txt" | head -n1)
if [ -z "$EXPECTED_SHA" ]; then
  echo "Failed to find checksum entry for unifocl-${VERSION}-${PLATFORM}.tar.gz"
  exit 1
fi

if command -v sha256sum >/dev/null 2>&1; then
  ACTUAL_SHA=$(sha256sum "$TMP/unifocl.tar.gz" | awk '{print $1}')
elif command -v shasum >/dev/null 2>&1; then
  ACTUAL_SHA=$(shasum -a 256 "$TMP/unifocl.tar.gz" | awk '{print $1}')
else
  echo "No SHA256 tool found (need sha256sum or shasum)."
  exit 1
fi

if [ "$ACTUAL_SHA" != "$EXPECTED_SHA" ]; then
  echo "Checksum verification failed."
  echo "Expected: $EXPECTED_SHA"
  echo "Actual:   $ACTUAL_SHA"
  exit 1
fi
echo "Checksum verified."

# ── Verify attestation (optional, strict via env) ──────────────────────────
STRICT_ATTEST="${UNIFOCL_REQUIRE_ATTESTATION:-0}"
if command -v gh >/dev/null 2>&1; then
  if gh attestation verify "$TMP/unifocl.tar.gz" --repo "$REPO" >/dev/null 2>&1; then
    echo "Attestation verified."
  else
    if [ "$STRICT_ATTEST" = "1" ]; then
      echo "Attestation verification failed (strict mode)."
      exit 1
    fi
    echo "Warning: attestation verification failed; continuing (non-strict mode)."
  fi
else
  if [ "$STRICT_ATTEST" = "1" ]; then
    echo "gh CLI is required when UNIFOCL_REQUIRE_ATTESTATION=1."
    exit 1
  fi
  echo "Warning: gh CLI not found; skipping attestation verification."
fi

# ── Extract ──────────────────────────────────────────────────────────────────
tar -xzf "$TMP/unifocl.tar.gz" -C "$TMP"

if [ ! -f "$TMP/unifocl" ]; then
  echo "Extraction failed: unifocl binary not found in archive."
  exit 1
fi

# ── Install ──────────────────────────────────────────────────────────────────
echo "Installing to ${INSTALL_DIR}/unifocl..."
if [ -w "$INSTALL_DIR" ]; then
  install -m 755 "$TMP/unifocl" "$INSTALL_DIR/unifocl"
else
  sudo install -m 755 "$TMP/unifocl" "$INSTALL_DIR/unifocl"
fi

# ── Done ─────────────────────────────────────────────────────────────────────
echo ""
echo "unifocl ${VERSION} installed successfully."
echo "Run 'unifocl --version' to verify."
