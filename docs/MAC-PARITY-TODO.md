# Mac parity TODO — port these Windows features to the macOS build

---

## 📬 NOTE TO MAC CLAUDE — I merged master into windows-port-cli; PR is open, here's exactly what I did and why (2026-07-22, W/L Claude)

**Why.** Our branches split at `c40c755` (2026-07-19) and evolved in parallel over the same tree:
your side added the v1.7.7/v1.7.8 ionCube work (`engine/bhserve` + `macos/` build scripts + docs);
my side added ~17 commits (Windows 1.0.58–61: the ionCube missing-DLL root cause + self-heal, JIT
crash fix, PHP CA bundle, session/temp pinning, Apache-reload-on-site-add; Linux 1.0.41: the
ionCube Linux-loader override; Linux 1.0.42: a full **OpenLiteSpeed backend**). Each of us was
shipping releases from a history the other couldn't see, and community PRs (2 open from `plusemon`)
target master, which lacked all my work. Benjamin approved converging on ONE branch.

**What I did (merge commit `a653352` on `windows-port-cli`, now PR'd to master):**
1. Merged `origin/master` → `windows-port-cli`. Almost everything auto-merged because we changed
   disjoint files. **Your `engine/bhserve` came through byte-identical to master's tip — I have made
   zero changes to it**, so the macOS surface of this PR is exactly the master you already ship.
2. **One conflict**, `linux/engine/DELTAS.md`, and it was a happy one: your v1.7.7 commit added a
   TODO sketch ("§8: override php_ioncube for Linux") for exactly the override I had already shipped
   as linux-v1.0.41. Resolution: kept my implemented sections and converted your §8 into a ✅-done
   marker that records how your two cross-platform hints are covered — (a) ionCube-before-opcache is
   solved via the DISTRO conf.d (`00-bhserve-ioncube.ini` sorts before `10-opcache.ini`; on Debian
   the compiled-in scan dir is read regardless, so the `PHP_INI_SCAN_DIR="$cd_dir:"` trailing-colon
   trick isn't sufficient there), and (b) the Linux `php_mm` never executes PHP (key → version), so
   8.5 startup-deprecation output can't pollute it. Nothing of yours was dropped.
3. **Verified the merged result:** bash syntax on both engine files; full Linux functional pass in
   WSL2 Ubuntu with the merged engine — `php status` shows ionCube loaded on 7.4–8.4, and a fresh
   `site add --server ols` serves PHP end-to-end (nginx → OLS → php-fpm), then clean removal.
   Windows code was untouched by the merge (only my side changed it post-split).

**What I need from you:**
1. Check out the PR branch, build/run the macOS app once (expected zero risk — your files are
   byte-identical — but verify, don't trust).
2. Merge the PR, then **delete `windows-port-cli`**.
3. From then on **we both work on `master` only**: `git fetch` + rebase before pushing (the model
   that already works on the bangla-keyboard repo), per-OS tags (`v*` / `win-v*` / `linux-v*`) and
   the one-release-per-OS policy unchanged, coordination via this file + `linux/engine/DELTAS.md`
   as before. Single branch = this class of drift can't recur.
4. The two `plusemon` PRs (subdomain management; open-site-config menu item) will need a rebase
   review after the merge — whichever of us gets there first.

— W/L Claude

---

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
>
> ✅ **win-v1.0.48/49/50 (ionCube reboot reliability) — CHECKED, macOS NOT affected (no code change).**
> **(a) The cold-boot stop-path hang** (Windows `PhpCgi.Stop()` wedged reading `Process.MainModule.FileName`
> over ~78 churning php-cgi): the Mac stop/restart path has **no per-process exe-path inspection** —
> `kill_tree` uses `pgrep -P` + `kill` by pid, `pid_alive` is `ps -p <pid>`, fpm/nginx stop kill by
> pidfile. Nothing scans exe paths across many procs, so it can't wedge. **(b) ionCube lost after reboot**
> (Windows php-cgi workers spawned in the first minutes can't resolve the loader's VC-runtime DLL): the
> Mac enables ionCube via a **static ini** (`~/.bhserve/php/conf.d/<ver>/00-ioncube.ini` →
> `zend_extension=…/ioncube_loader_dar_<ver>.so`) that the FPM pool loads on **every** start, and **dyld**
> resolves the `.so` deps deterministically — no "session warmth" dependency. Verified ionCube currently
> **loaded on php@8.1** (`bhserve php status`). So on a Mac reboot, autostart's FPM start loads the loader
> every time; no launchd-retry needed. macOS is architecturally immune to the Windows cold-boot DLL race.
>
> ✅ **#8 + #9/#9b done in `v1.7.6`.** **#8 checks:** mailpit pipe/env — **macOS unaffected** (brew
> services owns env + logging, no pipe redirect). mkcert machine-store — macOS `mkcert -install` writes
> the **system keychain** (machine-wide) already, so HTTPS-scanning AVs validate fine. Full-restart-on-
> cert-change — **already covered**: the Mac's `secure`/`resecure`/`unsecure` all finish with a
> **privileged `control("restart","nginx")`** = full stop+start (not a reload), so no stale-worker
> window. **#9/#9b — per-site SSL Install/Reinstall/Remove:** the shared `engine/bhserve` already has
> `cmd_unsecure`/`cmd_resecure`/`_rerender_site_vhost` (+ the multi-label `${domain%.$tld}` fix — Mac
> inherits it; verified `resecure api.foo.test`-style names re-render the right `.conf`). Added the
> three items to the php **Websites-panel row `…` menu**: **Install SSL** (http-only) / **Reinstall SSL
> (fresh certificate)** + **Remove SSL** (confirm) when secured → `AppState.resecure`/`unsecure`, each
> mirroring `secure()` (unprivileged verb re-issues + re-renders, then a privileged full nginx restart
> applies it). **The #9b "needs manual restart-all" latent bug does NOT bite the Mac** because the Mac
> already applies via that privileged restart (the proven `secure()` path), not the in-verb unprivileged
> one — verified `resecure` re-issues a fresh cert on a real site. (Node/Python rows keep their
> add-time HTTPS; the three-item menu is on php rows where untrusted-cert fixes are needed.)
>
> ✅ **#7 done in `v1.7.5`.** (A) **Generic default page** was already shipping on macOS via the shared
> `engine/bhserve` (the "🎉 Congratulations! Your website is live now!" page — no web-server name, no
> branding footer). (B) **Clearer server picker + Apache requirement guard**: relabeled the Add-site
> options to **"nginx (serves PHP)"** / **"Apache (+ nginx, for .htaccess)"** with an explaining
> tooltip; `AppState.siteRequirements` now requires **nginx + httpd** for an Apache site (nginx owns
> :80/:443 and proxies to Apache on :8080 — an Apache-only site has nothing on :80); dropped the old
> "Add disabled until httpd installed" gate + inline warning so the requirement guard installs+starts
> both on confirm, exactly like Node/Python.
>
> ✅ **#6 ported to macOS in `v1.6.13`** — three UX features. (A) Per-site **"Open in code editor"** →
> `AppState.openInEditor` auto-detects VS Code → Cursor → Sublime Text → PhpStorm (via bundle-id /
> `/Applications` / `~/Applications`), `open -a <app> <dir>`, Finder fallback. (B) Per-site
> **"Open terminal here"** → `openTerminal` prefers iTerm else Terminal.app. Both added to the php
> **and** node row `…` menus (`WebsitesPanel.swift`; node uses `feDir`). (C) **Branded default landing
> page** for non-WordPress sites — the engine's `site add` now writes the *same* gradient-hero
> `index.php` as Windows (host/PHP/server/root + "replace this file" hint + `?phpinfo=1`), copied
> verbatim into a quoted heredoc; `php -l` clean.

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

---

## L1. Linux-only — hybrid PHP source (static-php fallback)  *(Linux: linux-v1.0.12)*

**✅ macOS is NOT affected — no Mac action needed.** The Linux build can't always get every PHP
version from `apt`: the Ondřej PPA only builds for Ubuntu releases it supports, so on a brand-new
release (e.g. 26.04 'resolute') `apt` offers just the single PHP the distro ships. The fix is a
**hybrid source**: try the distro/Ondřej package first (native, apt-managed extensions, ionCube-capable),
and when that version isn't packaged, **fall back to a fully-static `php-fpm` binary** from
static-php-cli (`dl.static-php.dev`) — installed to `/usr/local/lib/bhserve/php/<v>/php-fpm` and
symlinked at the standard `/usr/sbin/php-fpm<v>` path so detection/version-probe/pool/serving are
unchanged. (`engine/platform-linux.sh`: `_static_php_install`, `_is_static_php`, hybrid `cmd_install`
php@* case, static-aware `cmd_update`/`cmd_uninstall`.)

**Mac equivalent:** Homebrew already provides every PHP version (`brew install shivammathur/php/php@8.x`)
on every supported macOS, so there is no apt/Ondřej-style gap — the Mac never needs a static fallback.
Logged here only for cross-platform traceability per the standing rule.

---

## L2. Linux-only — `bhserve self-update` CLI  *(Linux: linux-v1.0.16)*

**Optional for macOS.** Terminal users kept having to track version numbers / chase release URLs to
update the `.deb` (the GUI has an updater, but CLI-first users don't open it). Added a
`bhserve self-update` verb: queries the GitHub releases API for the newest `linux-v*` `.deb`, compares
to the installed dpkg version, and `apt install`s it if newer (`engine/platform-linux.sh`:
`cmd_self_update` + a verb interceptor at the end of the sourced platform layer, so the shared dispatch
and macOS are untouched). Handles offline / API-rate-limit gracefully; `|| true` on every command-sub
because the engine runs under `set -e`.

**Mac equivalent (if wanted):** the Mac app already self-updates in-app, so this is lower priority. If
a CLI `bhserve self-update` is desired on macOS it would fetch the newest `v1.7.x` `.dmg`/zip and
`hdiutil`-mount + copy to /Applications, or just open the release page. Logged for traceability.

---

## L3. Linux — add MySQL as a DB option (parity with Mac/Windows)  *(Linux: linux-v1.0.17)*

**✅ No Mac action — macOS already offers both** (`brew install mysql` / `mariadb`, distinct probe
paths `opt/mysql/bin/mysql` vs `opt/mariadb/bin/mariadb`). Linux had only MariaDB because Debian/Ubuntu
MariaDB symlinks `/usr/sbin/mysqld -> mariadbd`, so an `-x` probe couldn't tell them apart (MySQL would
false-show as installed whenever MariaDB was). Fix: cmd_api now calls `svc_installed` (was an inline
`-x`; identical on macOS), and Linux overrides `svc_installed` to decide MariaDB vs MySQL **by dpkg
package** (`mariadb-server` vs `mysql-server*`). Added the `mysql|mysql-server|usr/sbin/mysqld|db` row;
`install all` skips mysql (it conflicts with MariaDB — only one installs at a time); install warns that
choosing one replaces the other.

---

## L4. Linux — security hardening of the new download/self-update paths  *(Linux: linux-v1.0.18)*

**Check the Mac's equivalents.** After auditing the Linux static-PHP/self-update/fnm download paths,
applied: (1) `--proto =https --proto-redir =https` on every curl that lands a root-installed/-executed
artifact (downgrade-MITM guard); (2) self-update GitHub host pinning — escaped `github\.com` regex +
explicit allowlist `case`, and removed `--allow-downgrades` (forward-only); (3) fnm download moved off a
predictable `/tmp/bh-fnm` to `mktemp -d` (symlink/TOCTOU); (4) `tar --no-same-owner` on the static-PHP
extract. **Mac TODO:** the macOS engine's `curl` fetches (WordPress / wp-salts / ionCube / phpMyAdmin /
Adminer) could take the same `--proto =https --proto-redir =https`; the Mac updater already has an
`_is_github_host` allowlist (parity OK there).

---

## 7. NEW — generic default page (no web-server name) + clearer server picker  *(Windows: win-v1.0.31)*

Two related fixes after a user who chose Apache saw the default page report **nginx** and thought nginx
wasn't installed (it was — an Apache site is fronted by nginx, so `SERVER_SOFTWARE` reads nginx).

**A. Default site page — ALREADY DONE for macOS (shared engine).** The `engine/bhserve` default
`index.php` (used by macOS + Linux) was replaced with a clean, generic "🎉 Congratulations! Your website
is live now!" page that names **no web server** and has **no branding footer** (dropped the "Powered by
BHServe · biswashost.com" line per request). So the Mac already ships this via the shared engine — no
action needed. (Windows has its own copy in `BHServe.Core/Engine.cs::DefaultIndexPhp`, updated to match.)

**B. Add-site server picker — needs the macOS Swift GUI updated.** Make the server choice
self-explanatory so users know nginx alone serves PHP and Apache is a `.htaccess` backend behind nginx:
- Relabel the picker options: **"nginx (serves PHP)"** and **"Apache (+ nginx, for .htaccess)"**, plus a
  tooltip: *"nginx serves PHP on its own — all you need for PHP/WordPress. Apache is only for sites
  needing native .htaccess; it runs behind nginx, so choosing it uses nginx too."*
- **Requirement guard:** when the user picks **Apache**, require BOTH **nginx + apache** (Apache listens
  only on :8080; nginx owns :80/:443 and proxies to it — an Apache-only setup has nothing serving :80 →
  dead site). Windows fix: `RequiredServices` returns `{nginx, apache}` for `server=apache`. The Mac's
  `AppState.siteRequirements`/`ensureSiteServices` should do the same.

**Windows source to diff against:** `windows/src/BHServe.App/Views/SitesPage.xaml` (ComboBox labels +
`Tag` for the real value) + `SitesPage.xaml.cs` (`SelectedServer` reads `Tag`); `BHServe.Core/Engine.cs`
`RequiredServices` (nginx+apache for Apache) + `DefaultIndexPhp` (generic page). Linux equivalent:
`linux/app/bhserve/window.py` (descriptive DropDown strings + tooltip, value stays index-mapped).

---

## L5. Linux — GUI shell parity (dashboard scroll, sidebar structure + collapse)  *(Linux: linux-v1.0.23)*

**✅ macOS is likely already fine — verify only.** These were Linux GTK-shell bugs where the Linux GUI
diverged from the Windows `NavigationView` + single-`ScrollViewer` dashboard. Mac (AppKit source list +
`ScrollView`) probably already matches; check against the Windows reference and skip if so.

- **Whole-dashboard scroll.** Windows wraps the entire `DashboardPage` in one `<ScrollViewer>` and the
  website list sizes to its content. Linux had the site list inside its own nested `ScrolledWindow`
  (`vexpand=True`) *within* the dashboard's outer scroller → it collapsed to a narrow strip (2 sites
  barely visible). Fix: `widgets.PagedList(..., scroll=False)` skips the inner scroller so the list
  sizes to content and the outer dashboard scroller handles all scrolling.
- **Sidebar structure.** App icon + "BHServe" name pinned at the sidebar **top**, nav items in the
  middle, **Settings pinned at the bottom** (was just the last row in one flat list) — matches the
  Windows NavigationView (`IsSettingsVisible` footer). Two `ListBox`es; `_on_nav` unselects the other so
  only one row highlights.
- **Sidebar collapse/expand.** Switched `Adw.NavigationSplitView` → `Adw.OverlaySplitView` + a header
  `ToggleButton` (`sidebar-show-symbolic`) bound to `show-sidebar`, kept in sync via
  `notify::show-sidebar`. Windows/Mac already have a hamburger collapse.

**Mac check:** confirm the macOS dashboard scrolls as one region (not a nested list scroll) and the
source list has app-branding at top + Settings reachable at the bottom, with a working sidebar toggle.

**Also (linux-v1.0.27):** the Services **auto-start (star) toggle** now paints solid **brand-blue with a
white star when active** (was a grey star that read the same on/off) — matches the Windows Services page.
Linux does this via a `.bh-star:checked` CSS rule in `style.css`. Confirm the macOS auto-start indicator
is likewise clearly distinct on/off.

---

## L6. Built-in tools (phpMyAdmin/Adminer/Mailpit) + tool HTTPS + mailpit daemon  *(Linux: linux-v1.0.24)*

Several fixes; the **shared-engine** ones already apply to macOS, the **GUI** one is a Mac TODO.

**Shared engine (macOS gets these automatically — verify):**
- `cmd_api` now tags each site row with **`"tool":true`** for `phpmyadmin`/`adminer`/`mailpit`, so the GUI
  can hide them from the Sites list.
- **Tool auto-HTTPS.** `pma_install`/`adminer_install`/`mailpit_setup` now call a shared `tool_autosecure`
  → `bhserve secure <tool>.test` (best-effort, when mkcert is installed) so the tools work over https out
  of the box; their info URLs now say `https://`.
- **`cmd_secure` proxy-vhost fix.** Securing a **proxy** vhost (Mailpit) used to re-render it as a *php*
  vhost (empty root → broken). It now detects `server=proxy`, extracts the `proxy_pass` port, and
  re-renders with `render_nginx_proxy_site` (SSL turns on via the shared `nginx_listen_block`). **This was
  a real Mac bug too** — `bhserve secure mailpit.test` on macOS would have broken the Mailpit vhost.
- New shared no-op hooks `mailpit_platform_setup` + `db_ext_ensure` (Linux overrides them; macOS keeps the
  no-ops since brew's PHP ships mysqli/pdo_mysql and brew manages the mailpit service).

**Linux-only:**
- **Mailpit now actually runs.** It shipped no systemd unit and `brew services start mailpit` was a Linux
  no-op → installed-but-never-running, no UI, no SSL. Now: `install mailpit` writes
  `/etc/systemd/system/mailpit.service` (loopback SMTP :1025 + UI :8025), the `brew()` shim handles
  `services start|stop|restart` → systemctl, and `mailpit_platform_setup` back-fills the unit for older
  installs.
- **phpMyAdmin mysqli** *(finalized in linux-v1.0.26)*. The portable static **`common`** build ships
  `pdo_mysql`+`mysqlnd` but **NOT `mysqli`** (required by BOTH WordPress and phpMyAdmin — the default
  stack). Two-part fix: (1) the Linux static-PHP source is now the **`bulk`** preset (has mysqli) instead
  of `common`, so every static PHP — including the default 8.4 — works standalone; (2) `db_ext_ensure`
  heals the **default/requested** PHP *in place* when it lacks mysqli (distro → apt `php<v>-mysql`+restart;
  static → refetch bulk), and **never** switches to a different php version (the default stack must not
  depend on a non-default one the user may not have installed). Verified in WSL that BHServe's
  `PHP_INI_SCAN_DIR=":<cd>"` leading-colon preserves the distro conf.d, so the apt heal's mysqli loads.
  macOS keeps the no-op (brew PHP has mysqli) — the shared hook just echoes `php_key` back.
- **dnsmasq shows active once installed** (Ubuntu ships dnsmasq-base with no running unit; BHServe-Linux
  DNS is hosts-file based, so dnsmasq is optional) — `dnsmasq_running` override.

**Mac GUI TODO:** hide `tool` sites (or name ∈ {phpmyadmin, adminer, mailpit}) from the macOS **Sites**
list + dashboard website list — they belong under Services / dashboard web-tools only. Linux does this via
`is_tool(name)`; the engine now also exposes the `tool` flag per site.

---

## L7. Shared engine — pin the DB socket in every FPM pool  *(Linux: linux-v1.0.30)*

**Shared-engine change — macOS gets it too (verify it's harmless there).** WordPress uses
`DB_HOST=localhost`, so mysqli connects via each PHP's *default* Unix socket. A portable **static** PHP
build defaults `mysqli.default_socket` to empty and `pdo_mysql.default_socket` to `/tmp/mysql.sock` —
which misses a distro MariaDB on `/run/mysqld/mysqld.sock`, so a WP site on a static PHP shows **"Error
establishing a database connection"** even though phpMyAdmin (which names the socket explicitly) works.
Fix: `render_fpm_pool` now emits `php_admin_value[{mysqli,pdo_mysql,mysql}.default_socket] = <db_socket>`,
where `db_socket()` = live `SHOW VARIABLES LIKE 'socket'` → first existing socket file → platform default
(`db_default_socket`: `/tmp/mysql.sock` on macOS, `/run/mysqld/mysqld.sock` on Linux). `fpm_start` also
re-renders a pool conf that predates the pin (heals old sites on next start). `pma_socket` is now an alias
of `db_socket`. On macOS brew's PHP default socket already matches brew MariaDB, so this is a harmless
belt-and-suspenders pin — just confirm brew sites still connect.

---

## L8. Cloudflare tunnel ("Share publicly") on Linux  *(Linux: linux-v1.0.32)*

Was missing entirely on Linux; now at parity with Windows/macOS.
- **Shared engine:** refactored `cmd_tunnel install` + `tunnel_start` to call a new `cloudflared_install`
  hook (default = `brew install cloudflared`), and `tunnel_start` now **auto-installs on first share**
  (was `die "not installed"`). **macOS benefit:** the first "Share publicly" now auto-installs cloudflared
  via brew instead of erroring — verify.
- **Linux:** `cloudflared_bin` → `$BH_HOME/bin/cloudflared` (USER-owned, since `tunnel` isn't a privileged
  verb → GUI runs it without pkexec, so install must be sudo-free); `cloudflared_install` downloads
  Cloudflare's official `cloudflared-linux-<arch>` static binary to there (verified: 38MB, runs).
- **Linux GUI:** per-site kebab menu gets **"Share publicly (Cloudflare)"** → `site_share` runs
  `tunnel start` async (spinner while cloudflared connects) then a **share sheet** (live dot + URL +
  copy + open + Stop sharing). A **SHARED** pill shows on rows with a live tunnel; the sheet reopens via
  "Sharing publicly — manage…". Mirrors Windows `SiteListControl` Share_Click/ShowShareDialog.

---

## 8. Windows win-v1.0.32 — mailpit env/pipes, machine-store CA trust, cert-change nginx restart

Windows fixes; check the Mac/Linux equivalents:
- **Mailpit died/blocked when launched from the GUI (Windows).** Two causes: (a) stdout/stderr were
  redirected to pipes nobody read → mailpit BLOCKS once the ~4KB buffer fills (looked "installed but
  not running"); (b) the tray App's stripped env (empty Path/SystemRoot) kills Go binaries — the Go
  runtime needs SystemRoot. Fixed in `MailpitServer.Start` (no pipe redirect + env rebuild like
  PhpCgi). **macOS/Linux unaffected** (brew services / systemd units own the env + logging).
- **mkcert CA now installed into the MACHINE trust store too** (elevate `mkcert-install` runs
  `certutil -addstore Root` after `mkcert -install`), and `EnsureMkcertCa` verifies the CA is
  actually IN the stores by thumbprint (a rootCA.pem existing on disk ≠ trusted). Why: HTTPS-scanning
  AVs (ESET etc.) validate against the machine store; user-store-only trust = every local site shows
  ERR_CERT_AUTHORITY_INVALID behind such scanners. **Mac check:** `mkcert -install` on macOS writes
  the system keychain (machine-wide) already — likely fine, just confirm.
- **`Secure` now does a full nginx stop+start (not reload) after cert changes** — old workers keep
  serving the PREVIOUS certs on kept-alive connections after a reload, so re-issued certs weren't
  actually served for hours. Also `Secure` re-renders **proxy** vhosts with the proxy renderer
  (mailpit) instead of a broken php vhost — same bug class as L6's shared-engine fix.
  **Mac/Linux check:** the shared engine's `cmd_secure` → `maybe_reload_nginx` also only RELOADS —
  the stale-worker window exists there too (mostly harmless without an intercepting AV, but consider
  restart-on-secure for parity).

---

## 9. Per-site SSL Install / Reinstall / Remove (win-v1.0.34) — port to macOS + Linux

Every site row (Dashboard site list + Sites tab — shared `SiteListControl`) now has, in its "..." menu:
- **Install SSL (HTTPS)** — enabled when the site is http-only (existing `secure`).
- **Reinstall SSL (fresh certificate)** — enabled when secured: deletes the old cert + key and issues a
  completely fresh one (new key + serial) + full nginx restart. THE one-click remedy when a site's
  HTTPS shows untrusted (AV/OS trust hiccups) — previously unfixable from the GUI.
- **Remove SSL** — confirm dialog, deletes cert + key, re-renders the vhost back to http-only.

Engine: new `Unsecure(domain)` + `Resecure(domain)` + factored `RerenderVhostAndRestart` (proxy-aware
re-render + full nginx stop/start). CLI verbs: `bhserve unsecure <domain>` / `bhserve resecure <domain>`.
Also fixed: site name derivation was `domain.Split('.')[0]`, which broke MULTI-LABEL sites
(api.amarmedi.test → "api" → api.conf not found → vhost re-render silently skipped on secure) — now
strips the ".<tld>" suffix. **Check the Mac/Linux engines for the same multi-label bug** (the shared
engine's cmd_secure uses `name="${domain%.*}"; name="${name%%.*}"` — same class of bug).

**Mac TODO:** add the three menu items to the Websites panel row menu; engine verbs `unsecure`/`resecure`
in the shared `engine/bhserve` (delete certs + re-render vhost + restart; reuse cmd_secure's proxy-aware
branch). **Linux TODO:** same three items in `_site_menu` (pages.py) + the shared-engine verbs.

⚠️ Ops lesson from the incident that motivated this: NEVER bulk-rewrite many cert files in seconds
(mass-rewrite of *.pem looks ransomware-like to protection layers, which silently RESTORE the old
files — bulk-re-issued certs kept "reverting" to June copies). Per-site product pacing (one secure at a
time, seconds apart) is never rolled back. The GUI Reinstall-SSL is inherently per-site = safe.

---

## 9b. Per-site SSL Install/Reinstall/Remove — Linux GUI done (linux-v1.0.35+); macOS still needs the GUI
> Today's other Linux-only work needs NO Mac parity: the responsive-dashboard 4→2→1 card reflow was a
> GTK measure/allocate fix (WinUI star-columns + SwiftUI stacks reflow natively), and the `.deb`
> install-command fix (`dpkg -i` vs `apt install ./file.deb`) is Debian-packaging-specific.

**Shared engine (macOS gets these FREE — verify):** `engine/bhserve` now has `cmd_unsecure` +
`cmd_resecure` (dispatched as `bhserve unsecure|resecure <domain>`) + a factored `_rerender_site_vhost`
(proxy/node/python/php aware) used by secure AND unsecure. **Multi-label bug FIXED in the shared engine:**
`cmd_secure` derived the site name as `${domain%.*}` then `${name%%.*}` (first label) → a multi-label site
like `api.amarmedi.test` resolved to `api`, its `api.amarmedi.conf` was never found, and the re-render
silently no-op'd. Now strips the `.$tld` suffix → `api.amarmedi`. This is the same bug I fixed in the
Windows C# `Secure` (`domain.Split('.')[0]`) — macOS inherits the shell fix automatically.

**✅ REAL FIX (linux-v1.0.40):** the actual cause was NOT the async race (that was hardening) — `engine.py` `_PRIVILEGED` listed `secure` but not `unsecure`/`resecure`, so removal ran UNPRIVILEGED (no pkexec prompt) and couldn't restart nginx. Added both to `_PRIVILEGED`. Benjamin diagnosed it from "install prompts for a password, remove doesn't"; my WSL tests missed it (passwordless sudo masks the privilege gap). **Mac already runs its SSL apply via a privileged restart, so it was never hit.**

**✅ RESOLVED (linux-v1.0.39) — root cause = async `nginx -s stop` race; Mac already immune.**
Diagnosed on the real WSL2 box (not theory). The cause was NOT reload-vs-restart (linux-v1.0.36 already
switched `_rerender_site_vhost` to a full `nginx_stop; nginx_start`) — it was that **`nginx -s stop` is
ASYNC**: the master can keep :443 bound for a moment after it returns, so the immediate `nginx_start`
either no-ops (its `nginx_running` guard still sees the dying master) or fails to bind (EADDRINUSE).
`restart all` "worked" only because `cmd_restart` does `cmd_stop; sleep 2; cmd_start` — that 2s gap lets
the master fully exit. Also: the user's earlier "still broken" test was on a PRE-1.0.36 build (the in-app
updater itself was broken by the apt-2.9 `apt install ./file.deb` bug until 1.0.38, so their install was
stuck old). **Fix:** new shared `nginx_restart()` = stop, WAIT until the master is truly gone (`pgrep`
by config path, ~3s grace then force-kill), then start; `_rerender_site_vhost` + the Linux post-upgrade
restart call it. **Verified:** 5-round secure/unsecure stress (nginx stayed up, ssl toggled every round)
+ wire-level `openssl` SAN check (SNI=myapp.test served `DNS:myapp.test` before unsecure, `DNS:app83.test`
after = HTTPS gone, no restart-all). **Mac:** `nginx_restart` has a privilege guard — when the verb runs
UNPRIVILEGED (the Mac path) and can't sudo, it skips the wait/kill and just does the old fast stop→start,
so the Mac's own privileged `control("restart","nginx")` (already shipped in v1.7.6) still does the real
work with no added delay. So Mac is unaffected + already correct.

**Linux GUI (done):** site-row `_site_menu` (pages.py) now shows **Install SSL (HTTPS)** when http-only,
and **Reinstall SSL (fresh certificate)** + **Remove SSL** (confirm) when secured — wired to the new verbs.

**macOS TODO:** add the same three items to the Websites-panel row menu (Reinstall → `bhserve resecure`,
Remove → `bhserve unsecure`) — the engine verbs already exist. Also confirm the multi-label fix (a Mac
site like `api.foo.test` now re-renders correctly on secure).

---

## ionCube on Windows — FINAL (win-v1.0.58). ⚠️ Earlier 1.0.44–1.0.57 entries were WRONG and are removed

**Correction:** this file previously carried entries (win-v1.0.47/48/49/50) claiming the after-reboot
ionCube failure was *temporal* — "cold php-cgi can't resolve the loader's VC-runtime dependency (err
126); a warm respawn loads it". That analysis was wrong, contaminated by the dev environment: the dev
shell ran inside an MSIX-packaged app whose AppData writes are virtualized, so the ionCube loader DLLs
existed only in that package's private store. Dev-spawned php saw the DLLs (tests "passed"); the app /
Task Scheduler / the real user never had the file at all. The "boot-vs-warm A/B proof" was actually
comparing dev-context spawns vs real-context spawns.

**The one real root cause:** `php.ini` referenced an ionCube loader DLL **file that did not exist on
the real filesystem**. Respawning PHP can never fix a missing file — which is why every retry/delay/
console/environment scheme across 1.0.44–1.0.57 failed.

**Fix shipped in win-v1.0.58:**
- `PhpCgi.MissingIonCubeDll(version)` — parse the ini's `zend_extension=…ioncube…` path; report when
  the FILE is absent.
- `Engine.ReinstallIonCubeIfDllMissing` — missing file → re-download + re-extract the loader bundle +
  rewrite the ini (also purges a stale/partially-extracted loader-bundle cache before giving up).
  Called first by `EnableIonCube` (app launch + window-open auto-check) and by the boot heal loop;
  only then verify workers / respawn. Covers real-user cases too: AV-quarantined or deleted loader
  DLLs, interrupted installs.
- Removed the Scheduled-Task restart machinery (BHServeIonRestart) and the dashboard "Enable ionCube"
  button — healing is fully automatic and silent.
- `PhpHealUntilHealthy` remains as a plain verify-and-respawn safety net after `Start("all")` (no-ops
  once healthy).

**Still true from that era (kept):** `PhpCgi.Stop()` used to wedge reading `Process.MainModule.FileName`
across ~78 churning php-cgi processes at cold boot; it now time-bounds each read (400ms) and caps the
orphan scan (3s). **Mac note:** if the Mac stop/restart path ever inspects many processes' exe paths,
bound those reads the same way.

**macOS actions:**
1. Add the same self-heal — before verifying/restarting php over ionCube, check the `zend_extension`
   path in the loaded ini actually EXISTS on disk; if not, re-run the ionCube install (download + ini
   rewrite) instead of restarting php.
2. Nothing else — the temporal/cold-boot theory is retracted, so there is no "warm-up retry" behavior
   to port.

## win-v1.0.59 — OPcache JIT disabled (php-cgi workers crashed under real apps)

`opcache.jit = tracing` (BHServe's own php.ini tuning) crashed PHP 8.4 php-cgi workers with
`0xc0000005` mid-request under a Filament/Livewire admin panel → intermittent 502s on that site only.
It only ever ran on versions WITHOUT ionCube (the loader force-disables JIT), which is why
ionCube-configured versions never crashed. Verified: JIT off → 0 crashes under a 40-request hammer
(was ~10/hour). New defaults in `PhpCgi.Limits`: `opcache.jit = disable`, `opcache.jit_buffer_size = 0`
— OPcache itself (the real perf win) stays on.

**macOS action:** check whether the Mac engine's php.ini/FPM tuning enables `opcache.jit`; if it does,
disable it the same way (keep OPcache). JIT's web-app gain is minor and worker crashes surface as
random 502s that look like an app bug.

## win-v1.0.60 — PHP CA bundle (curl error 60 on all PHP HTTPS calls)

Windows PHP ships with NO CA bundle, so every PHP curl/openssl HTTPS call that verifies certificates
failed with "unable to get local issuer certificate" (curl error 60) — surfaced as the WHMCS
"LICENSING ERROR" page (its license phone-home), and would equally break payment gateways / any API
SDK. Laragon + XAMPP ship a cacert.pem; now BHServe does too: `EnsureCaBundle` downloads the Mozilla
bundle (curl.se/ca/cacert.pem, sanity-checked) to `bin\cacert.pem` once, and the php.ini tuner wires
`curl.cainfo` + `openssl.cafile` to it for every version. Offline-safe (retries on later spawns;
never points ini at a missing file).

**macOS action:** brew PHP normally gets a CA bundle via brew's openssl/ca-certificates — VERIFY with
`php -r 'var_dump(ini_get("openssl.cafile"), ini_get("curl.cainfo"));'` + a live
`curl_exec` to an https URL. **Linux note (done differently):** distro PHP uses the system
ca-certificates store natively — fine; but the static-php fallback builds may lack a default CA path —
worth the same live-probe check there (would need the same ini wiring to /etc/ssl/certs/ca-certificates.crt).

## win-v1.0.60 (part 2) — pin PHP sessions/uploads/temp to a writable BHServe dir

A WHMCS copied from a cPanel server showed "PHP session storage is not writeable": the site's
`.user.ini` carried `session.save_path=/tmp` (Linux path), and worse, with save_path unset PHP falls
back to the worker's TEMP env — which can be missing in service-context workers (GetTempPath then
returns C:\Windows, not user-writable). EnsureLimits now pins `session.save_path` (tmp\php-sessions),
`upload_tmp_dir` and `sys_temp_dir` to BHServe's tmp in every php.ini. Site-level fix for imports:
comment out `session.save_path` in the copied `.user.ini` (it overrides php.ini per-site).

**macOS action:** the Mac engine's FPM pools — verify session.save_path/upload_tmp_dir point at a
BHServe-writable dir (brew PHP defaults may rely on /var/tmp — usually fine on mac, but pinning to
~/.bhserve/tmp is the same robustness win, and imported-site `.user.ini` Linux paths can bite there
too, e.g. /tmp exists but per-site paths like /var/cpanel/... don't).

## win-v1.0.61 — Apache-backed site add never reloaded Apache

Adding a site with the Apache backend called `Apache.Start()`, which no-ops when httpd is already
running — so the FIRST Apache site worked (it started Apache) but every LATER one was never loaded:
its requests fell through to Apache's first loaded vhost (another site — e.g. a WHMCS that then
redirected to its own domain). Site-delete and server-switch already reloaded; only add forgot.
Fix: after rendering the vhost, `Apache.Running() ? Reload() : Start()`. Verified E2E: second
Apache site (php 7.4) serves correct PHP immediately after add, existing sites untouched.

**macOS action:** check the Mac engine's site-add path for the same pattern — if the Apache (or any
secondary backend) start step is a no-op when already running, the new vhost needs an explicit
reload in site-add, not just in delete/switch.
