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

## Later
- Phase 3: DBs (start/stop, create/drop helpers) + phpMyAdmin/Adminer/Mailpit/Node.
- Phase 4: SwiftUI menu-bar app (`app/`) calling the engine (status poll, start/stop, site list, PHP switch).
- Phase 5: package the `.app`, LaunchAgent auto-start, optional sign/notarize.
