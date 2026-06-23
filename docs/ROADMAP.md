# BHServe — progress & next steps

## Decisions locked
- **Form:** native **SwiftUI menu-bar app** driving a transparent **bash engine** (`engine/bhserve`).
- **Driver:** a free, fully self-controlled stack (no vendor lock-in), latest versions via Homebrew.
- **Scope:** multi-PHP (per-site), **Nginx AND Apache** (either/both), MySQL/MariaDB + PostgreSQL + Redis, phpMyAdmin + Adminer + Mailpit + Node, `*.test` + trusted HTTPS.
- **Config root:** `~/.bhserve/` (separate from system/brew). Engine: `/Applications/ServBay/www/BHServe/engine/bhserve`.
- **Coexistence with ServBay: DECIDED (2026-06-24) — option (b): BHServe owns 80/443 + `*.test`.**
  ServBay must be quit before BHServe web/DNS binds those ports; ports 80/443 + dnsmasq use sudo.

## Done — Phase 1 (foundation)
- `engine/bhserve` with `doctor` (deps + ports + ServBay check), `init` (creates `~/.bhserve`), `status`, and a service registry (php/php@8.1-8.4, nginx, httpd, mariadb, mysql, postgresql@17, redis, dnsmasq, mkcert, mailpit, node).
- Env confirmed: macOS 26.6 arm64, **Xcode 26.5 + Swift 6.3.2** (can build the GUI), Homebrew 6. Installed already: php@8.1-8.4 (+ default `php` symlink oddly points to 7.4 — BHServe should normalize), nginx 1.31, mariadb 12.3.

## Done — Phase 2 (web + sites)
1. ✅ `bhserve install <svc|all>` — brew install wrapper over the registry.
2. ✅ nginx main conf (`~/.bhserve/nginx/nginx.conf`, catch-all default + `include sites/*.conf`) + per-site vhosts; `bhserve site add <name> [--php 8.4] [--root path] [--server nginx|apache]`, `site list`, `site rm`.
3. ✅ `bhserve dns` — writes BHServe dnsmasq conf for `*.test` and prints the sudo activation steps (dnsmasq.d + `/etc/resolver/test`).
4. ✅ `bhserve secure <domain>` — `mkcert -install` (once) + per-site cert into `~/.bhserve/certs/`, then re-renders the vhost to turn on the HTTPS listener.
5. ✅ Per-site PHP-FPM pools — one socket per PHP version in `~/.bhserve/run/php-<ver>.sock`; vhost fastcgi wired to the chosen version. **Verified live**: pool starts + binds socket; `nginx -t` passes.
6. ✅ `bhserve start|stop|restart <svc|all>` — nginx (sudo when port <1024), FPM pools (pid tracking in `run/`), and brew-services daemons (mariadb/mysql/postgresql@17/redis/mailpit/dnsmasq). `status` shows running state + sites.
7. ✅ Version probes capture `2>&1` (nginx/httpd version goes to stderr).

### Not yet executed (privileged — run when going live, ServBay quit)
- `bhserve dns` activation steps (sudo: dnsmasq + `/etc/resolver/test`).
- `mkcert -install` (keychain prompt) on first `bhserve secure`.
- `bhserve start all` binding 80/443 (sudo).
- Optional: normalize the default `php` symlink (currently 7.4); BHServe sidesteps it by defaulting to `php@8.4`.

## In progress — Phase 4 (native GUI, our own — no ServBay/Herd/Laragon dependency)
Engine contract: `bhserve api` emits JSON (config + services{installed,running,version} + sites).
SwiftUI app in `app/` (SwiftPM, macOS 14+, builds with `swift build` / opens in Xcode):
- ✅ `Engine.swift` — Process bridge: `run` (user) + `runPrivileged` (osascript admin prompt for :80/:443 + dns, shell+AppleScript escaped), `snapshot()` decodes `api` JSON.
- ✅ `AppState` (@Observable) — resolves engine path (env → ~/.bhserve → dev checkout), reload/control/install/addSite/secure/removeSite, runs engine off the main actor.
- ✅ UI — `Window` + `MenuBarExtra`; NavigationSplitView (Services / Sites). Services grouped by role with status dots + Start/Stop/Install; Sites list with open-in-browser, one-click Secure, add-site sheet (PHP picker), remove. Start/Stop All in sidebar footer + menu bar.
- ✅ Live auto-refresh (4s), per-site PHP version switch.
- ✅ Databases: engine `db {list|create|drop|passwd} [name] [--engine mysql|pg] [--password PW]` (mysql auth auto-detect: OS user → -u root; name validation). Optional per-DB user (named after the DB) with password — set on create, set/change after, dropped with the DB; password passed via `$BHSERVE_DB_PASSWORD` (never argv); SQL-escaped. GUI Databases tab: server start/stop, create with engine picker + optional password + Generate, per-row Set/Change password sheet (hasUser-aware), drop with confirm.
- ✅ DB root user: engine `db root-status` / `db root-passwd` (empty = blank); GUI root-user card with Set/Change password (blank allowed) + Generate. Only touches root@localhost (the OS-user socket account we operate through is untouched).
- ✅ Fixed mysql/mariadb collision: probe keg-specific `opt/<formula>/bin/...` (bin/mysql is a mariadb symlink, was falsely flagging mysql installed → broken Start).
- ✅ Settings: engine `config {show|set <key> <value>}` (validates; tld/port changes regenerate all vhosts + nginx.conf) + GUI Settings tab (TLD, http/https port, sites_root, default PHP/web; Save restarts nginx on port/tld change via admin prompt).
- ✅ Logs: engine `logs [file|--list] [lines]` + GUI Logs tab (pick a log, monospaced tail, reload).
- ✅ Node multi-version: registry `node` → **fnm**; engine `node {list|remote|install|use|uninstall}` (versions under `~/.bhserve/fnm`; `use` sets fnm default + links node/npm/npx into `~/.bhserve/bin`). GUI Node tab: install by version/quick-buttons (18/20/22/24/lts/latest), installed list with default badge + Use/Uninstall.
- ▶️ Next: Apache vhosts, phpMyAdmin/Adminer/Mailpit one-click panels.

## Phase 5 (packaging) — in progress
- ✅ `app/build-app.sh` → self-contained **BHServe.app** (bundles the engine in Resources; app prefers the bundled engine, then `~/.bhserve/engine`, then dev checkout). Info.plist (`com.biswashost.bhserve`), ad-hoc signed last (Apple-Silicon "damaged" trap avoided). Engine prepends `/opt/homebrew/{bin,sbin}` to PATH so a Finder-launched app still finds `brew`.
- ✅ App icon (.icns, BH blue). Menu-bar-resident: close window → .accessory (no Dock icon, stays running); reopen → .regular.
- ✅ Distributables: `app/make-dist.sh` → `BHServe-<ver>.dmg` (drag-to-Applications, +Applications symlink) and `BHServe-<ver>.pkg` (pkgbuild → /Applications). Ad-hoc signed; DMG checksum verified, PKG payload verified.
- ▶️ Next: LaunchAgent (launch-at-login / autostart services), optional Developer-ID sign + notarize for clean distribution to other Macs.
- Run now: `cd app && swift run BHServe`  (engine must be initialized; privileged actions prompt for admin).

## Later
- Phase 3 (fold into GUI): DB start/stop + create/drop helpers, phpMyAdmin/Adminer/Mailpit/Node.
- Phase 5: package the `.app` (Info.plist, LSUIElement, bundle the engine), LaunchAgent auto-start, sign/notarize.
