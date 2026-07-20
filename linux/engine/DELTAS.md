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

## 8. ionCube — the shared `php_ioncube` is macOS-only (needs a Linux override)

`php_ioncube` in `../../engine/bhserve` downloads the **macOS** bundle (`ioncube_loaders_mac_arm64.zip`
/ `..._mac_x86-64.zip`) and configures `ioncube_loader_mac_<ver>.so`. **Override it for Linux** (in
`platform-linux.sh`): use the Linux loaders — `ioncube_loaders_lin_x86-64.zip` (or `..._lin_aarch64.zip`
on ARM) and `ioncube_loader_lin_<ver>.so`. ionCube ships Linux loaders for **all** versions (7.4–8.5+),
better than macOS. **Keep the two cross-platform fixes from the macOS ionCube work (v1.7.7), they apply
to Linux too:** (a) load BHServe's per-version conf.d **before** the distro default so ionCube is the
**first `zend_extension`** (`PHP_INI_SCAN_DIR="$cd_dir:"`, trailing colon) — else opcache loads first and
ionCube fatals *"The Loader must appear as the first entry"*; (b) `php_mm` must run php with
`-d display_errors=0 -d error_reporting=0` + `grep -oE '[0-9]+\.[0-9]+'` so 8.5's startup deprecations
don't pollute the detected version.
