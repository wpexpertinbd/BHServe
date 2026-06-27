# Mac parity TODO — port these Windows features to the macOS build

> ✅ **#1 + #2 ported to macOS in `v1.6.9`.** (1) HTTPS checkbox (default ON) on the
> Add-site sheet → best-effort `secure` after add, engine prints `secured: https://<domain>`,
> Node uses its own flow. (2) Proactive update: an "Update now / Later" alert on auto-checks
> (launch + 6h poll + window-open) when a window is up, a system notification when launched
> hidden; once per session, gated by the "Automatically check for updates" setting.
>
> ✅ **#3 ported to macOS in `v1.6.11`** — Add-site **requirement guard**: clicking Add computes the
> site type's required services (nginx/Apache + chosen PHP + MariaDB for WP/PHP; nginx + fnm for Node)
> and, if any are **not installed or not running**, prompts "Install & start them, then create?" →
> `AppState.{siteRequirements,missingForSite,ensureSiteServices}`. First-run setup (part B) was already
> covered by the existing **SetupView** (shown when Homebrew or the core stack is missing).
>
> ✅ **#4 checked — macOS is NOT affected.** Mac installs MySQL/MariaDB via **Homebrew**
> (`brew install mysql`/`mariadb`), which fetches bottles from its own infra (ghcr.io) and sets its
> own headers — there is **no direct `dev.mysql.com` download** and **no custom User-Agent** anywhere
> in `engine/bhserve`. The Mac's only direct `curl` fetches are WordPress / WP-salts / ionCube /
> phpMyAdmin / Adminer (default `curl` UA, all accepted). Probe paths are distinct
> (`opt/mariadb/bin/mariadb` vs `opt/mysql/bin/mysql`) so a failed install can't false-succeed off the
> other engine. No fix needed.
>
> ✅ **#5 FIXED in `v1.6.12`** — but the Mac had a *different* (worse) bug than Windows: the salt block
> was injected with `awk -v s="$salts"`, and **BSD awk chokes on the multi-line `-v` value
> ("newline in string") → the salts were silently dropped entirely** (no `AUTH_KEY`/`*_SALT` defines in
> `wp-config.php` at all). Fixed by writing the salts to a temp file and reading them with `getline`
> (no `-v`, no newline/backslash/`$`/`&` escape processing) → all 8 salts land verbatim, `php -l` clean.
> The .NET-specific `$`/`Regex.Replace` corruption never applied to the Mac.

Two features landed on the Windows build that the macOS build (engine `engine/bhserve` + the
Mac app) should mirror. Keep the update channels separate: **macOS = `v1.6.x` tags**, **Windows
= `win-v1.0.x` tags**.

---

## 3. NEW — make users install the core stack before/at site creation *(Windows: win-v1.0.14 Add-site guard, first-run setup)*

**Problem:** users skip the readme, install BHServe, and immediately add a site with **nothing
installed** (no nginx/PHP/DB) → a dead site. Fix it in the app, two layers:

**A. Add-site requirement guard (the enforcement).** When the user clicks **Add site**, compute
what that site type needs and, if anything is missing, block with a prompt to install it:
- **WordPress** → web server + chosen PHP version + a database (mariadb, or mysql if that's the one installed).
- **PHP / static** → web server (+ PHP for php type).
- **Node** → nginx + Node (fnm).
- On confirm: **install the missing pieces AND start them** (also covers an installed-but-stopped
  stack — a site won't serve if nginx/PHP/DB aren't running). On cancel: don't create the site.

**B. First-run setup prompt (proactive).** On first launch, if the **core stack** (web server +
default PHP + database + mkcert) isn't installed, show a one-time **"Welcome — quick setup / Install
now"** dialog that installs + starts the core stack (a fresh install is on the latest version, so
this never collides with the update prompt). If launched hidden (autostart), skip it.

**Windows source to diff against**
- `windows/src/BHServe.Core/Engine.cs`:
  - `MissingForSite(type, php, server)` → required-but-not-installed services (key + friendly label),
    via private `RequiredServices()` + `ServiceLabel()`.
  - `EnsureSiteServices(type, php, server)` → `Install(key)` any missing required + `Start(key)` each
    (idempotent; `Start` no-ops if already running).
  - `MissingCore()` → core stack (`nginx`, `php@default`, `mariadb`, `mkcert`) not installed.
- `windows/src/BHServe.App/Views/SitesPage.xaml.cs` → `Add_Click` calls `EnsureRequirements(...)` before
  creating the site (returns false to abort); the dialog lists missing pieces, installs on confirm.
- `windows/src/BHServe.App/MainWindow.xaml.cs` → `FirstRunThenUpdateCheck()` (waits for XamlRoot, then
  `OfferFirstRunSetup()` else the update check) + `OfferFirstRunSetup()` (prompt → progress dialog →
  `Install("all")` + `Start("all")` → done/partial result).
- Engine already has `Install("all")` = core stack (nginx + php@default + mariadb + mkcert) on both platforms.

---

## 1. "HTTPS" checkbox on the Add-site form  *(Windows: win-v1.0.6, commit `4264530`)*

**Behavior**
- Add an **HTTPS** checkbox to the add-site bar, **default ON**.
- When ticked, after creating the site, call the engine's existing **`bhserve secure <name>.<tld>`**
  to issue the trusted cert + re-render the vhost with HTTPS. (No new engine command — `secure`
  already exists on the Mac engine too.)
- **Best-effort:** if the cert step fails (e.g. mkcert missing), still report the site as added,
  just with a warning — don't fail the whole add.
- **Grey it out for Node apps** — `secure` re-renders a *PHP* vhost and would clobber a Node
  reverse-proxy config; Node has its own add flow.
- Keep the existing per-row "Enable HTTPS" action for enabling later if left unticked.
- Minor: have `secure` print `secured: https://<domain>` so the result UI can show the https URL.

**Windows source to diff against**
- `windows/src/BHServe.App/Views/SitesPage.xaml` — `<CheckBox x:Name="SslBox" Content="HTTPS" IsChecked="True">` in the add-site bar.
- `windows/src/BHServe.App/Views/SitesPage.xaml.cs`:
  - `Add_Click` — reads `SslBox.IsChecked`; after `Engine.SiteAdd(...)` succeeds, runs a 2nd
    `RunCaptured(() => Engine.Secure($"{name}.{Config.Load().Tld}"))`, appends its output.
    Best-effort (site stays added on cert failure).
  - `Type_Changed` — `SslBox.IsEnabled = !isNode`.
  - `ShowResult` — URL regex now prefers `https://`, falls back to `https?://`.
- `windows/src/BHServe.Core/Engine.cs` → `Secure(string domain)` — added `Ok($"secured: https://{domain}")`
  at the end (cert via mkcert + `RenderPhpVhost` + nginx reload; **PHP vhost only** — that's why Node is excluded).

---

## 2. Proactive "update available" notification  *(Windows: win-v1.0.7 launch prompt + tray balloon `222d3c3`, win-v1.0.8 24h re-check `8c7cfd6`)*

**Behavior**
- Today the check is passive. Make it **proactive, gated by the existing "Automatically check for
  updates" setting (default ON)** — if it's off, nothing automatic fires (manual check still works).
- **On launch:** check for a newer release; if found, show an **"Update now / Later"** prompt. If the
  app started hidden (dockless / `LSUIElement` autostart, no window), show a **notification** instead
  and surface the prompt when the user opens it.
- **Re-check every 24h while running** (timer), so a long-running instance notices releases without a
  restart.
- Show once per session; re-check next launch. "Update now" = the existing `.pkg`/`.dmg`
  download+install flow.

**Windows source to diff against**
- `windows/src/BHServe.App/MainWindow.xaml.cs`:
  - fields `_pendingUpdate` (`Updater.Result?`), `_updateTimer` (`DispatcherTimer`, `Interval = TimeSpan.FromHours(24)`).
  - ctor: `_ = CheckForUpdateOnLaunch();` + `_updateTimer.Tick += (_,_) => _ = CheckForUpdateOnLaunch(); _updateTimer.Start();`
  - `CheckForUpdateOnLaunch()` — `if (!Config.Load().AutoUpdate) return;` → `Updater.Check()` → Settings
    `InfoBadge` dot + set `_pendingUpdate` → `AppWindow.IsVisible ? ShowUpdatePromptIfPending() : _tray.ShowBalloon(...)`.
  - `ShowUpdatePromptIfPending()` — `ContentDialog` "Update now / Later" → `Updater.DownloadAndRun(asset)`;
    clears `_pendingUpdate` (once per session); whole show wrapped in try/catch.
  - `ShowFromTray()` — calls `ShowUpdatePromptIfPending()` after showing (surfaces a prompt found while hidden).
- `windows/src/BHServe.App/Services/Updater.cs` — `Check()` → `record Result(bool UpdateAvailable,
  string Latest, string? AssetUrl, string? Notes, string? Error)`; `CurrentVersion`; `DownloadAndRun(url)`
  (downloads installer, launches it, then `App.ForceQuit()`).
- `windows/src/BHServe.Core/Config.cs` — `auto_update` bool, **default `true`** (the gate).

**Related dependency (already on Windows):** the self-update **quit-on-update** fix (`App.ForceQuit()` /
`MainWindow.QuitForUpdate()` + installer `CloseApplications=force` / `RestartApplications=no`, commit
`70be473`, win-v1.0.4). Mac's `.pkg` flow handles its own replace, so only port if the Mac updater has
the same "running app blocks the install" issue.

---

## 4. CHECK — does MySQL install on macOS? (Windows hit a User-Agent 403)  *(Windows fix: win-v1.0.16)*

**The Windows bug:** installing **Oracle MySQL** (not MariaDB) silently failed — it looked like
"Installing mysql done" but nothing landed. Root cause: **`dev.mysql.com`'s CDN returns `403
Forbidden` for a custom `User-Agent`** (Windows sent `BHServe/0.1 (+github…)`; browser UAs also 403),
but it serves **curl's default UA** fine. MariaDB was never affected (archive.mariadb.org ignores UA).
Windows fix = download the MySQL zip with **no custom UA** (let curl use its own).

**Check on macOS — only relevant if the Mac engine downloads MySQL DIRECTLY from `dev.mysql.com`:**
1. Grep the engine: `grep -nE 'mysql|dev\.mysql\.com|brew install mysql' engine/bhserve`.
   - If MySQL is installed via **Homebrew** (`brew install mysql`) → **NOT affected**, brew handles the
     fetch. Nothing to do; note it and move on.
   - If it does a **direct `curl`/`wget` from `dev.mysql.com`** with a custom UA (`curl -A …` /
     `wget -U …` / a `--header 'User-Agent: …'`) → likely the same 403.
2. Reproduce (any shell): `curl -fIL -A "BHServe/…"  '<the mysql zip url>'` vs `curl -fL -o /dev/null '<url>'`
   (no `-A`). **Note:** a `HEAD`/`-I` request can return 200 even when the real GET 403s — test an
   actual GET (a small range, `-r 0-1000`, is enough): `curl -fL -A "<UA>" -r 0-1000 '<url>'` → 403
   means the UA is blocked.
3. Fix: drop the custom UA for the MySQL download only (use the tool's default UA). Don't change the UA
   globally — GitHub API + other hosts may want/need it.

**Windows source to diff against:** `windows/src/BHServe.Core/Downloader.cs` — `CurlTo(url, dest,
string? ua = UA)` / `DownloadToTmp(..., ua)` now accept an optional UA, and `DoInstallDb` passes
`ua: null` so curl uses its own UA. (Also win-v1.0.13: each DB installer validates its OWN engine —
`MysqldExe("mysql")` vs `("mariadb")` — so a failed download can't false-succeed by finding the other
engine; check the Mac install verifies the right binary too.)

---

## 5. CHECK — WordPress wp-config salt injection ($ / special chars)  *(Windows fix: win-v1.0.20)*

**Windows bug:** new WP sites sometimes got a broken `wp-config.php` ("Parse error: syntax error …")
because the secret-key/salt block was used as the **replacement string of .NET `Regex.Replace`**, and
WP salts contain `$` (and sequences `$'`, `` $` ``, `$&`) which .NET treats as substitution patterns →
injected parts of the file. Fixed by a `MatchEvaluator` (literal replacement) in
`Downloader.InstallWordPress`.

**Check on macOS — `engine/bhserve` injects salts via `awk -v s="$salts" '…{printf "%s", s}…'`:**
- `printf "%s", s` prints the salts **literally** (the `%`/`$`/`&` are in the *argument*, not the format
  string), so the macOS path is **likely NOT affected** — no fix needed. Confirm a fresh WP site's
  `wp-config.php` is valid (`php -l`).
- The only residual risk: `awk -v s=…` processes **backslash escapes** in the value. WP salts don't
  normally contain `\`, but if you want to be bulletproof, read the salts via `getline`/a file or an
  env var instead of `-v` so no escape processing happens.

---

## 6. NEW — per-site "Open in code editor" + "Open terminal" + nicer default page  *(Windows: win-v1.0.21 editor/terminal, win-v1.0.22 default page)*

Three small UX features from user feedback. Port to the macOS app.

**A. Per-site "Open in code editor".** Add to each site's row/context menu. Auto-detect an installed
editor and open the **site folder** (don't ask which editor). On macOS, detect in order:
- VS Code (`/Applications/Visual Studio Code.app`, or `code` on PATH) → `open -a "Visual Studio Code" <dir>` or `code <dir>`
- Cursor (`/Applications/Cursor.app`) → `open -a Cursor <dir>`
- Sublime Text (`/Applications/Sublime Text.app`, or `subl`) → `open -a "Sublime Text" <dir>`
- JetBrains (PhpStorm) if easy; else fall back to `open <dir>` (Finder) + a note if none found.

**B. Per-site "Open terminal here".** Open a terminal at the site folder. macOS: prefer **iTerm** if
installed, else **Terminal** — `open -a iTerm <dir>` / `open -a Terminal <dir>` (or a small AppleScript
to cd into the dir).

**C. Nicer default landing page for non-WordPress sites.** Replace the bare "heading + phpinfo()"
placeholder `index.php` with the **branded landing page** (gradient hero + site host + PHP version +
server + document root + "replace this file" hint + biswashost.com link; `phpinfo()` behind
`?phpinfo=1`). **The PHP/HTML file is identical cross-platform — copy it verbatim from Windows.**

**Windows source to diff against**
- Editor/Terminal: `windows/src/BHServe.App/Views/SiteListControl.xaml` (two `MenuFlyoutItem`s) +
  `SiteListControl.xaml.cs` → `CodeEditor_Click` / `Terminal_Click` + `FindEditor()` / `OnPath()`.
- Default page: `windows/src/BHServe.Core/Engine.cs` → `const string DefaultIndexPhp` (the whole page)
  + its write in `SiteAdd` (`File.WriteAllText(.../index.php, DefaultIndexPhp)` when no index exists).
  Linted valid PHP 7.4 → 8.6.
