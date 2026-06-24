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

## Install

Grab the latest **`BHServe-x.y.z.pkg`** (or `.dmg`) from the
[**Releases**](https://github.com/wpexpertinbd/BHServe/releases) page.

### ⚠️ First launch on macOS — "unidentified developer" / "damaged" (read this!)

BHServe is **free and open-source but not notarized by Apple** (notarization needs a
paid Apple Developer account). So macOS shows a one-time security warning the **first
time** you open it. This is expected — here's exactly how to get past it (you only do
it **once**; macOS remembers after that):

**Installed the `.pkg`?**
1. Double-click the `.pkg`. If macOS says *"cannot be opened because it is from an
   unidentified developer"* → **right-click (Control-click) the `.pkg` → Open → Open**.
2. Click through the installer. Done.

**Using the `.dmg` (drag to Applications)?**
1. Drag **BHServe** to Applications, then open it. macOS says *"can't be opened because
   Apple cannot check it"* (or *"is damaged"*) — that's the quarantine flag, not a real problem.
2. Open **System Settings → Privacy & Security**, scroll down → you'll see
   *"BHServe was blocked…"* with an **"Open Anyway"** button → click it → **Open**.
   *(Older macOS: right-click the app → Open → Open.)*

> **Why?** Apps not signed with a paid Apple Developer ID always trigger this. BHServe
> is fully open-source — every line is in this repo. The in-app **Settings ▸ Updates**
> handles future versions for you.

## The app (native GUI)
A SwiftUI menu-bar + window app in [`app/`](app/) drives the engine via `bhserve api` (JSON).
100% our own — no ServBay/Herd dependency.
```bash
cd app && ./build-app.sh        # → dist/BHServe.app (self-contained, ad-hoc signed)
open dist/BHServe.app            # or drag to /Applications
./make-dist.sh                   # → dist/BHServe-<ver>.dmg (drag-install) + .pkg (installer)
# dev: swift run BHServe         # or open Package.swift in Xcode
```
Closing the window keeps BHServe running in the menu bar (no Dock icon); reopen from there.
Tabs: **Services** (start/stop/install), **Sites** (add, per-site PHP switch, one-click HTTPS,
open in browser), **Databases** (server start/stop, create/drop, per-DB + root passwords),
**Logs**, **Settings** (ports/TLD/sites-root). Privileged actions (:80/:443, DNS) prompt for admin.
The built app bundles the engine, so it doesn't depend on this checkout.

## Security

BHServe is a **local development** tool, hardened accordingly:
- **Loopback-only:** nginx (and Apache `:8080`, Mailpit `:8025`) listen on **`127.0.0.1`**,
  so your sites, **phpMyAdmin/Adminer**, and Mailpit are **never exposed to the LAN**.
- **DBs use `root` with no password by design** — fine because nothing is reachable off
  this machine. Set a root password anytime in **Databases ▸ root**.
- Site/DB/log names are validated (no path traversal / config injection); DB inputs are
  SQL-escaped; passwords pass via env (never argv).
- The optional "password-less control" helper grants `sudo` **only** to the `nginx`
  binary (so it can bind `:80/:443`) — the same approach Laravel Valet uses.

## Roadmap
1. ✅ Foundation: config root, dependency doctor, service registry.
2. ✅ Web + sites: nginx, `*.test` (dnsmasq), HTTPS (mkcert), per-site PHP-FPM, start/stop.
3. Data + extras: MariaDB/MySQL, PostgreSQL, Redis, phpMyAdmin, Adminer, Mailpit, Node.
4. ▶️ Native GUI over the engine — **scaffold building & running** (Services + Sites).
5. Packaging (.app bundle, LaunchAgent auto-start), sign/notarize.

— BiswasHost · https://www.biswashost.com

## ☕ Support

BHServe is free and open-source. If it saved you time setting up your local dev
stack, you can **buy me a coffee** — it genuinely helps me keep building and
maintaining free tools like this. 🙏

- **bKash** (Personal · *Send Money*): **`01710378396`**

ধন্যবাদ! / Thank you!
