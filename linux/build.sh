#!/bin/bash
# BHServe for Linux — build the .deb (and, with appimagetool present, the .AppImage).
# Run on Ubuntu/Debian (or WSL2):  ./linux/build.sh [version]
# Produces: linux/dist/bhserve_<version>_all.deb
set -euo pipefail

VERSION="${1:-}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
if [ -z "$VERSION" ]; then
  VERSION="$(grep -oE '__version__ *= *"[^"]+"' "$ROOT/linux/app/bhserve/__init__.py" | grep -oE '[0-9]+\.[0-9]+\.[0-9]+' | head -1)"
  VERSION="${VERSION:-1.0.0}"
fi
echo "▶ Building BHServe for Linux $VERSION"

DIST="$ROOT/linux/dist"; mkdir -p "$DIST"
STAGE="$(mktemp -d)"; trap 'rm -rf "$STAGE"' EXIT
PKG="$STAGE/bhserve"

mkdir -p "$PKG/DEBIAN" \
         "$PKG/usr/lib/bhserve/engine" \
         "$PKG/usr/lib/bhserve/app" \
         "$PKG/usr/bin" \
         "$PKG/usr/share/applications" \
         "$PKG/usr/share/icons/hicolor/256x256/apps"

# ── engine (strip CR in case it was checked out on Windows) ──
for f in bhserve platform-linux.sh; do
  tr -d '\r' < "$ROOT/engine/$f" > "$PKG/usr/lib/bhserve/engine/$f"
done
chmod 0755 "$PKG/usr/lib/bhserve/engine/bhserve"

# ── GTK app (python package + launcher), CR-stripped ──
mkdir -p "$PKG/usr/lib/bhserve/app/bhserve" "$PKG/usr/lib/bhserve/app/bin"
for f in "$ROOT"/linux/app/bhserve/*.py "$ROOT"/linux/app/bhserve/*.css; do
  tr -d '\r' < "$f" > "$PKG/usr/lib/bhserve/app/bhserve/$(basename "$f")"
done
tr -d '\r' < "$ROOT/linux/app/bin/bhserve-gui" > "$PKG/usr/lib/bhserve/app/bin/bhserve-gui"
chmod 0755 "$PKG/usr/lib/bhserve/app/bin/bhserve-gui"

# ── CLI + GUI on PATH (symlinks; engine resolves its real dir via readlink -f) ──
ln -sf /usr/lib/bhserve/engine/bhserve   "$PKG/usr/bin/bhserve"
ln -sf /usr/lib/bhserve/app/bin/bhserve-gui "$PKG/usr/bin/bhserve-gui"

# ── icon: largest frame of the shared AppIcon.ico → 256px png ──
ICON_OUT="$PKG/usr/share/icons/hicolor/256x256/apps/bhserve.png"
if [ -f "$ROOT/linux/packaging/bhserve.png" ]; then
  cp "$ROOT/linux/packaging/bhserve.png" "$ICON_OUT"
elif command -v convert >/dev/null && [ -f "$ROOT/app/icon/AppIcon.ico" ]; then
  tmpd="$(mktemp -d)"; convert "$ROOT/app/icon/AppIcon.ico" "$tmpd/f-%02d.png" 2>/dev/null || true
  best=""; barea=0
  for f in "$tmpd"/f-*.png; do
    [ -f "$f" ] || continue
    wh="$(identify -format '%w %h' "$f" 2>/dev/null)"; a=$(( ${wh% *} * ${wh#* } ))
    [ "$a" -gt "$barea" ] && { barea="$a"; best="$f"; }
  done
  [ -n "$best" ] && convert "$best" -resize 256x256 "$ICON_OUT" || true
  rm -rf "$tmpd"
fi
[ -f "$ICON_OUT" ] || echo "  ! no icon (install imagemagick or add linux/packaging/bhserve.png)"

# ── desktop entry ──
cat > "$PKG/usr/share/applications/bhserve.desktop" <<'DESKTOP'
[Desktop Entry]
Type=Application
Name=BHServe
GenericName=Local Web Server
Comment=Free local web server: nginx/PHP/MariaDB, multi-PHP, *.test HTTPS, WordPress
Exec=bhserve-gui
Icon=bhserve
Terminal=false
Categories=Development;WebDevelopment;
Keywords=php;nginx;apache;mariadb;mysql;postgresql;wordpress;localhost;server;laravel;
StartupNotify=true
DESKTOP

# ── control + maintainer scripts ──
cat > "$PKG/DEBIAN/control" <<EOF
Package: bhserve
Version: $VERSION
Section: web
Priority: optional
Architecture: all
Depends: bash, python3, python3-gi, gir1.2-gtk-4.0, gir1.2-adw-1, curl, libglib2.0-bin
Recommends: libnss3-tools, policykit-1, software-properties-common
Maintainer: BiswasHost <support@biswashost.com>
Homepage: https://www.biswashost.com
Description: Free local web server (nginx/PHP/MariaDB) with a GTK control panel
 BHServe is a self-controlled local development server for Ubuntu/Debian — a clean
 alternative to XAMPP. It runs nginx/Apache, multiple PHP versions side by side,
 MariaDB / MySQL / PostgreSQL, Redis, and managed Node + Python apps, with trusted
 *.test HTTPS (mkcert) and one-click WordPress / PHP sites.
 .
 The servers themselves are installed on demand via apt (Ondrej PHP repo for PHP);
 this package provides the engine and the GTK4/libadwaita control panel.
EOF

cat > "$PKG/DEBIAN/postinst" <<'POST'
#!/bin/bash
set -e
update-desktop-database -q 2>/dev/null || true
gtk-update-icon-cache -q -f /usr/share/icons/hicolor 2>/dev/null || true
exit 0
POST
chmod 0755 "$PKG/DEBIAN/postinst"

# ── build ──
OUT="$DIST/bhserve_${VERSION}_all.deb"
dpkg-deb --root-owner-group --build "$PKG" "$OUT" >/dev/null
echo "✓ $OUT"
ls -la "$OUT"
