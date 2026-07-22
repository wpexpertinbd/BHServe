# Engine deltas — exactly what to change in `../../engine/bhserve` for Linux

The macOS engine (`engine/bhserve`, bash) runs on Linux **almost unchanged** — same php-fpm + unix
sockets, raw-process supervision (node-sites / py-sites), nginx/apache vhosts, mkcert, fnm, ionCube.
Below is the **complete inventory of the macOS-specific constructs** (grepped from the v1.7.4 engine)
and the Linux equivalent for each. Line numbers are approximate — grep the token to find the current
spot.

> **Strategy:** don't fork the engine. Add a sourced **`platform-linux.sh`** (and keep a
> `platform-macos.sh` factored out of the inline code) selected by `case "$(uname)" in Darwin|Linux)`.
> Each item below becomes a function the platform file overrides. Develop + test every change on a
> real Ubuntu/WSL2 box — none of this is testable from macOS.

## 1. Homebrew assumptions → Linuxbrew (or apt) — *the core decision*

- **`export PATH="/opt/homebrew/bin:…"`** (top of file) → add `/home/linuxbrew/.linuxbrew/bin:/home/linuxbrew/.linuxbrew/sbin`.
- **`BREW="$(command -v brew)"` / `BREW_PREFIX="$(brew --prefix || echo /opt/homebrew)"`** → on Linux,
  Linuxbrew prefix is `/home/linuxbrew/.linuxbrew`. The fallback default must not be `/opt/homebrew`.
- **`brew services list|start|stop`** (`brew_svc`, `brew_svc_running`, mailpit autostart, the api
  status loop) → **Linuxbrew has no `brew services`** (it's macOS-only). Replace the daemon
  start/stop/running layer with one of:
  - **raw process + pid files** (the engine already does this for node/py sites and php-fpm — extend
    the same pattern to mariadb/redis/etc.), **or**
  - **systemd `--user` units** per service.
  Abstract behind the existing `brew_svc` / `brew_svc_running` helpers so the api `running` detection
  keeps working. This is the single biggest delta — budget for it.
- **Service registry** (`services()` table): the `php@8.4|php@8.4|opt/php@8.4/bin/php|php` probe paths
  are Homebrew-keg-relative (`$BREW_PREFIX/opt/...`). Linuxbrew keeps the same `opt/<formula>` layout,
  so probes port directly **if** you use Linuxbrew. For apt mode the paths differ
  (`/usr/sbin/php-fpm8.4`, `/usr/sbin/nginx`, …) → a per-source probe map.

## 2. DNS / `*.test` — macOS resolver → Linux resolver (the genuinely tricky bit)

`cmd_dns` (~line 1319-1353) is **all macOS**:
- **`/etc/resolver/<tld>`** + `printf 'nameserver 127.0.0.1' > /etc/resolver/<tld>` — macOS-only
  mechanism. **Linux has no `/etc/resolver`.**
- **`dscacheutil -flushcache`** — macOS-only.

Linux replacement (Ubuntu owns `127.0.0.53:53` via **systemd-resolved**), easiest → most powerful:
1. **`/etc/hosts` line per site** (no wildcard, needs root) — ship as the default, like the Windows port.
2. **systemd-resolved drop-in** for true wildcard `*.test`: run dnsmasq on a private address +
   `/etc/systemd/resolved.conf.d/bhserve.conf` (or `resolvectl` `~test` routing domain) → opt-in toggle.
3. **NetworkManager dnsmasq** drop-in `/etc/NetworkManager/dnsmasq.d/bhserve.conf` (`address=/test/127.0.0.1`)
   when NM manages DNS.
- **`dnsmasq_running(){ pgrep -x dnsmasq; }`** — `pgrep -x` exists on Linux (procps), **keep as-is**. ✅

## 3. Start-at-login — LaunchAgent → systemd user / autostart

`loginitem_*` (~line 2187-2236) is macOS:
- **`~/Library/LaunchAgents/com.biswashost.bhserve.login.plist`** + **`launchctl bootout/bootstrap/enable`** →
  replace with a **systemd `--user` service** (`~/.config/systemd/user/bhserve.service`,
  `systemctl --user enable bhserve`, + `loginctl enable-linger $USER` for pre-login), **or** a
  `~/.config/autostart/bhserve.desktop`. The `loginitem_enabled` check in the api snapshot maps to
  `systemctl --user is-enabled bhserve`.

## 4. Elevation — osascript → pkexec / sudoers

- The DNS/cert/hosts writes are invoked **privileged**. The comment at `cmd_dns` mentions
  "osascript admin"; on Linux wrap privileged verbs with **`pkexec /path/to/bhserve <verb>`**
  (PolicyKit GUI prompt — the direct analog of `osascript … with administrator privileges`).
- **`helper install`** already writes **`/etc/sudoers.d/bhserve`** (cross-platform!) — keep it for the
  password-less path; optionally add a PolicyKit `.policy` file too.

## 5. BSD vs GNU userland — small but real

- **`sed -i ''`** (2 occurrences) — BSD syntax. **GNU sed is `sed -i`** (no empty `''` arg). Guard with
  a helper: `sedi(){ if sed --version >/dev/null 2>&1; then sed -i "$@"; else sed -i '' "$@"; fi }`.
- **mkcert** — same binary/flags on Linux, but installs the CA into the **system trust + browser NSS
  store** (needs `libnss3-tools` for `certutil`). `secure` ports directly once mkcert + certutil exist.
- **`stat`, `date`, `awk`** flags are GNU on Linux — spot-check any `stat -f` / BSD `date` usage
  (the engine mostly uses portable forms, but verify on a real box).

## 6. Things that need ZERO change (verify, then move on) ✅

- php-fpm **pool → unix socket** + `fastcgi_pass unix:…/php-X.sock` (render_fpm_pool, nginx vhosts)
- **node-sites** (`nodesite` verb) + **py-sites** (`pysite` verb) supervision: `bash -c`, pid files in
  `run/`, logs in `logs/`, `kill_tree` (uses `pgrep -P` — works on Linux), reverse-proxy vhosts
- the **502 multi-version FPM autostart** fix (`site_php_keys`)
- **branded default `index.php`**, WordPress download + **wp-config salts** (the `getline` fix)
- **ionCube** — real Linux loaders exist for **all** PHP versions (better than macOS's 7.4/8.1 only)
- **fnm** (Node) — native Linux build
- **`~/.bhserve`** data layout, the **`api` JSON** shape, the **`pysite`/`nodesite`** config JSONs
- **MariaDB 127.0.0.1 bind** security fix — same `my.cnf.d/bhserve.cnf` idea (path differs per source)

## 7. Ubuntu-only gotchas to design for

- A **system nginx/apache2/mysql** may already hold `:80/:443/:3306` → `doctor` must detect and offer
  to stop/disable them (`systemctl`).
- **MariaDB root uses `unix_socket` auth** by default on Ubuntu → the `db` connect/passwd logic differs
  from Homebrew MariaDB.
- **AppImage needs libfuse2** on 22.04+ (or run `--appimage-extract-and-run`).
- **Wayland/X11** for the tray `StatusNotifierItem` — test on stock GNOME (may need the AppIndicator
  extension).

## ionCube (linux-v1.0.41)

The shared engine's `php_ioncube` is macOS-only (downloads the Darwin `dar` loader bundle, writes
`ioncube_loader_dar_<mm>.so` — Mach-O, unloadable on Linux). `platform-linux.sh` overrides it:

- Downloads `ioncube_loaders_lin_x86-64.zip` / `lin_aarch64.zip` (bundle covers 7.4 & 8.1–8.5;
  no 8.0/8.6 — warned per version). Purges + re-downloads a cache dir with no `lin_*.so` in it
  (a machine that ran the pre-fix code has a useless `dar` cache).
- Writes `zend_extension=` to **`/etc/php/<mm>/{fpm,cli}/conf.d/00-bhserve-ioncube.ini`** (sudo),
  NOT BHServe's own conf.d: ionCube must load before opcache or it aborts, and the distro's
  `10-opcache.ini` lives in the compiled-in scan dir which PHP reads before the `PHP_INI_SCAN_DIR`
  extra dir. `00-` sorts before `10-opcache` in the same dir → loads first. Also removes any broken
  `00-ioncube.ini` the pre-fix code left in BHServe's conf.d.
- Skips static-php fallback builds (fully static → can't dlopen shared zend extensions) with a warn.
- `php_mm` override: on Debian the key IS the minor (`php@8.3` → `8.3`); the shared brew probe
  falls back to the default php and returns the wrong mm. `php_status` override checks the distro
  conf.d path for "configured".

Verified in WSL2 Ubuntu: 7.4, 8.1, 8.2, 8.3, 8.4 all load `ionCube PHP Loader v15.5.0` (7.4 banner
says "+ ionCube24"), cleanly before opcache, CLI + FPM.

## OpenLiteSpeed backend (linux-v1.0.42)

Linux-only third site backend: `--server ols` (GUI: "OpenLiteSpeed" in the add-site dropdown).
nginx stays the front door (:80/:443, TLS, *.test) and proxies OLS sites to `127.0.0.1:8088` —
the exact shape of the apache backend (:8080). PHP goes from OLS to BHServe's EXISTING php-fpm
pools over their unix sockets (vhost-level `extprocessor` with `UDS:/…/php-<v>.sock`) — one PHP
stack, so ionCube / per-site version switching apply unchanged.

- Install: LiteSpeed apt repo (their repo.litespeed.sh, downloaded then executed) +
  `openlitespeed` + **`lsphp83`** (the admin console runs on it and OLS FATALLY exits at startup
  when `admin_php` dangles — don't rely on the bootstrapper pulling it) + `inotify-tools`.
  Auto-installed on first `--server ols` use.
- Takeover: `_ols_kill` (unit stop + lswsctrl stop + pkill; the package postinst starts OLS
  OUTSIDE systemd, and the unit ships `KillMode=none` — plain `systemctl stop` leaves the stock
  instance holding `*:8088`/`*:7080`, which makes the first BHServe start die with "address in
  use"). Then httpd_config.conf is rewritten as a BHServe-managed file (original backed up),
  loopback listeners only, workers as the site user (fpm-socket + file access), site block
  between `# BHSERVE-SITES-*` markers regenerated by `_ols_sync_config` on every site change.
  Admin console bound to 127.0.0.1:7080. Never graceful-restart ACROSS a listener-address
  change — old passed-through sockets conflict fatally; cold start at takeover.
- .htaccess: per-vhost `autoLoadHtaccess 1` + the `bhserve-ols-watch` systemd unit —
  `inotifywait -m -q -r` on the sites root piped to a debounced loop firing
  `systemctl reload lshttpd` (graceful). Two hard-won inotify rules: **must be monitor mode**
  (single-shot `-r` never sees dirs created after start) and **must NOT use `--include`**
  (with it, events in late-created dirs never surface — filter in the loop instead). Watcher
  script carries a `BHSERVE-OLSWATCH-V<N>` marker; `_ols_apply` re-deploys it when the version
  or unit state is off, so fixes reach existing installs on any site change.
- `ols_running` probes the PORT (`/dev/tcp`) first — pidfiles/unit state are unreliable
  (Type=forking + KillMode=none flap produced a false "failed to start" while serving fine).
- Overrides via `declare -f` rename-wrappers (site_add/set_php/set_root/rm, start_all,
  cmd_start/stop) + full replacements (render_site_vhost, site_set_server) — shared engine and
  macOS untouched.

Verified in WSL2 Ubuntu 24.04 (noble): full chain serves PHP 8.3/8.1; .htaccess rule changes
live in ~2s (existing, new, and nested dirs); php/server switches; stop/start all; site rm
cleanup; loopback-only listeners.
## 8. ✅ ionCube Linux override — DONE (merge note; see "ionCube (linux-v1.0.41)" above)

Mac Claude sketched this TODO on master (v1.7.7) while the override was being shipped independently
on windows-port-cli as linux-v1.0.41 — the two converged on the same design. The two cross-platform
notes from the macOS ionCube work are covered by the shipped implementation: (a) load ordering —
solved via the DISTRO conf.d (`/etc/php/<mm>/{fpm,cli}/conf.d/00-bhserve-ioncube.ini` sorts before
`10-opcache.ini` in the same dir), which is stronger than the `PHP_INI_SCAN_DIR="$cd_dir:"`
trailing-colon trick because on Debian the compiled-in scan dir is read regardless; (b) version
detection — the Linux `php_mm` override never executes PHP at all (`php@8.3` → `8.3` from the key),
so PHP 8.5 startup-deprecation output can't pollute it.

## 9. Subdomain / alias management (community PR #3) — GUI verify + ship only

The shared `engine/bhserve` already has the full subdomain feature (`site_subdomain`, `vhost_aliases`,
conflict detection, SSL reissue — verified on macOS v1.7.9), so **Linux needs NO engine override**. The
Linux GTK GUI is also already on master (`linux/app/bhserve/pages.py`: `site_subdomains` dialog +
"Manage subdomains…" item + aliases pill; plus PR #2's "Open nginx/Apache config" item). Just build/run
the GTK app on Ubuntu, do a functional pass (add/list/rm/conflict + HTTPS), and ship `linux-v1.0.x`. Full
checklist: `docs/WINDOWS-PARITY-TODO.md` §8 (Linux subsection).
