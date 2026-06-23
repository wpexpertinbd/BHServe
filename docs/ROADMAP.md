# BHServe — progress & next steps

## Decisions locked
- **Form:** native **SwiftUI menu-bar app** driving a transparent **bash engine** (`engine/bhserve`).
- **Driver:** a free, fully self-controlled stack (no vendor lock-in), latest versions via Homebrew.
- **Scope:** multi-PHP (per-site), **Nginx AND Apache** (either/both), MySQL/MariaDB + PostgreSQL + Redis, phpMyAdmin + Adminer + Mailpit + Node, `*.test` + trusted HTTPS.
- **Config root:** `~/.bhserve/` (separate from system/brew). Engine: `/Applications/ServBay/www/BHServe/engine/bhserve`.
- **Coexistence with ServBay: UNDECIDED — paused.** When resuming, first pick:
  (a) test on alt ports 8080/8443 (+ maybe `.bhtest`) so ServBay stays up, then switch to 80/443/.test later; or
  (b) quit ServBay and have BHServe own 80/443 + `*.test` (migrate the user's `www` sites in).

## Done — Phase 1 (foundation)
- `engine/bhserve` with `doctor` (deps + ports + ServBay check), `init` (creates `~/.bhserve`), `status`, and a service registry (php/php@8.1-8.4, nginx, httpd, mariadb, mysql, postgresql@17, redis, dnsmasq, mkcert, mailpit, node).
- Env confirmed: macOS 26.6 arm64, **Xcode 26.5 + Swift 6.3.2** (can build the GUI), Homebrew 6. Installed already: php@8.1-8.4 (+ default `php` symlink oddly points to 7.4 — BHServe should normalize), nginx 1.31, mariadb 12.3.

## Next — Phase 2 (web + sites) — START HERE
1. `bhserve install <svc>` (brew install wrapper) — add httpd, dnsmasq, mkcert, redis, postgresql@17, mailpit, node.
2. Generate nginx main conf + per-site vhost templates in `~/.bhserve/nginx/`; `bhserve site add <name> [--php x.y] [--root path] [--server nginx|apache]`.
3. `*.test` via dnsmasq + `/etc/resolver/test` (needs sudo).
4. Trusted HTTPS via `mkcert -install` + per-site certs in `~/.bhserve/certs/`.
5. Per-site **PHP-FPM** pools (one socket per PHP version); wire fastcgi in vhosts.
6. `bhserve start|stop|restart <svc|all>` — process/pid management in `~/.bhserve/run/`.
   - ⚠️ ports 80/443 + dnsmasq need sudo/root; design the privilege story (sudo prompts now; a privileged helper for the GUI later).
7. Fix version probes (nginx uses `-v` to stderr; capture `2>&1`).

## Later
- Phase 3: DBs (start/stop, create/drop helpers) + phpMyAdmin/Adminer/Mailpit/Node.
- Phase 4: SwiftUI menu-bar app (`app/`) calling the engine (status poll, start/stop, site list, PHP switch).
- Phase 5: package the `.app`, LaunchAgent auto-start, optional sign/notarize.
