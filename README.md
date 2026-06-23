# BHServe — a free local dev environment for macOS

A self-controlled, native macOS local development stack (by BiswasHost) — manage
**PHP (multi-version), Nginx + Apache, MySQL/MariaDB + PostgreSQL + Redis,
phpMyAdmin/Adminer, Mailpit, Node**, with automatic `*.test` domains and trusted
local HTTPS. A SwiftUI menu-bar app drives a transparent Bash engine over
Homebrew — latest versions, nothing hidden, fully yours.

> **Status: early build (Phase 1 — foundation).** Not ready for daily use yet.

## Architecture
- `engine/bhserve` — the engine CLI (install/configure/start/stop services, sites, SSL, DNS).
- `app/` — SwiftUI menu-bar app (drives the engine).  *(coming in a later phase)*
- Config & generated files live in `~/.bhserve/` (separate from system/brew defaults).

## Try the engine
```bash
engine/bhserve doctor              # check deps + ports + ServBay
engine/bhserve init                # create ~/.bhserve config root
engine/bhserve install nginx mkcert dnsmasq   # brew install services
engine/bhserve site add myapp --php 8.4        # → http://myapp.test
engine/bhserve secure myapp.test               # trusted local HTTPS
engine/bhserve dns                             # set up *.test (prints sudo steps)
engine/bhserve start all                       # nginx + PHP-FPM pools
engine/bhserve status                          # config + what's running
```
> BHServe owns ports **80/443** and the `*.test` domain — quit ServBay first.
> DNS, the first cert, and binding 80/443 need **sudo** (you'll be prompted).

## Roadmap
1. ✅ Foundation: config root, dependency doctor, service registry.
2. ✅ Web + sites: nginx, `*.test` (dnsmasq), HTTPS (mkcert), per-site PHP-FPM, start/stop.
3. Data + extras: MariaDB/MySQL, PostgreSQL, Redis, phpMyAdmin, Adminer, Mailpit, Node.
4. SwiftUI menu-bar app over the engine.
5. Packaging, auto-start, polish.

— BiswasHost · https://www.biswashost.com
