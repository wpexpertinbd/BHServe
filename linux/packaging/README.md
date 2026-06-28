# linux/packaging — distributables

- deb/      → debian control + rules, builds bhserve_<ver>_amd64.deb (apt install ./file.deb)
- appimage/ → AppDir + appimagetool recipe, builds BHServe-<ver>.AppImage (download & run)

The auto-updater polls releases/latest and matches the `.deb` / `.AppImage` asset (like the
mac `.pkg` / windows `.exe`). Ship on the `linux-v1.0.x` tag channel. No code-signing needed
(Linux has no Gatekeeper/SmartScreen).
