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
engine/bhserve doctor   # check what's installed + coexistence with ServBay
engine/bhserve init     # create ~/.bhserve config root
engine/bhserve status   # show current config
```

## Roadmap
1. ✅ Foundation: config root, dependency doctor, service registry.
2. Web + sites: nginx & Apache, `*.test` (dnsmasq), HTTPS (mkcert), per-site PHP-FPM.
3. Data + extras: MariaDB/MySQL, PostgreSQL, Redis, phpMyAdmin, Adminer, Mailpit, Node.
4. SwiftUI menu-bar app over the engine.
5. Packaging, auto-start, polish.

— BiswasHost · https://www.biswashost.com
