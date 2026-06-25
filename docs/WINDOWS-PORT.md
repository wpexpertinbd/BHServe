# BHServe for Windows — Port Plan & Architecture Spec

> This document is written **from the macOS codebase** as the build spec for the
> Windows version. Do the actual build **on a Windows machine** (you cannot build
> or test WinUI 3, Windows services, the hosts file, or an MSI from macOS).
> Get the code with `git clone https://github.com/wpexpertinbd/BHServe` — don't
> copy the folder over USB. The Windows app lives in `windows/` in this repo.

BHServe (mac) = a self-contained, free local web-server stack — a ServBay/Herd
alternative. The Windows version is the **Laragon alternative**: same UX, same
brand, an independent implementation (SwiftUI + bash do **not** port).

---

## 1. Decided stack

- **UI:** WinUI 3 (Windows App SDK), **C# / .NET 8**, **unpackaged** (Win32).
  - NOT MSIX — the sandbox blocks spawning child servers + editing the hosts file.
- **Core logic:** a shared **`BHServe.Core`** class library (the brains).
- **CLI:** a thin **`bhserve.exe`** console app over `BHServe.Core` — mirrors the
  Mac's "transparent CLI you can run standalone." The WinUI app calls into
  `BHServe.Core` directly (in-process), NOT by shelling out to the CLI.
- **Installer:** **Inno Setup** (or WiX/MSI) producing a branded `BHServe-Setup.exe`.
- **Auto-update:** same as Mac — poll `releases/latest` on GitHub, download the
  setup `.exe`, run it. (Asset matcher: `.exe` instead of `.pkg`.)

```
windows/
  BHServe.sln
  src/
    BHServe.Core/        # services model, vhost render, php-cgi mgr, certs, dns, db
    BHServe.Cli/         # bhserve.exe  (doctor/init/install/site/db/start/stop/…)
    BHServe.App/         # WinUI 3 GUI  (Dashboard/Services/Sites/DB/Node/Logs/Settings)
  installer/
    bhserve.iss          # Inno Setup script (branded, BiswasHost blue #0d6efd)
  build.ps1              # dotnet publish + makensis/iscc → installer/dist/
```

Keep `docs/ROADMAP.md` shared across Mac + Windows.

---

## 2. The big port mappings (mac → Windows)

| Concern | macOS (current) | Windows (target) |
|---|---|---|
| Lang/UI | Swift + SwiftUI | C# + WinUI 3 |
| Engine | `engine/bhserve` (bash) | `BHServe.Core` (C#) + `bhserve.exe` |
| Pkg mgr | Homebrew (`brew install`) | **download pinned portable zips** into the data dir (see §3) |
| Data dir | `~/.bhserve` | `%LOCALAPPDATA%\BHServe` |
| Binaries | brew kegs in `/opt/homebrew` | `%LOCALAPPDATA%\BHServe\bin\<tool>\<ver>` |
| **PHP runner** | **php-fpm pool → unix socket** `php-<ver>.sock` | **`php-cgi.exe` per version → TCP** `127.0.0.1:<port>` (no php-fpm on Windows!) |
| nginx fastcgi | `fastcgi_pass unix:…/php-8.3.sock;` | `fastcgi_pass 127.0.0.1:9183;` (port-per-version) |
| Web server | nginx / httpd (brew) | nginx (nginx.org Win build); Apache optional later |
| Process mgmt | launchd-free, raw `Process` + pid files | child `Process` + pid/port files in `…\BHServe\run` |
| TLS | mkcert (`/opt/homebrew/bin/mkcert`) | **mkcert.exe** (official Win build) → Windows cert store ✅ |
| `*.test` DNS | dnsmasq + macOS resolver | **hosts-file line per site** (default) or **Acrylic DNS** for wildcard |
| Start at login | LaunchAgent plist (`/usr/bin/open`) | Registry `Run` key OR Task Scheduler "at logon" |
| Elevation | `osascript … with administrator privileges` (sudo) | UAC: a manifested **`bhserve-elevate.exe`** helper for hosts-file/firewall writes |
| DB | MariaDB/MySQL/PG (brew services) | MariaDB/MySQL portable zip; PG optional |
| Node | fnm | **fnm has a Windows build** ✅ (or nvm-windows) |
| phpMyAdmin/Adminer/Mailpit | served vhosts | identical (just static files + a php site + mailpit.exe) |

### PHP-CGI detail (the #1 gotcha)
Windows PHP ships **`php-cgi.exe`**, never php-fpm. For each enabled PHP version:
1. Assign a stable TCP port, e.g. `9100 + (minor)` → 8.1→9181, 8.2→9182, 8.3→9183, 8.4→9184 (pick a scheme, persist it).
2. Spawn `php-cgi.exe -b 127.0.0.1:<port>` with `PHP_FCGI_MAX_REQUESTS=0` and a small pool via `PHP_FCGI_CHILDREN` (children only work when launched under a spawner; on Windows the common pattern is to spawn **N php-cgi processes** on the same port — or one per request-batch). Simplest robust v1: **one php-cgi.exe per version** is enough for local dev; add a multi-process pool later.
3. Track pid + port in `…\BHServe\run\php-<ver>.json`. "Running" = pid alive.
4. nginx vhost for that site: `fastcgi_pass 127.0.0.1:<port>;`.

This is the structural analog of the Mac `render_fpm_pool` + `fpm_start`/`fpm_stop`
+ `php_sock`. The **502 fix we just shipped applies here too**: on "Start All",
start **every PHP version any enabled site references**, not just the starred one.

### Binaries: download, don't bundle (matches the Mac's brew model)
Don't ship php/nginx/mariadb inside the installer (huge + stale). Ship a small app +
a **curated download manifest** (pinned URLs + SHA256) and fetch portable zips on
demand into `…\BHServe\bin` — the Windows equivalent of `brew install`. Sources:
- PHP: `https://windows.php.net/downloads/releases/archives/` (use **NTS** builds for php-cgi; pick VS16/VS17 per version).
- nginx: `https://nginx.org/en/download.html` (Windows zip).
- MariaDB: official "Windows ... ZIP file" (no-installer).
- mkcert / fnm: GitHub release `.exe`.
This keeps the installer ~a few MB like the Mac `.pkg`, and stays "self-controlled"
(no Chocolatey/Scoop dependency).

---

## 3. Feature-parity checklist (map to current `bhserve` subcommands)

Port each Mac engine verb to a `BHServe.Core` method + a `bhserve.exe` subcommand:

- [ ] `doctor` — check deps/ports/conflicts (also detect Laragon/XAMPP on 80/443)
- [ ] `init` — create `%LOCALAPPDATA%\BHServe\{config,nginx\sites,bin,run,logs,sites,certs,tmp}`
- [ ] `install <tool>` — download+extract a pinned portable build (php@x.y / nginx / mariadb / node / mkcert / mailpit / adminer / phpmyadmin)
- [ ] `update <tool>` / `uninstall <tool>`
- [ ] `site add <name> [--php 8.4] [--root] [--server nginx] [--type wordpress|php|others]` — render vhost, add hosts line, auto-create DB, WP download for `--type wordpress`
- [ ] `site list|rm|php|server|root`
- [ ] `secure <domain>` — mkcert into Windows store + re-render vhost ssl block
- [ ] `dns` — hosts-file management (and/or Acrylic config)
- [ ] `db {list|create|drop|passwd}` (mysql/mariadb; pg later)
- [ ] `node {list|install|use|uninstall}` (fnm-win)
- [ ] **`php {ioncube|status|ini {path|reload} <ver>}`** — **including the new Edit php.ini** (path resolves `php_ini_loaded_file()` via that version's `php.exe`; reload restarts that version's php-cgi). ionCube: use the **Windows** `php_ioncube.dll` loaders.
- [ ] `pma|adminer|mailpit` setup (vhost + assets)
- [ ] `config {show|set}` — `tld`, `http_port`, `https_port`, `sites_root`, `default_php`, `default_web`
- [ ] `logs` / `start|stop|restart <svc|all>` / `enable|disable` (auto-start) / `status` / `api` (JSON snapshot the GUI decodes)

GUI screens to match the Mac app 1:1: **Dashboard** (live CPU/RAM/disk + sparkline,
service cards), **Services** (start/stop/install, ★ auto-start, the "…" menu with
Update/Edit php.ini/Uninstall), **Sites**, **Databases**, **Node**, **Logs**, **Settings**
(updates, password-less/elevation helper, start-at-login). Brand blue **#0d6efd**;
tray-resident, close → tray (no taskbar entry), matching the Mac menu-bar behavior.

---

## 4. Elevation model (Windows UAC vs Mac sudo)

Most operations need **no admin** on Windows (a normal user can bind :80/:443 and
run child processes — unlike Unix). Admin is needed only for:
- writing the **hosts file** (`C:\Windows\System32\drivers\etc\hosts`),
- adding a **firewall** rule (optional),
- installing the **mkcert root CA** (mkcert prompts itself).

Pattern: a tiny **`bhserve-elevate.exe`** with `requireAdministrator` in its manifest
that takes a verb (e.g. `hosts-add <domain>`) — the app launches it via `runas`
(one UAC prompt), mirroring the Mac's single `osascript … administrator` prompt.
Offer a "remember / scheduled-task" path later so it's promptless (the Mac
equivalent of the password-less sudoers helper).

---

## 5. Build / dev workflow on Windows

```powershell
# prerequisites: Visual Studio 2022 (or build tools) + .NET 8 SDK + Windows App SDK
git clone https://github.com/wpexpertinbd/BHServe
cd BHServe\windows
dotnet build BHServe.sln
dotnet run --project src\BHServe.App     # launch the GUI
dotnet run --project src\BHServe.Cli -- status   # the CLI

# release
.\build.ps1            # dotnet publish (win-x64, self-contained) + iscc → installer\dist\BHServe-Setup.exe
```

Then cut the GitHub release with `BHServe-Setup.exe` so the in-app updater finds it.

---

## 6. Suggested phasing (each is a shippable milestone)

1. **Skeleton + engine core:** `init`, data dir, download-manager, `install nginx`+`install php@8.4`, render one PHP site, php-cgi-over-TCP, hosts line, serve `http://myapp.test`. CLI first, no GUI.
2. **HTTPS + multi-PHP:** mkcert, `secure`, multiple php versions w/ port scheme, start-all-needed-versions (the 502 fix), `site php` switch.
3. **WinUI shell:** Dashboard + Services + Sites + Settings driving the core; tray; auto-start at logon.
4. **DB + Node + tools:** MariaDB, `db` verbs, fnm-win, phpMyAdmin/Adminer/Mailpit.
5. **php.ini editor, ionCube, auto-updater, Inno installer, polish.**

---

## 7. Things that are genuinely easy on Windows
- **mkcert** works great (installs into the Windows cert store).
- **fnm** has a Windows build — Node story is nearly identical.
- Binding **:80/:443** doesn't need admin (unlike Unix) — fewer prompts overall.
- phpMyAdmin / Adminer / Mailpit are framework-agnostic — copy the vhost concepts.

## 8. Things that need real care
- **php-cgi over TCP** (no php-fpm) — pool/port management + restart-on-ini-change.
- **hosts-file** wildcard limitation (`*.test` needs Acrylic; per-site lines otherwise).
- **Antivirus / SmartScreen** flagging an unsigned `.exe` — document "More info → Run
  anyway," same spirit as the Mac "Open Anyway." Code-signing cert later removes it.
- **Path separators / spaces** everywhere (`C:\Users\...`), and forward-vs-back slashes
  in nginx confs (nginx on Windows wants forward slashes in paths).
