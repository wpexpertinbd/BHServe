#!/bin/bash
# Build distributables: BHServe.app → BHServe-<ver>.dmg (drag install) + .pkg (installer).
# Ad-hoc signed (local). Run: ./make-dist.sh
set -euo pipefail
cd "$(dirname "$0")"

APP_NAME="BHServe"
VERSION="1.6.13"          # keep in sync with build-app.sh
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

# ── PKG (installs to /Applications) — branded installer via productbuild ─────
echo "▶ PKG..."
PKGROOT="$DIST/.pkg-root"
rm -rf "$PKGROOT"; mkdir -p "$PKGROOT/Applications"
cp -R "$APP" "$PKGROOT/Applications/"

PKGTMP="$DIST/.pkg-build"
rm -rf "$PKGTMP"; mkdir -p "$PKGTMP/res"
# component package (the actual payload)
pkgbuild --root "$PKGROOT" --install-location / \
  --identifier "$IDENT" --version "$VERSION" --ownership recommended \
  "$PKGTMP/component.pkg" >/dev/null

# branded Welcome + Conclusion screens (BiswasHost — brand blue #0d6efd)
cat > "$PKGTMP/res/welcome.html" <<HTML
<!DOCTYPE html><html><head><meta charset="utf-8">
<style>
  body{font-family:-apple-system,Helvetica,sans-serif;color:#1d1d1f;margin:0;padding:18px 20px;line-height:1.5}
  .brand{font-size:13px;font-weight:700;letter-spacing:.5px;color:#0d6efd;text-transform:uppercase}
  h1{font-size:22px;margin:2px 0 2px}
  .tag{color:#6e6e73;font-size:13px;margin:0 0 14px}
  ul{margin:8px 0 14px;padding-left:18px} li{margin:3px 0;font-size:13px}
  .free{background:#eef5ff;border:1px solid #cfe0ff;border-radius:8px;padding:9px 11px;font-size:12.5px;color:#0948b3}
  b{color:#0d6efd}
</style></head><body>
  <div class="brand">BiswasHost</div>
  <h1>BHServe $VERSION</h1>
  <p class="tag">Your own free local web-server for macOS — an alternative to ServBay / Herd.</p>
  <p style="font-size:13px;margin:0 0 6px">This installer will place <b>BHServe</b> in your Applications folder. It includes:</p>
  <ul>
    <li>Multiple <b>PHP</b> versions (7.4, 8.1–8.6) — per site</li>
    <li><b>nginx</b> &amp; <b>Apache</b> (.htaccess), <b>MariaDB / MySQL / PostgreSQL</b></li>
    <li><b>Redis</b> &amp; <b>Memcached</b>, <b>Node.js</b> (multiple versions)</li>
    <li><b>phpMyAdmin · Adminer · Mailpit</b>, trusted <b>HTTPS</b> + <code>*.test</code> domains</li>
    <li>One-click <b>WordPress / PHP</b> sites with auto database</li>
  </ul>
  <div class="free">100% free &amp; open-source — built with ❤️ by <b>BiswasHost</b> (Benjamin Biswas).</div>
</body></html>
HTML

cat > "$PKGTMP/res/conclusion.html" <<HTML
<!DOCTYPE html><html><head><meta charset="utf-8">
<style>
  body{font-family:-apple-system,Helvetica,sans-serif;color:#1d1d1f;margin:0;padding:18px 20px;line-height:1.5}
  .brand{font-size:13px;font-weight:700;letter-spacing:.5px;color:#0d6efd;text-transform:uppercase}
  h1{font-size:20px;margin:2px 0 8px}
  p{font-size:13px;margin:0 0 9px} b{color:#0d6efd}
  .note{background:#fff8e6;border:1px solid #ffe2a8;border-radius:8px;padding:9px 11px;font-size:12.5px;color:#7a5b00}
</style></head><body>
  <div class="brand">BiswasHost</div>
  <h1>BHServe is installed 🎉</h1>
  <p>Find <b>BHServe</b> in your Applications folder or Launchpad. It lives in the menu bar — open it to add sites, start services, and manage databases.</p>
  <p class="note"><b>First launch:</b> macOS may say the app is from an “unidentified developer” (it isn't notarized — that needs a paid Apple account). Right-click the app → <b>Open</b>, or go to <b>System Settings ▸ Privacy &amp; Security ▸ Open Anyway</b>. You only do this once.</p>
  <p>Free &amp; open-source: <b>github.com/wpexpertinbd/BHServe</b> — thank you for using BHServe!</p>
</body></html>
HTML

cat > "$PKGTMP/distribution.xml" <<XML
<?xml version="1.0" encoding="utf-8"?>
<installer-gui-script minSpecVersion="1">
  <title>BHServe</title>
  <welcome file="welcome.html" mime-type="text/html"/>
  <conclusion file="conclusion.html" mime-type="text/html"/>
  <options customize="never" require-scripts="false" hostArchitectures="arm64,x86_64"/>
  <choices-outline><line choice="default"/></choices-outline>
  <choice id="default"><pkg-ref id="$IDENT"/></choice>
  <pkg-ref id="$IDENT" version="$VERSION">component.pkg</pkg-ref>
</installer-gui-script>
XML

PKG="$DIST/$APP_NAME-$VERSION.pkg"
rm -f "$PKG"
productbuild --distribution "$PKGTMP/distribution.xml" \
  --resources "$PKGTMP/res" --package-path "$PKGTMP" "$PKG" >/dev/null
rm -rf "$PKGROOT" "$PKGTMP"
echo "✓ $PKG ($(du -h "$PKG" | cut -f1))  (branded installer)"

# Drop the build artifact so it doesn't show up as a second BHServe.app in
# Spotlight/Launchpad — the .dmg and .pkg already contain it.
rm -rf "$APP"

echo
echo "Distributables in $PWD/$DIST:"
echo "  • $APP_NAME-$VERSION.dmg   — open, drag BHServe to Applications"
echo "  • $APP_NAME-$VERSION.pkg   — double-click to install to /Applications"
echo "Note: ad-hoc signed (no Developer ID) — on another Mac, right-click → Open"
echo "      (or System Settings ▸ Privacy & Security ▸ Open Anyway) the first time."
