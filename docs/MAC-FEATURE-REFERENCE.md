# BHServe macOS — Feature & GUI Reference (parity checklist for the Windows/Linux ports)

> **Purpose:** the complete, authoritative description of what the **shipping macOS
> app** (v1.5.7) does and how its UI is laid out, so the Windows (and later Linux)
> build can be compared screen-by-screen and bring every missing feature to parity.
> Each section names the **exact Mac source file** to read for the real behavior —
> open it alongside the corresponding Windows file and match the features + layout.
>
> Mac source lives in `app/Sources/BHServe/`. The engine verbs are in `engine/bhserve`.
> Windows equivalents live in `windows/src/BHServe.App/` (GUI) + `BHServe.Core` (engine).

---

## 0. App shell & menu-bar behavior — `BHServeApp.swift`, `ContentView.swift`

**Mac files:** `BHServeApp.swift` (menu-bar app, on-demand window, MenuBarView), `ContentView.swift` (sidebar + routing + StatusFooter).

- **Menu-bar resident app.** Lives in the menu bar (Windows: **system tray**). Closing the
  window keeps it running (Mac drops Dock icon → `.accessory`; Windows: hide to tray, no taskbar entry).
- **On-demand main window** — a background/login launch shows **no window**, just starts services.
  Normal launch / clicking the tray opens it. Window ~860×620.
- **Menu-bar / tray popover** (`MenuBarView`) contains, top-to-bottom:
  - App name + version + a green/grey "any service running" dot
  - One-line list of running services
  - **Live CPU / RAM / Disk** bars + a CPU **sparkline**
  - **Start All / Stop All / Restart All** buttons — **state-aware enablement**: Start All is disabled when all
  installed daemon services are already running; Stop All / Restart All are disabled when nothing is running.
  (mkcert/fnm are excluded — they're tools, not daemons, so they don't count toward "all running".)
  - First **5 sites** as clickable open-in-browser links (lock icon if HTTPS); "+N total" hint if >5
  - **Tools** quick-open (phpMyAdmin / Adminer / Mailpit) — only when installed & served
  - **Open BHServe** + **Quit**
- **Sidebar (left nav), 7 items in this order** (`ContentView.SidebarItem`):
  **Dashboard · Services · Sites · Databases · Node · Logs · Settings**, each with an SF icon.
  A **blue dot** appears on *Settings* when an update is available. Sidebar **bottom footer** =
  `StatusFooter` (overall status). → **Windows = a NavigationView with these same 7 panes + a footer.**

---

## 1. Dashboard (home page) — `DashboardView.swift`, `WebsitesPanel.swift`

The home screen, top-to-bottom (`DashboardView.body`):

1. **Row of 4 status cards** (`StatusCard` / `CacheCard`), responsive 2-or-4 columns:
   - **Web Server** — nginx running? + `<version> · <N> sites`
   - **PHP** — running versions (e.g. "8.4, 8.3") + "<N> installed"
   - **Database** — MariaDB/MySQL running? + version
   - **Cache** — Redis / Memcached install + run status (lists each engine)
2. **Row of 4 live system-metric cards** (`SystemMetricsGrid`, 2-second sampling, `SystemMetrics.swift`):
   - **CPU** (% + sparkline, green/orange/red by load)
   - **Memory** (% + used/total GB, progress bar)
   - **Storage** (% + used/total GB)
   - **Network** (↓ down / ↑ up throughput)
3. **Websites panel** (`WebsitesPanel`) — see §3 (search + paginated 10/page + per-site actions).
4. **Web tools panel** (`ToolsPanel`) — phpMyAdmin / Adminer / Mailpit install + on/off toggles + Open.

→ **Windows gap to check:** does `DashboardPage` have all **8 cards** (4 status + 4 live metrics with a
working sampler), the websites panel, AND the web-tools panel? The live metrics (CPU/RAM/disk/net) are a
signature feature — Windows needs a `PerformanceCounter`/WMI sampler feeding the same 8-card layout.

---

## 2. Services — `ServicesView.swift`

**Mac file:** `ServicesView.swift` (`ServicesView`, `ServiceRow`).

- Services **grouped by role** (PHP, Web Server, Databases, Cache, Mail, DNS…), each group a titled card.
- **Each row** (`ServiceRow`): status dot · key (e.g. `php@8.4`) + short version · **★ auto-start toggle** ·
  **Start/Stop** button (or **Install** if not installed) · **"…" menu**.
- **The "…" menu** (installed services): **Update to latest** · **Edit php.ini** (PHP rows only — opens an
  editor sheet, saves + reloads that version's FPM) · **Uninstall** (confirm dialog).
- Buttons disabled while `busy`; nginx/all/dns may show an admin prompt unless the password-less helper is on.
- **Non-daemon tools show "active" once installed:** mkcert (tls) and **fnm (node)** report running when
  installed (they're ready, not background services). Install/update show a **result sheet** (success/failure
  + the engine's steps) via `ResultSheet`.
- **Update to latest = auto-upgrade to latest stable** (engine `update_one`, the standard live-server flow):
  `brew update` + `brew upgrade <formula>` installs the **latest stable**, then the service is **restarted onto
  the new binary** so sites keep working, and for **MariaDB/MySQL** it runs **`mariadb-upgrade`** (= the
  upgrade-command you'd run on a live box; migrates the system/privilege tables to the new version). **No full
  DB dump** — the data directory stays compatible, so dumping every time would only create disk bloat. nginx
  that can't restart (no root) keeps serving on the old binary until the next restart.

→ **Windows gap:** role-grouped list, ★ auto-start, install/uninstall, **and the per-row "…" menu with
Edit php.ini** (the engine already has `php ini path|reload` — wire the GUI editor sheet to it).

---

## 3. Sites — `SitesView.swift`, `WebsitesPanel.swift`

The Sites tab and the Dashboard websites panel share the **same row + add sheet**.

- **Header:** site count + **search box**; **＋** add button; a **"Show:" menu** (`PerPagePicker`) =
  **10 / 15 / 20 / 50 / 100 / All**, defaulting to the per-list page size from Settings (§7) and able to
  override it per view.
- **Pagination** (`SitePaging` / `PageBar`): configurable page size (default **10** on Dashboard, **15** on
  the Sites tab — both set in Settings); footer shows prev / **"Page X of Y"** / next **plus a jump-to-page box**
  (type a number → Go jumps straight there). Resets to page 1 on search or page-size change. "All" = no paging.
- **Each site row** (`WebsiteRow`): enabled dot · name · domain · **server badge** (nginx/apache) · **php badge** ·
  a row of **circular action buttons**:
  - 🧭 **Open in browser**
  - 📁 **Open folder** (Finder → Windows: Explorer)
  - ✏️ **Edit** (sheet: PHP version, web server, root folder, **Enable HTTPS**)
  - 🔎 **Logs** (per-site access/error log sheet)
  - 📡 **Share publicly** (Cloudflare Tunnel — gives a public URL; green when active)
  - ▶️/⏸ **Start/Stop** (enable/disable the vhost)
  - 🗑 **Delete** (confirm; files kept on disk)
- **Add Site sheet** (`AddSiteSheet`): name + `.tld`; **Type = WordPress / PHP / Others(static) / Node app**;
  **PHP version**; **web server** (nginx/apache); **site root = default folder or custom (folder picker)**.
  - *WordPress* → creates DB + downloads WP + pre-writes wp-config.
  - *PHP* → creates a DB named after the site.
  - *Others* → domain only, no DB.
  - *Node app* → frontend (folder + run command + port) **+ optional backend/API** (folder + command + port) +
    api-paths regex. BHServe runs both as supervised processes and reverse-proxies them at the domain.
- **Site-added success notice** (`ResultSheet`, `AppState.ActionResult`): after Add Site (and after installing a
  service) a sheet shows the engine's steps with green checks, the new URL as a link, and **Open site / Done**.
- **Per-site "…" menu** (php `WebsiteRow`): **Change PHP ▸** (version submenu) · **Change root folder…** ·
  **Switch to nginx/apache** · **Enable HTTPS** · **Delete**. (The Node row keeps its own node-specific menu.)
- **Remove site dialog** (`RemoveSiteSheet`): a checkbox **"Also delete the site files and drop its database"**
  → engine `site rm --purge` (drops the named DB + `rm -rf` the root, guarded to `$HOME` only). Default keeps both.

### Node sites (managed) — `NodeSiteUI.swift`, engine `nodesite` verb, `Models.Site` node fields
A first-class **Node** site type. Engine: `bhserve nodesite {add|list|rm|start|stop|restart|status|npm}` —
each site = a **frontend** process (dir/cmd/port) + **optional backend** (dir/cmd/port), supervised via pid
files + per-process logs, fronted by an auto-rendered nginx reverse-proxy vhost (`/` → FE, `/api`,`/storage`,…
→ BE) with mkcert HTTPS. Config in `~/.bhserve/node-sites/<name>.json`; the `api` snapshot carries
`node/feRunning/beRunning/fePort/bePort/feDir/beDir/feCmd/beCmd/apiPaths`. **Node rows** in the Sites list
(`WebsiteRow` node branch) show a green run-dot + `node` badge + fe/be port badges, and actions:
**Open · Open folder · Start/Stop (processes) · Restart · Process logs · "…" menu** (Edit config
[ports/commands] · Edit frontend/backend **.env** · **npm install** frontend/backend) · **Delete**.
The **.env editor** (`EnvEditorSheet`) saves + restarts the site; **Edit config** (`EditNodeSheet`) changes
folders/commands/ports and re-renders the vhost; **process logs** (`NodeLogsSheet`) show fe/be output.

→ **Windows gap:** none of this exists in the Windows build yet. Port the whole `nodesite` engine verb +
the GUI Node type + node rows + .env editor + npm-install. (Windows: spawn `node`/`npm`/`php` as child
processes with pid files; same reverse-proxy vhost shape; `.env` editing identical.)

→ **Windows gaps to check:** pagination (10/page), the **6–7 per-row circle actions** (esp. **Open folder**,
**per-site Logs**, **Cloudflare public Share**, Start/Stop, Edit-with-HTTPS), and the **3 site types with
auto-DB + WP download** in the Add sheet.

---

## 4. Databases — `DatabasesView.swift`

**Mac file:** `DatabasesView.swift`.

- **Database servers** — two always-shown `DbServerRow`s: the **MySQL family first** (MariaDB *or* MySQL,
  whichever is installed; labelled accordingly) then **PostgreSQL**. Each is state-aware: **Install** when
  absent, **Start** (disabled when running), **Stop** (disabled when stopped), **Root password…** (MySQL family,
  when running), and a status line ("running · no password" / "stopped" / "not installed").
- **Create-database engine dropdown auto-detects installed engines** (`AppState.dbEngineOptions`): shows just
  MariaDB *or* MySQL, plus PostgreSQL if installed — so the options are e.g. only-MariaDB, MariaDB+PostgreSQL,
  MySQL+PostgreSQL, or only-PostgreSQL. Create is disabled (with a hint) until the chosen engine is running.
- **root user card** (`RootUserCard`) — shows whether root@localhost has a password; **Set/Change root password** sheet.
- **Create database** (when a server runs): name + **engine picker** (MySQL/PostgreSQL) + **optional password** with
  a **Generate** button. Blank password = no dedicated user; a password creates a user named after the DB.
- **Database list** — each row (`DatabaseRow`): name · engine label · user (if any) · **Set/Change password** · **Drop** (confirm).

→ **Windows gap:** root-password management, create-with-optional-user+generated-password, per-DB
change-password + drop. (The Windows CLI has `db list/create/drop` but the GUI needs the full card UI.)

---

## 5. Node — `NodeView.swift`

**Mac file:** `NodeView.swift`. Uses **fnm**.

- If fnm not installed → an **Install fnm** prompt.
- **Install a version**: text field (`18 · 20 · 22 · lts · 20.11.0`) + **quick buttons** (18/20/22/24/lts/latest).
- **Installed versions** list: each row → **Use** (set default) + **Uninstall**, with a **default** badge.
- Note about adding the default to PATH.

- **Node apps section** (same screen): an **"Add Node app"** button + a list of the managed Node sites
  (the same node `WebsiteRow`s as the Sites tab) — so Node apps can be added/managed **from either the Node
  tab OR the Sites-tab "Node app" type**. `AddNodeAppSheet` = Name + frontend (folder/cmd/port) + optional
  backend (folder/cmd/port) + api-paths.

→ **Windows gap:** the quick-pick buttons + installed-versions list with Use/Uninstall + default badge (fnm has a Windows build). (Windows already has the Node-tab "Add Node app" + apps list — keep BOTH entry points, like macOS.)

---

## 6. Logs — `LogsView.swift`

**Mac file:** `LogsView.swift`.

- **Log file picker** (dropdown of all `*.log`) + **Reload**.
- Monospaced, selectable log viewer (tail). Empty-state when no logs yet.

→ **Windows gap:** the picker + monospaced tail viewer. (Engine `logs`/`LogFiles`/`LogText` already exist.)

---

## 7. Settings — `SettingsView.swift`

**Mac file:** `SettingsView.swift`. Grouped form:

- **Domains & ports:** TLD, HTTP port, HTTPS port (note about <1024 needing admin).
- **Site lists:** **Sites per page — Dashboard** (default **10**) and **Sites per page — Sites tab**
  (default **15**), free-numeric input, persisted. These seed each list's "Show" menu default (§3).
- **Updates:** current version · **"Automatically check for updates" toggle (default ON)** · check / "up to date" /
  **Update available → Download & Install** / failed+retry. (Windows updater asset = `.exe` instead of `.pkg`.)
- **Startup:** **Launch at login** · **Start services when BHServe launches** · **password-less control** helper
  (Mac = sudoers rule; **Windows analog = the elevation/Task-Scheduler path**).
- **Defaults for new sites:** default PHP version · default web server · sites root.
- **Save / Revert** (changing TLD/ports re-renders vhosts + restarts nginx).

→ **Windows gap:** the **auto-update toggle (default on) + update flow**, launch-at-login, start-services-on-launch,
default-PHP/web/sites-root, and TLD/port editing that re-renders vhosts.

---

## 8. Full feature checklist (engine + app) — what "parity" means

Multi-PHP (7.4, 8.1–**8.6**) per-site · **Edit php.ini per version** · **ionCube** loader · **nginx + Apache**
(.htaccess) · **MariaDB / MySQL / PostgreSQL** · **Redis / Memcached** · **phpMyAdmin / Adminer / Mailpit**
(per-tool on/off) · **Node via fnm** (multi-version) · **trusted HTTPS via mkcert** · **`*.test` domains** ·
**2 GB uploads** · **site types WP / PHP / Others + auto-DB** · **WordPress auto-download + wp-config** ·
**per-site custom root** · **per-site logs** · **Cloudflare Tunnel public sharing** · **live system metrics**
(CPU/RAM/disk/net + sparkline) · **menu-bar/tray resident** · **start-at-login** · **start-services-on-launch** ·
**password-less elevation helper** · **auto-update (default on) + sidebar badge** · **branded installer**.

> **How to use this doc (for the Windows session):** for each numbered section, open the named **Mac
> `app/Sources/BHServe/*.swift`** file and the matching **`windows/src/BHServe.App/Views/*Page.xaml`**, then add
> any missing controls/cards/actions and match the layout order. The **engine** side (`BHServe.Core`) already
> covers most verbs from Phase 1+2 — most gaps are in the **GUI pages**, not the engine. Keep the brand blue
> **#0d6efd**, loopback-only binds, and the same screen order (Dashboard→Services→Sites→Databases→Node→Logs→Settings).
