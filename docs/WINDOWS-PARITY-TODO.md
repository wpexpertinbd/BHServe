# Windows parity TODO — port these macOS features to the Windows build

> Reverse of `MAC-PARITY-TODO.md`. Features that landed on **macOS** first and should be
> mirrored on the **Windows** build. Keep the update channels separate: **macOS = `v1.6.x`
> tags**, **Windows = `win-v1.0.x` tags**. Leave the `windows/` tree to Windows-Claude — this
> doc is just the spec + the macOS source to diff against.

---

## 1. NEW — Python web apps (Flask / Django / FastAPI / Gunicorn / Uvicorn)  *(macOS: v1.7.0, engine `pysite` verb + app "Python app" type)*

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

## 4. NEW — search + per-page + pagination on the Node & Python tabs  *(macOS: v1.7.4)*

Same search/Show/pagination as the Sites + Databases lists, now on the **Node apps** and **Python
apps** lists. macOS extracted a reusable `ManagedAppsSection` (header: title + count + Show menu +
Search + Add button; paginated `WebsiteRow`s; prev/next + jump-to-page footer) used by both tabs, and
added one shared **"Apps per page — Node & Python tabs"** setting (default 15). Mirror on Windows if
those tabs can hold many apps.
