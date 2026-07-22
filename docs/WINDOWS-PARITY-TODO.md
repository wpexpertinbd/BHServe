# Windows parity TODO — port these macOS features to the Windows build

> Reverse of `MAC-PARITY-TODO.md`. Features that landed on **macOS** first and should be
> mirrored on the **Windows** build. Keep the update channels separate: **macOS = `v1.6.x`
> tags**, **Windows = `win-v1.0.x` tags**. Leave the `windows/` tree to Windows-Claude — this
> doc is just the spec + the macOS source to diff against.

---

## 1. ✅ DONE (win-v1.0.25) — Python web apps (Flask / Django / FastAPI / Gunicorn / Uvicorn)

> Windows: `PySite.cs` (single-process supervisor + venv + reverse-proxy vhost), `bhserve pysite add/start/stop/restart/rm/pip/list`, portable CPython via astral-sh/python-build-standalone (default UA), `python` tool-service + requirement guard, "Python app" Add-site type, and a Python sidebar tab (PythonPage). Engine/CLI/GUI build clean; runtime (download + venv + run a Flask app) to be verified on a real machine.

Same model as the existing **Node app** support: a Python site is **one supervised process**
listening on a port, with **nginx reverse-proxying** the domain to it (`location / → 127.0.0.1:<port>`),
plus trusted HTTPS via mkcert. Optionally a per-project **virtualenv** (`.venv`) so each app pins
its own dependencies. This is intentionally the Node pattern minus the FE/BE split (Python apps are
single-process).

### What the macOS engine added (`engine/bhserve`) — your spec to mirror

A new **`pysite`** verb parallel to `nodesite`:

```
bhserve pysite add <name> --dir <path> --port <n> [--cmd "<run command>"] [--venv yes|no] [--python 3.13]
bhserve pysite {list|start|stop|restart|status|rm|pip} <name>
```

- **`add`** — writes a config JSON (`$BH_HOME/py-sites/<name>.json`: name, domain, dir, cmd, port,
  venv, pyver), creates the venv if `--venv yes`, mkcerts the domain, renders the nginx reverse-proxy
  vhost (reused the node vhost renderer with a `server=python` label so the api snapshot can tell them
  apart), reloads nginx. Default `--cmd` is `python app.py`; the command runs with `$PORT` exported so
  users can write `uvicorn main:app --port $PORT`, `gunicorn app:app -b 127.0.0.1:$PORT`,
  `python manage.py runserver 127.0.0.1:$PORT`, `flask run --port $PORT`, etc.
- **`start`** — supervises the process: `cd <dir>` then run `<cmd>` with `PATH` = venv-bin first
  (so `python`/`gunicorn`/`uvicorn`/`flask` resolve to the venv), `PORT=<port>`, `PYTHONUNBUFFERED=1`;
  writes a pid file (`run/py-<name>.pid`) + log (`logs/py-<name>.log`). `stop` kills the process tree.
- **`pip`** — `pip install -r requirements.txt` into the project venv (separate, like `nodesite npm`,
  because it can be slow).
- **`rm`** — stops, removes the config + vhost (keeps the project files on disk).
- Registered **`python`** as a service (role `python`, formula `python@3.13`, like `fnm`/node it's a
  *tool* — "active once installed", not a daemon). It's **opt-in** — `bootstrap` does NOT install it;
  the Add-site requirement guard installs it when the user adds the first Python app.
- The **api snapshot** site row carries `server:"python"` + `python:true, pyRunning, pyPort, pyDir,
  pyCmd, pyVenv, pyVer` (a `py_api_fields` fragment, parallel to `node_api_fields`).

### What the macOS app added — mirror in the WinUI app

- **Models.Site**: python fields (`python, pyRunning, pyPort, pyDir, pyCmd, pyVenv, pyVer`); a
  tolerant decode (default-absent) like the node fields; `serverKind` returns `"python"`.
- **Add-site sheet**: a **"Python app"** type → folder picker + run-command + port + a
  **"Create a virtualenv"** toggle. After add, start it; tell the user to run "pip install" from the
  row menu.
- **Sites list row**: a `pyActions` view (parallel to `nodeActions`) — open in browser, open folder,
  start/stop/restart, process log, and a `…` menu with **Open in code editor / Open terminal here /
  pip install / Delete**. Teal badge to distinguish from node (green) / php (blue).
- **Requirement guard**: a Python site needs **nginx + python**; the helper now keys off the `type`
  string (`node`/`python`/php/wordpress) instead of a bool.
- **Dedicated "Python" sidebar tab** (parallel to the Node tab) — `PythonView`: an Interpreter
  section (install / show version / update-to-latest of the managed python) + a "Python apps" section
  (Add Python app button → `AddPythonAppSheet` + the list of Python sites). Python sites also show in
  the main Sites list + Dashboard (they're in `realSites`). `activeSites` (menu-bar list) keys Python
  on `pyRunning` (not `enabled`). Engine `start/stop python` are no-ops ("tool, not a daemon"),
  matching `fnm`.

### ⚠️ Windows-specific gotchas (these DIFFER from macOS — handle them)

1. **No Homebrew → you need a portable Python.** Mac uses `brew install python@3.13`. On Windows, do
   what you already do for node/php — a **portable download + extract via Windows' signed
   `curl.exe`/`tar.exe`** (keeps you off the "dropper" AV flag). **Recommended source: the
   `astral-sh/python-build-standalone` releases** (the same self-contained CPython builds `uv` uses) —
   they're relocatable zips that include `pip` and `venv` and need no installer. Avoid the python.org
   *embeddable* zip (no pip/venv) and avoid the full `.exe` installer (UAC + not portable).
   - ⚠️ **Watch the User-Agent 403 you already hit with `dev.mysql.com`** — download with curl's
     **default UA** (no custom `BHServe/…` UA) to be safe; GitHub release assets are fine either way.
2. **venv layout differs:** on Windows the venv puts executables in **`.venv\Scripts`** (not
   `.venv/bin`) and the interpreter is **`python.exe`**. Prepend `<dir>\.venv\Scripts` to `PATH`; pip
   is `.venv\Scripts\pip.exe`.
3. **Process supervision:** mirror your **`php-cgi.exe`** supervisor — spawn the run command (via
   `cmd /c` so shell builtins/`$PORT`-style expansion work; or expand the port yourself and pass
   `%PORT%`), capture the PID, redirect stdout/stderr to `logs\py-<name>.log`, and **kill the whole
   process tree** on stop (gunicorn/uvicorn/django spawn workers — use a Job Object or
   `taskkill /T /F /PID`).
4. **Env vars:** set `PORT` and `PYTHONUNBUFFERED=1` (unbuffered → logs appear immediately).
5. **nginx vhost** is identical to the node reverse-proxy you already render — just point `location /`
   at `127.0.0.1:<port>` and label it `server=python` in the comment so your api snapshot can
   distinguish python from node sites.

### macOS commits to diff against
- Engine: the `pysite` block in `engine/bhserve` (`py_proc_running`, `py_python`, `py_runpath`,
  `py_start_proc`, `py_make_venv`, `py_api_fields`, `pysite_add/start/stop/status/list/rm/pip`,
  `cmd_pysite`), the `python|python@3.13|…|python` service row, the `server=$srvlabel` param on
  `render_nginx_node_vhost`, and the `[ "$srv" = python ]` hook in the api snapshot.
- App: `Models.Site` python fields; `AppState.addPySite/pyStart/pyStop/pyRestart/removePySite/pyPip`
  + the refactored `siteRequirements/missingForSite/ensureSiteServices` (key off `type`);
  `WebsitesPanel.swift` `pyActions`; `NodeSiteUI.swift` `PyLogsSheet`; `SitesView.swift` "Python app"
  type + fields.

---

## 2. ✅ DONE (win-v1.0.24) — throttle the auto update-check (GitHub 60/hr/IP)  *(macOS fix: v1.7.2)*

> Windows: automatic checks now run at most once per 30 min (persisted `run/update-check.txt`, stamped up-front so a 403 backs off); manual check always runs. UA + `win-v*` filtering were already present.

The update checker hits `api.github.com/repos/.../releases/latest` **unauthenticated** — GitHub's
limit there is **60 requests/hour/IP, shared across the whole network**. If your updater fires on
window-open / launch / a periodic timer with **no throttle**, a user reopening the app during testing
burns one request each time and **rate-limits their IP** (you'll see HTTP 403). macOS fix: automatic
checks run **at most once per 30 min** via a persisted `lastUpdateCheckAt` timestamp (stamped up-front
so a 403 also backs off); a **manual** "Check for updates" always runs. Verify the Windows
`Updater.Check()` has the same throttle (and a `User-Agent` header — GitHub requires one).

---

## 3. ✅ DONE (win-v1.0.24) — search + per-page + pagination on the Databases tab  *(macOS: v1.7.3)*

> Windows: search (name/engine) + Show 10/15/20/50/100/All + prev/next on the Databases page; persisted `databases_page_size` (default 15). Inline in DatabasesPage (RenderDbs).

The Databases list got the same **search box + "Show 10/15/20/50/100/All" menu + prev/next +
jump-to-page** footer the Sites tab already has, plus a persisted **"Databases per page" setting**
(default 15) alongside the existing site-list page-size settings. macOS reused its shared
`SitePaging`/`PerPagePicker`/`PageBar`; search filters by **database name or user**. Mirror it on the
Windows Databases page if it shows a long DB list, using whatever paging helper your Sites list uses.

---

## 4. ✅ DONE (win-v1.0.26) — search + per-page + pagination on the Node & Python tabs  *(macOS: v1.7.4)*

> Windows: same search + Show 10/15/20/50/100/All + prev/next on both the Node apps and Python apps lists; shared persisted `apps_page_size` (default 15). Inline RenderApps in NodePage/PythonPage.

Same search/Show/pagination as the Sites + Databases lists, now on the **Node apps** and **Python
apps** lists. macOS extracted a reusable `ManagedAppsSection` (header: title + count + Show menu +
Search + Add button; paginated `WebsiteRow`s; prev/next + jump-to-page footer) used by both tabs, and
added one shared **"Apps per page — Node & Python tabs"** setting (default 15). Mirror on Windows if
those tabs can hold many apps.

---

## 5. Rich tray / menu-bar flyout (live metrics + sites + tools)  *(macOS: BHServeApp.swift MenuBarView)*  — **medium, polish**

The Windows tray (`windows/src/BHServe.App/TrayIcon.cs`, `ShowMenu()`) is a plain Win32
`Shell_NotifyIcon` context menu with only **Open BHServe / Start all / Stop all / Restart all / Quit**
(wired in `MainWindow.xaml.cs`). The macOS menu-bar is a rich **flyout** (`macos/Sources/BHServe/BHServeApp.swift`
`MenuBarView`, ~L79–168): live **CPU / RAM / Disk bars + a CPU sparkline**, a **running-services line with a
status dot**, the **first-5 active sites as clickable open-links**, and **Tools** (phpMyAdmin / Adminer /
Mailpit) quick-open. **`windows/src/BHServe.Core/SystemMetrics.cs` already computes the metrics** — they're
just not surfaced in the tray. Port a small WinUI/Win32 popup window anchored to the tray icon with the same
four sections. Not urgent (the dashboard already shows all of this) — pure convenience parity.

## 6. PHP-site **Access**-log viewing in the GUI  *(macOS: WebsitesPanel.swift SiteLogsSheet)*  — **low**

Windows' per-site log button (`SiteListControl.xaml.cs` `Logs_Click`) opens **only `{name}-error.log`**;
there's no way to view the **access** log from the app. But `NginxConfig.cs` already writes
`{name}-access.log` for every PHP site — the file exists, it's just unviewable. macOS `WebsitesPanel.swift`
`SiteLogsSheet` has an **Error / Access segmented picker** reading `<name>-error.log` and `<name>-access.log`
(reloadable). Add the same Access option to the Windows PHP-site log view. (The `LogTail which=fe/be` path is
Node/Python process logs — separate.)

## 7. Settings ▸ "List sizes" — add Databases + Node/Python boxes  *(macOS: SettingsView.swift)*  — **low, centralization-only (NOT a capability gap)**

macOS Settings exposes **four** per-page defaults (Dashboard / Sites / Databases / Node&Python) via
`appState.dbsPerPage` + `appsPerPage`. Windows `SettingsPage.xaml` shows only **Dashboard + Sites**
NumberBoxes. **This is UI placement only, not a missing feature:** `Config.cs` already carries
`DatabasesPageSize` + `AppsPageSize`, and users can already change AND persist both via the inline **"Show N"**
dropdown on `DatabasesPage` / `NodePage` / `PythonPage`. So it's just centralizing those two into the Settings
"List sizes" card for discoverability — do it only if convenient.

---

> **Also checked (no action — Windows already covered it):** the macOS v1.7.7/v1.7.8 **ionCube** work
> (loader-ordering: ionCube before opcache; arch auto-detect arm64/x86-64; per-version resilience + dynamic
> "available versions" message). Windows solved its *own*, different ionCube problems in **win-v1.0.58–61**
> (the loader DLL missing from the real FS + a polluted-env root cause + JIT-crash fix + self-heal), so the
> macOS engine-specific fixes don't port. Verified against `windows/` source — nothing to do here.

---

## 8. ✅ DONE — Windows shipped `win-v1.0.62`, Linux shipped `linux-v1.0.43` (2026-07-22)

> **Windows verify (real box):** builds clean; `hosts-remove` verb confirmed present in
> `bhserve-elevate`; functional pass — add `api`+`shop` (hosts entries elevated OK), `list`, cross-site
> claim of `api.<first>.test` REJECTED, `rm api` leaves `shop` + removes the hosts line; cert reissue
> verified live: SAN = canonical+both aliases, `Invoke-WebRequest` (Windows trust store) → HTTP 200
> TRUSTED on all three hosts. ⚠️ One testing gotcha for future notes: `bhserve secure <name>` (bare
> site name) mints a junk cert for the literal name and the alias-reissue then correctly no-ops —
> `secure` takes the DOMAIN (`<name>.test`). Consider a future guard that resolves a bare site name.
> **Linux verify (WSL2 Ubuntu):** same engine flow all-pass (add/list/rm, conflict rejection,
> multi-SAN reissue, HTTPS 200 on aliases via curl --resolve, api snapshot emits `aliases`);
> `pages.py` syntax-clean, subdomain dialog + aliases pill + PR #2 "Open nginx/Apache config" item all
> present. Note: the PR #2 opener labels OLS-backed sites as "nginx config" (it opens the nginx front
> proxy conf — technically correct; a future nicety could open the OLS vhconf too).

*(original spec kept below for reference)*

## 8-spec. Subdomain / alias management — Windows **+ Linux** code is ALREADY on master (verify + ship both)  *(macOS: v1.7.9; community PR #3 by @plusemon)*

**This one is different: you don't need to port anything — @plusemon's PR #3 (merged to master 2026-07-22)
already includes a full Windows implementation.** Your job is to **build-verify + functionally test it on a
real Windows box, then ship it** in a `win-v1.0.x` release. macOS shipped it as **v1.7.9**; the engine flow
(`bhserve site subdomain {list|add|rm}`) is verified working there (add/list/rm, cross-site conflict
rejection, SSL cert reissued to cover aliases). Aliases are stored **in the vhost `server_name` line** (no
separate store) so they survive re-renders.

**Where the Windows code already lives (review these):**
- `windows/src/BHServe.Core/Engine.cs` — `SiteSubdomains()` / `SiteSubdomainAdd()` / `SiteSubdomainRemove()`
  (~L749–800) + helpers `NormalizeAlias` / `VhostDomains` / `VhostAliases` / `AllDomains` /
  `RewriteServerName` / `ReissueSiteCertificateIfSecure` / `RestartOrReloadAfterAliasChange`.
- `windows/src/BHServe.Core/{NginxConfig,NodeSite,PySite}.cs` — pass aliases into the vhost `server_name`.
- `windows/src/BHServe.Cli/Program.cs` — the `case "subdomain":` CLI subcommand (~L96).
- `windows/src/BHServe.App/Views/SiteListControl.xaml{,.cs}` — the "Manage subdomains…" dialog + alias badge.

**⚠️ Windows-specific things to VERIFY (plusemon likely tested on one platform, not Windows):**
1. **It compiles.** Build `windows/BHServe.sln` — a community PR may have C# errors that only surface on a
   real toolchain. (The logic reads clean, but confirm.)
2. **Hosts-file entries (Windows has no dnsmasq).** Add calls `EnsureHosts(host)`; remove calls
   `Hosts.Remove(host)` and falls back to `Elevation.Run("hosts-remove", host)`. **Confirm your
   `bhserve-elevate.exe` actually implements a `hosts-remove` verb** — if it doesn't, removing a subdomain
   leaves a stale `hosts` line (and add needs the per-subdomain hosts entry to resolve at all).
3. **SSL reissue with multiple SANs.** `ReissueSiteCertificateIfSecure` re-runs mkcert with canonical +
   aliases. Verify Windows mkcert issues a multi-name cert and `RestartOrReloadAfterAliasChange` does a full
   nginx **stop→start** on cert change (it does in the code — confirm it takes effect).
4. **Functional pass:** add `api` + `shop` to a site → both resolve; `list` shows them; a second site
   claiming `api.<first>.test` is **rejected**; `rm api` leaves `shop`. Then ship.

Minor style note (not a bug): `SiteSubdomainRemove` uses `!aliases.RemoveAll(...).Equals(1)` to detect
"exactly one removed" — works, but `!= 1` would read clearer.

### 🐧 Linux — same PR, also on master (verify + ship in a `linux-v1.0.x`)

The **same PR #3** added the Linux GTK side, and the shared `engine/bhserve` gives Linux the
`bhserve site subdomain {list|add|rm}` CLI **for free** (already verified working on macOS — same engine).
So on Linux you only verify the **GUI + a functional pass**, then ship:
- `linux/app/bhserve/pages.py` — `site_subdomains(win, s)` (Adw.MessageDialog add/remove, ~L188), the
  **"Manage subdomains…"** context-menu item (~L148), and the **aliases-count pill** on site rows (~L175).
  It reads `s.get("aliases")` from the `api` snapshot — confirm the engine's api emits `aliases` per site
  (it does on macOS; Linux uses the same engine).
- **Also ship @plusemon's PR #2** (Linux-only, already merged): the **"Open nginx/Apache config"** context
  item in `pages.py` (~L153) — `os` is imported, path is `~/.bhserve/{nginx|apache}/sites/<name>.conf`,
  opens via the editor. Trivial; just include it in the same release.
- **Functional pass (GTK, on a real Ubuntu box):** add `api` + `shop` → both resolve + the pill shows "2
  aliases"; a second site claiming `api.<first>.test` is **rejected** (engine enforces this); `rm api`
  leaves `shop`; HTTPS still valid (cert reissued with the aliases). Then cut `linux-v1.0.x`.

> Cross-ref: this note lives in the Windows doc but the **same Claude owns `linux/`** — the Linux delta
> layer + handoffs are in `linux/engine/DELTAS.md`. Subdomain needs **no** Linux engine override (shared
> engine covers it); it's GUI-verify-and-ship only.

**UX (both platforms):** the Subdomains dialog applies each Add immediately, so give it an obvious
**Close/Cancel** affordance — the macOS sheet initially only had a "Done" button which read like a commit
(fixed in **v1.7.10**: added an ✕ in the header + Escape-to-close + renamed "Done"→"Close"). Make sure the
Windows `ContentDialog` / Linux `Adw.MessageDialog` clearly let a user who's just looking dismiss without
feeling they added something.
