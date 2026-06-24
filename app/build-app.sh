#!/bin/bash
# Build BHServe.app — a self-contained, double-clickable macOS bundle with the
# engine inside. Ad-hoc signed (local use). Run: ./build-app.sh
set -euo pipefail
cd "$(dirname "$0")"

APP_NAME="BHServe"
VERSION="1.2.2"
DIST="dist"
APP="$DIST/$APP_NAME.app"

echo "▶ building release binary..."
swift build -c release
BIN="$(swift build -c release --show-bin-path)/$APP_NAME"
[ -x "$BIN" ] || { echo "✗ release binary not found"; exit 1; }

echo "▶ assembling $APP..."
# Keep Spotlight/LaunchServices from indexing the dev build as a 2nd "BHServe" app
# (the installed copy in /Applications is the one users should see).
mkdir -p "$DIST"; : > "$DIST/.metadata_never_index"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN" "$APP/Contents/MacOS/$APP_NAME"
# bundle the engine so the app does not depend on the dev checkout path
cp ../engine/bhserve "$APP/Contents/Resources/bhserve"
chmod +x "$APP/Contents/Resources/bhserve"
# app icon (regenerate with icon/make-icon.sh if missing)
if [ -f icon/AppIcon.icns ]; then
  cp icon/AppIcon.icns "$APP/Contents/Resources/AppIcon.icns"
else
  echo "  (no icon/AppIcon.icns — run icon/make-icon.sh; building without icon)"
fi

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>$APP_NAME</string>
  <key>CFBundleDisplayName</key><string>$APP_NAME</string>
  <key>CFBundleIdentifier</key><string>com.biswashost.bhserve</string>
  <key>CFBundleExecutable</key><string>$APP_NAME</string>
  <key>CFBundleIconFile</key><string>AppIcon</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$VERSION</string>
  <key>CFBundleVersion</key><string>$VERSION</string>
  <key>LSMinimumSystemVersion</key><string>14.0</string>
  <key>NSPrincipalClass</key><string>NSApplication</string>
  <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
PLIST

# Sign LAST — any edit after signing invalidates it ("damaged" on Apple Silicon).
echo "▶ ad-hoc codesign..."
codesign --force --deep --sign - "$APP"
codesign --verify --deep "$APP" && echo "✓ signature valid"

echo "✓ built $APP"
echo "  run:  open \"$PWD/$APP\""
echo "  install:  cp -R \"$APP\" /Applications/"
