#!/bin/sh
# unifocl installer for macOS (Apple Silicon)
# Usage:
#   curl -fsSL https://raw.githubusercontent.com/Kiankinakomochi/unifocl/main/scripts/install.sh | sh
#   # or with a specific version:
#   VERSION=2.3.0 curl -fsSL ... | sh

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
TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT

# ── Download ─────────────────────────────────────────────────────────────────
echo "Downloading unifocl ${VERSION} (${PLATFORM})..."
curl -fsSL --progress-bar "$URL" -o "$TMP/unifocl.tar.gz"

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
