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
  - **Start All / Stop All / Restart All** buttons
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

→ **Windows gap:** role-grouped list, ★ auto-start, install/uninstall, **and the per-row "…" menu with
Edit php.ini** (the engine already has `php ini path|reload` — wire the GUI editor sheet to it).

---

## 3. Sites — `SitesView.swift`, `WebsitesPanel.swift`

The Sites tab and the Dashboard websites panel share the **same row + add sheet**.

- **Header:** site count + **search box**; **＋** add button. List is **paginated 10 per page** (`SitePaging` /
  `PageBar`) — prev/next, resets to page 1 on search.
- **Each site row** (`WebsiteRow`): enabled dot · name · domain · **server badge** (nginx/apache) · **php badge** ·
  a row of **circular action buttons**:
  - 🧭 **Open in browser**
  - 📁 **Open folder** (Finder → Windows: Explorer)
  - ✏️ **Edit** (sheet: PHP version, web server, root folder, **Enable HTTPS**)
  - 🔎 **Logs** (per-site access/error log sheet)
  - 📡 **Share publicly** (Cloudflare Tunnel — gives a public URL; green when active)
  - ▶️/⏸ **Start/Stop** (enable/disable the vhost)
  - 🗑 **Delete** (confirm; files kept on disk)
- **Add Site sheet** (`AddSiteSheet`): name + `.tld`; **Type = WordPress / PHP / Others(static)**;
  **PHP version**; **web server** (nginx/apache); **site root = default folder or custom (folder picker)**.
  - *WordPress* → creates DB + downloads WP + pre-writes wp-config.
  - *PHP* → creates a DB named after the site.
  - *Others* → domain only, no DB.

→ **Windows gaps to check:** pagination (10/page), the **6–7 per-row circle actions** (esp. **Open folder**,
**per-site Logs**, **Cloudflare public Share**, Start/Stop, Edit-with-HTTPS), and the **3 site types with
auto-DB + WP download** in the Add sheet.

---

## 4. Databases — `DatabasesView.swift`

**Mac file:** `DatabasesView.swift`.

- **Database servers** list (MariaDB / MySQL / PostgreSQL) as `ServiceRow`s (start/stop/install).
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

→ **Windows gap:** the quick-pick buttons + installed-versions list with Use/Uninstall + default badge (fnm has a Windows build).

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
