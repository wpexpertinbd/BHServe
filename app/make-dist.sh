#!/bin/bash
# Build distributables: BHServe.app → BHServe-<ver>.dmg (drag install) + .pkg (installer).
# Ad-hoc signed (local). Run: ./make-dist.sh
set -euo pipefail
cd "$(dirname "$0")"

APP_NAME="BHServe"
VERSION="1.0.9"          # keep in sync with build-app.sh
IDENT="com.biswashost.bhserve"
DIST="dist"
APP="$DIST/$APP_NAME.app"

echo "▶ building app..."
./build-app.sh >/dev/null
echo "✓ $APP"

# ── DMG (drag BHServe.app → Applications) ────────────────────────────────────
echo "▶ DMG..."
STAGE="$DIST/.dmg-stage"
rm -rf "$STAGE"; mkdir -p "$STAGE"
cp -R "$APP" "$STAGE/"
ln -s /Applications "$STAGE/Applications"
DMG="$DIST/$APP_NAME-$VERSION.dmg"
rm -f "$DMG"
hdiutil create -volname "$APP_NAME" -srcfolder "$STAGE" -ov -format UDZO "$DMG" >/dev/null
rm -rf "$STAGE"
echo "✓ $DMG ($(du -h "$DMG" | cut -f1))"

# ── PKG (installs to /Applications) ──────────────────────────────────────────
echo "▶ PKG..."
PKGROOT="$DIST/.pkg-root"
rm -rf "$PKGROOT"; mkdir -p "$PKGROOT/Applications"
cp -R "$APP" "$PKGROOT/Applications/"
PKG="$DIST/$APP_NAME-$VERSION.pkg"
rm -f "$PKG"
pkgbuild --root "$PKGROOT" --install-location / \
  --identifier "$IDENT" --version "$VERSION" --ownership recommended "$PKG" >/dev/null
rm -rf "$PKGROOT"
echo "✓ $PKG ($(du -h "$PKG" | cut -f1))"

echo
echo "Distributables in $PWD/$DIST:"
echo "  • $APP_NAME-$VERSION.dmg   — open, drag BHServe to Applications"
echo "  • $APP_NAME-$VERSION.pkg   — double-click to install to /Applications"
echo "Note: ad-hoc signed (no Developer ID) — on another Mac, right-click → Open"
echo "      (or System Settings ▸ Privacy & Security ▸ Open Anyway) the first time."
