# BHServe for Linux — Ubuntu/Debian (beta)

> **Status: v1.0.0 beta — engine + GTK app + `.deb` all working** (verified on Ubuntu 24.04
> under WSL2). Built by the Windows-Claude per this plan. What's done:
> - **Engine** (`../engine/platform-linux.sh`, sourced only on Linux — macOS untouched): apt +
>   the Ondřej PHP repo, systemd service control, BHServe runs its own nginx + php-fpm.
>   `init` / `install` / `site add` / multi-PHP / **trusted HTTPS (mkcert)** / `*.test` via a
>   managed `/etc/hosts` block / **MariaDB** (root→loopback blank-password, the WordPress path) /
>   **Python apps** (pysite) all verified end-to-end. fnm + Mailpit install supported.
> - **GUI** (`app/bhserve/`, Python + GTK4/libadwaita): all 8 panes at macOS parity, drives the
>   bash engine via the `api` JSON, search+Show+pagination, per-site … menu, self-updater.
> - **Packaging** (`build.sh` → `dist/bhserve_<ver>_all.deb`): installs the engine + GUI,
>   `bhserve` / `bhserve-gui` on PATH, desktop entry + icon.
>
> **To do:** `.AppImage` build; cut the `linux-v1.0.x` GitHub release; native-Ubuntu polish
> (Wayland tray, postgres peer-auth, the wildcard-`.test` resolved path on real desktops).
>
> The macOS build (**v1.7.4**) is the design + feature reference. The full architecture spec is
> [`../docs/LINUX-PORT.md`](../docs/LINUX-PORT.md); the exact engine changes are in
> [`engine/DELTAS.md`](engine/DELTAS.md); the Mac feature list to reach parity with is
> [`../docs/MAC-FEATURE-REFERENCE.md`](../docs/MAC-FEATURE-REFERENCE.md) and the per-release
> changelog is the project memory `MEMORY.md` index line.

## Why Linux is the *easiest* of the three ports

The Mac and Linux builds share the **same engine language (bash)** and the **same runtime model**
(php-fpm pools on **unix sockets**, raw processes + pid files, nginx/apache vhosts, mkcert, dnsmasq,
fnm, ionCube). Windows had to rewrite all of that (`php-cgi.exe` over TCP, no unix sockets, no
fpm). On Linux **almost all of `../engine/bhserve` runs as-is** — only a handful of macOS-isms need a
Linux variant (see `engine/DELTAS.md`). The real new work is a **GTK GUI** (SwiftUI doesn't run on
Linux) that follows the Mac design.

## Develop on WSL2 (Windows-Claude can start immediately)

WSL2 is a real Ubuntu kernel, so the engine + services run natively there:

```bash
wsl --install -d Ubuntu-24.04          # once, on the Windows host
# inside the Ubuntu shell:
sudo apt update && sudo apt install -y git build-essential
git clone https://github.com/wpexpertinbd/BHServe && cd BHServe
```

WSL2 caveats to keep in mind (documented in `engine/DELTAS.md`): systemd is available on recent WSL
(`wsl --update`; needs `systemd=true` in `/etc/wsl.conf`); `*.test` DNS and ports 80/443 behave like
real Linux; the GUI can show through **WSLg** (built-in Wayland/X) so you can run the GTK app from
WSL and see the window on Windows. Final validation should still happen on a **native Ubuntu
22.04/24.04 + GNOME** box/VM before release.

## The plan in one screen

1. **Engine on Linux first (no GUI).** Pick the package source — start with **Homebrew-on-Linux
   (Linuxbrew)** for maximum reuse of the existing `brew` calls; offer native **apt + Ondřej PPA**
   later. Get `init` → `install nginx` → `install php@8.4` → `site add myapp` serving
   `http://myapp.test` (hosts-file DNS to start). Apply the deltas in `engine/DELTAS.md`.
2. **HTTPS + DNS + multi-PHP.** mkcert CA into the system + browser NSS trust; `secure`; the
   wildcard-`.test` approach (systemd-resolved drop-in — Ubuntu owns `:53`); multi-version FPM (the
   502-autostart fix is already in the engine).
3. **GTK shell** (GTK4 + libadwaita; Rust `gtk4-rs` *or* Python PyGObject) driving the engine exactly
   like the SwiftUI app drives it on Mac — spawn the CLI, parse the `api` JSON. Build the 8 panes
   below. Tray via `StatusNotifierItem`/AppIndicator; close → tray. Brand blue **#0d6efd**.
4. **DB + Node + Python + tools.** MariaDB (Ubuntu socket/auth deltas), fnm, **python (venv)**,
   phpMyAdmin/Adminer/Mailpit.
5. **Packaging + updater + polish.** `.deb` + `.AppImage`; auto-updater polling `releases/latest`
   (asset matcher `.deb`/`.AppImage`); the update-check **throttle** (≤1 GitHub call / 30 min — see
   the Mac fix). Then ship on the **`linux-v1.0.x`** tag channel (Mac = `v1.7.x`, Windows = `win-v1.0.x`).

## GUI panes to build (parity with macOS v1.7.4)

Mirror the Mac app 1:1 — these are the sidebar items in `ContentView.swift`:

| Pane | What it has (see `../docs/MAC-FEATURE-REFERENCE.md` for the detail) |
|---|---|
| **Dashboard** | live CPU/RAM/disk + sparkline, service status cards, **Websites panel** (search + Show menu + pagination), Web-tools card |
| **Services** | per-service install / start / stop / ★ auto-start / **Update to latest** / Uninstall; PHP rows have **Edit php.ini** |
| **Sites** | add-site sheet (WordPress / PHP / Others / **Node app** / **Python app**), per-site `…` menu (Change PHP/root/server, HTTPS, **Open in editor**, **Open terminal**, Delete), **search + Show + pagination** |
| **Databases** | MySQL-family + PostgreSQL server rows (install/start/stop/root-password), engine-aware Create dropdown, **search + Show + pagination** |
| **Node** | managed Node versions (fnm) + **Node apps** list (`ManagedAppsSection`: search + Show + pagination) |
| **Python** | managed interpreter (install/update) + **Python apps** list (same `ManagedAppsSection`) |
| **Logs** | per-service / per-site log viewer |
| **Settings** | autostart, auto-update toggle, **List sizes** (Dashboard / Sites / Databases / Apps per-page), defaults for new sites, password-less control |

## What's already true and must be preserved

- **Data dir `~/.bhserve`** is unchanged and already `$HOME`-based — node-sites in `node-sites/`,
  python-sites in `py-sites/`, vhosts in `nginx/sites/`, certs in `certs/`, pids/logs in `run/`+`logs/`.
- The **`api` JSON** the GUI consumes already carries node + python site fields — don't change its shape.
- **Security:** MariaDB/MySQL must bind **127.0.0.1** (loopback only) — same as the Mac fix.
- **Channels stay separate:** never touch the macOS `v1.7.x` or Windows `win-v1.0.x` releases; Linux
  ships on its own `linux-v1.0.x` tags.

## Folder layout (create as you go)

```
linux/
  app/                 # GTK4 + libadwaita GUI (Rust gtk4-rs OR Python PyGObject)
  engine/
    DELTAS.md          # the exact macOS→Linux changes to ../../engine/bhserve  ← READ THIS
    platform-linux.sh  # (to write) the Linux platform-delta layer the engine sources
  packaging/
    deb/               # debian/ control + rules → bhserve_<ver>_amd64.deb
    appimage/          # AppDir + appimagetool recipe
  build.sh             # build app + .deb + .AppImage
```
