# BHServe for Linux / Ubuntu — Port Plan & Architecture Spec

> Written **from the macOS codebase** as the build spec for the Linux version.
> Get the code with `git clone https://github.com/wpexpertinbd/BHServe`; the Linux
> app lives in `linux/` in this repo. Build/test on an actual Ubuntu box (or VM).

**The good news, up front:** unlike the Windows port (which was a near-total rewrite),
**Linux can reuse most of the existing `engine/bhserve` bash script.** bash, php-fpm,
**unix sockets**, dnsmasq, and mkcert all exist natively on Linux — the exact things
Windows lacked. So the Mac's whole service model (FPM pool per version on a unix
socket, raw-process + pid files, the 502 multi-version-autostart fix, ionCube) ports
**directly**. The real work is: (1) a Linux **GUI** (SwiftUI doesn't run on Linux),
and (2) a handful of **platform deltas** in the engine (package source, DNS wiring,
elevation, start-at-login). Target = **Ubuntu 22.04/24.04 + GNOME**, but keep it
distro-generic where cheap.

---

## 1. Recommended stack

- **Engine:** **reuse `engine/bhserve`** (bash) with a thin platform-delta layer.
  The script already abstracts the package manager behind a `BREW` variable and a
  `BREW_PREFIX` — see §3 for the two ways to fill that on Linux.
- **GUI:** **GTK4 + libadwaita** — native GNOME/Ubuntu look, the natural Linux
  counterpart to SwiftUI. It drives the bash engine exactly like the SwiftUI app
  does on Mac (spawn the CLI, parse the `api` JSON).
  - **Language sub-choice (decide when you start):**
    - **Rust + `gtk4-rs`** — single self-contained binary, matches BHServe's
      "no heavy runtime" ethos. More upfront work. *(leaning recommendation)*
    - **Python + PyGObject** — fastest to build, excellent GTK bindings, Claude is
      very strong here; ships as a `.deb` depending on `python3-gi` (tiny, in Ubuntu repos).
- **Packaging:** **`.deb`** (apt-installable, native Ubuntu) **+ `.AppImage`**
  (portable "download & run", the self-contained analog of the Mac `.dmg`). **Skip
  Flatpak/Snap** — their sandboxes block managing ports/dnsmasq/services, the same
  reason we skipped MSIX on Windows.
- **Auto-update:** same as Mac — poll `releases/latest`, download the asset
  (`.AppImage` or `.deb`), install. (Asset matcher: `.AppImage`/`.deb` vs `.pkg`.)

```
linux/
  app/                 # GTK4 + libadwaita GUI (Rust gtk4-rs OR Python PyGObject)
  engine/              # symlink/copy of ../engine + platform-delta overrides
  packaging/
    deb/               # debian/ control, rules → bhserve_<ver>_amd64.deb
    appimage/          # AppDir + appimagetool recipe
  build.sh             # build app + .deb + .AppImage
  README.md
```

(Or: keep using the repo-root `engine/bhserve` directly and add only a small
`engine/platform-linux.sh` sourced when `$(uname)` = Linux — see §3.)

---

## 2. Big port mappings (mac → Linux)

| Concern | macOS (current) | Linux / Ubuntu (target) |
|---|---|---|
| Engine | `engine/bhserve` (bash) | **same bash script** + platform deltas |
| UI | Swift + SwiftUI | **GTK4 + libadwaita** (Rust or Python) |
| Pkg mgr | Homebrew (`/opt/homebrew`) | **Homebrew-on-Linux** *(max reuse)* OR **apt + Ondřej PPA** *(native)* — see §3 |
| Data dir | `~/.bhserve` | `~/.bhserve` (unchanged — already `$HOME`-based) |
| **PHP runner** | **php-fpm pool → unix socket** | **identical** — php-fpm + unix socket both native on Linux ✅ |
| nginx fastcgi | `fastcgi_pass unix:…/php-8.3.sock;` | **identical** ✅ |
| Web server | nginx / httpd | nginx / apache2 (same configs) |
| Process mgmt | raw `Process` + pid files (no launchd for daemons) | **identical** ✅ |
| TLS | mkcert | **mkcert** (Linux build; installs into the system + browser NSS store) ✅ |
| `*.test` DNS | dnsmasq + resolver file | dnsmasq, but mind **systemd-resolved owns :53** — see §5 |
| Start at login | LaunchAgent plist | **systemd `--user` service** OR `~/.config/autostart/*.desktop` |
| Elevation | `osascript … administrator` / sudoers | **pkexec** (PolicyKit GUI prompt) OR `/etc/sudoers.d/bhserve` (the engine's `helper` already writes this!) |
| Ports 80/443 | need sudo | need root too (privileged ports) — same model |
| ionCube | mac loaders (only 7.4/8.1 exist) | **real Linux loaders for all versions** ✅ (better than Mac) |
| DB / Node | MariaDB/PG via brew; fnm | same via brew/apt; **fnm has a Linux build** ✅ |

Note how many rows say **identical / ✅** — that's the whole point of doing Linux.

---

## 3. The one real decision: how to install the binaries

The bash engine installs services via a `BREW`/`brew install` abstraction. Two ways
to satisfy it on Linux:

**Option A — Homebrew on Linux (Linuxbrew) → maximum code reuse.**
`brew` runs on Linux (installs to `/home/linuxbrew/.linuxbrew`). The engine already
detects `BREW_PREFIX`, so `brew install php@8.4 nginx mariadb …` works almost
unchanged — the *least porting effort by far*. Downsides: an extra ~Homebrew
install, not "the Ubuntu way," some formulae differ.

**Option B — native apt + Ondřej Surý PPA → native Ubuntu.**
`add-apt-repository ppa:ondrej/php` gives `php8.1`–`php8.4` (+ `-fpm`), plus
`apt install nginx mariadb-server`. Most native, but: packages are **system-wide**
(collide with any system nginx/mariadb), services are **systemd units** (so
install/start/stop must learn `systemctl`), and **everything needs sudo**. This is
more porting work and more invasive — replace the engine's install/start/stop layer
with apt + systemctl equivalents.

**Recommendation:** start with **Option A (Linuxbrew)** to get a working port fast
with the bash engine nearly intact; offer Option B later as a "native packages" mode
for users who object to Homebrew. Abstract the difference behind the existing
`BREW`/service helpers so both can coexist.

---

## 4. Feature-parity checklist (same `bhserve` verbs)

The verb surface is unchanged from Mac — most already work; the ones to revisit on
Linux are flagged:

- [ ] `doctor` — deps/ports/conflicts (also detect a **system nginx/apache/mysql**
      already on :80/:443/:3306, and **systemd-resolved on :53**)
- [ ] `init` — unchanged (`~/.bhserve` skeleton)
- [ ] `install/update/uninstall` — **delta:** brew-on-Linux (A) or apt+systemctl (B)
- [ ] `site add/list/rm/php/server/root` — vhost render unchanged; **delta:** hosts/DNS (see §5)
- [ ] `secure <domain>` — mkcert (installs CA into system trust + browser NSS via `certutil`)
- [ ] `dns` — **delta:** dnsmasq vs systemd-resolved (see §5)
- [ ] `db {list|create|drop|passwd}` — **delta:** MariaDB socket path / auth-plugin differs on Ubuntu
- [ ] `node {…}` — fnm-linux (near-identical)
- [ ] `php {ioncube|status|ini path|reload}` — **ports directly**; ionCube even
      better (real Linux loaders for all versions). The Edit-php.ini feature works as-is.
- [ ] `pma|adminer|mailpit` — unchanged
- [ ] `config|logs|start|stop|restart|enable|disable|status|api` — unchanged
      (incl. the **502 fix**: start every PHP version an enabled site uses)
- [ ] `helper {install|uninstall}` — **delta:** writes `/etc/sudoers.d/bhserve`
      (already does on Mac!) — keep, optionally add a pkexec PolicyKit policy too.

GUI screens mirror the Mac app 1:1 (Dashboard / Services / Sites / Databases / Node /
Logs / Settings). Tray/background: GTK `StatusNotifierItem` (AppIndicator) for the
menu-bar analog; close → tray. Brand blue **#0d6efd**.

---

## 5. DNS / `*.test` on Ubuntu (the genuinely tricky bit)

Ubuntu runs **systemd-resolved on 127.0.0.53:53**, so a naive dnsmasq on :53
collides. Three workable approaches, easiest first:

1. **`/etc/hosts` line per site** (no wildcard) — dead simple, no daemon, needs root
   to write. Good default; the Windows port uses the same fallback.
2. **systemd-resolved drop-in** — run dnsmasq on a non-conflicting address and add
   `/etc/systemd/resolved.conf.d/bhserve.conf` (or an `~test` routing domain via
   `resolvectl`) pointing `.test` at it. True wildcard, no :53 fight.
3. **NetworkManager's dnsmasq** — add `/etc/NetworkManager/dnsmasq.d/bhserve.conf`
   with `address=/test/127.0.0.1` (only if NM is managing DNS).

Recommend shipping #1 by default with #2 as an opt-in "wildcard `*.test`" toggle in
Settings (mirrors how the Mac uses dnsmasq + a resolver file).

---

## 6. Elevation (pkexec / sudoers — the sudo analog)

Root is needed for: binding :80/:443, writing `/etc/hosts` or resolved config,
installing the mkcert CA, and (Option B) `apt`/`systemctl`. Two mechanisms:

- **pkexec** (PolicyKit) → a graphical password prompt, the direct analog of macOS
  `osascript … with administrator privileges`. Wrap privileged engine actions:
  `pkexec /path/to/bhserve <verb>`.
- **`/etc/sudoers.d/bhserve`** → password-less specific commands, the same
  promptless path the Mac's `helper install` already creates. Keep it.

---

## 7. Start at login

- **systemd user service:** `~/.config/systemd/user/bhserve.service`
  (`ExecStart=…/bhserve start all`), `systemctl --user enable bhserve`, plus
  `loginctl enable-linger $USER` so it can run pre-login if wanted. The clean analog
  of the LaunchAgent.
- Or a desktop-autostart entry: `~/.config/autostart/bhserve.desktop`.

---

## 8. Build / dev workflow (Ubuntu)

```bash
git clone https://github.com/wpexpertinbd/BHServe
cd BHServe/linux

# engine works immediately (bash) once a pkg source is set up (Linuxbrew or apt):
../engine/bhserve doctor
../engine/bhserve init
../engine/bhserve site add myapp --php 8.4

# GUI (Rust path):
cd app && cargo run            # gtk4-rs
# GUI (Python path):
python3 -m bhserve_app         # PyGObject

# package:
./build.sh                     # → packaging/dist/bhserve_<ver>_amd64.deb + BHServe-<ver>.AppImage
```

Then cut a GitHub release with the `.deb` + `.AppImage` so the updater finds it.

---

## 9. Suggested phasing

1. **Engine on Linux:** pick Option A (Linuxbrew), get `init` + `install nginx`
   + `install php@8.4` + `site add` serving `http://myapp.test` (hosts-file DNS).
   No GUI yet — the bash engine should "just work" with minimal deltas.
2. **HTTPS + DNS + multi-PHP:** mkcert CA into system/NSS trust, `secure`, the
   wildcard-`.test` resolved drop-in, multi-version FPM (502 fix already in the script).
3. **GTK shell:** Dashboard + Services + Sites + Settings driving the engine; tray;
   systemd-user autostart.
4. **DB + Node + tools:** MariaDB (Ubuntu socket/auth deltas), fnm, pma/adminer/mailpit.
5. **php.ini editor + ionCube (all versions) + .deb/.AppImage + auto-updater + polish.**

---

## 10. Easy wins vs care-needed

**Easy (native on Linux, port directly):** the entire bash engine, php-fpm + unix
sockets, the FPM/502 model, mkcert, fnm, ionCube (all versions — better than Mac),
nginx/apache vhosts, `~/.bhserve` layout.

**Needs care:**
- **systemd-resolved owns :53** → don't fight it; use hosts-file or a resolved drop-in (§5).
- **System services may already hold :80/:443/:3306** (Ubuntu often has nginx/mysql
  installed) → `doctor` must detect + offer to stop/disable them (`systemctl`).
- **MariaDB auth on Ubuntu** uses `unix_socket` auth for root by default → the `db`
  verbs' connection/passwd logic needs adjusting vs Homebrew MariaDB.
- **AppImage + FUSE:** AppImages need libfuse2 on 22.04+ (`apt install libfuse2`), or
  build with `appimagetool --appimage-extract-and-run`. Document it.
- **Wayland vs X11** for the tray/AppIndicator — libadwaita handles most, but test the
  `StatusNotifierItem` on stock GNOME (needs the AppIndicator extension on some setups).
- **No code-signing/notarization needed** (Linux has no Gatekeeper) — one less hassle
  than Mac/Windows. `.deb` users just `apt install ./file.deb`.
