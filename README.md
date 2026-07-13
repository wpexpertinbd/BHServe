# BHServe — your own free local web server

A native, self-controlled local development stack by **BiswasHost** — a free alternative
to ServBay / Herd / Laragon. Multiple PHP versions, nginx + Apache, MySQL/MariaDB/PostgreSQL,
Redis, Memcached, phpMyAdmin/Adminer/Mailpit and Node.js — with automatic `*.test` domains,
trusted local HTTPS, one-click WordPress sites, managed Node apps, and public sharing via
Cloudflare Tunnel. 100% open-source.

**Available for:**

| Platform | Status |
|----------|--------|
| 🍎 **macOS** | ✅ **Stable** — native menu-bar app (Apple Silicon + Intel) |
| 🪟 **Windows** | ✅ **Stable** — native WinUI app (Windows 10/11) |
| 🐧 **Linux** | ✅ **Stable** — GTK4 app (Ubuntu/Debian, `.deb`) |

> 🟢 Runs the author's daily dev work — WordPress, OpenCart, WHMCS, Blesta, Laravel, Next.js.

---

## ✨ Features (all platforms)

- **Multiple PHP versions** — 7.4 and 8.1 → 8.6, **per site**, each with its own pool.
  ionCube + the common extensions for WordPress / OpenCart / WHMCS / Blesta are enabled.
- **nginx _and_ Apache** — pick per site. Apache mode gives full **`.htaccess`** support
  (nginx → Apache reverse proxy); nginx is the fast default.
- **Databases** — MariaDB / MySQL + PostgreSQL. Create/drop databases, set passwords, all
  from the GUI. (Default login is **root with no password** — see below.)
- **Caching** — Redis + Memcached.
- **Web tools** — **phpMyAdmin · Adminer · Mailpit**, each with a one-click on/off
  (phpMyAdmin allows **uploads up to 2 GB**).
- **Trusted HTTPS** — one click issues a locally-trusted certificate (mkcert) for any site.
  No browser warnings.
- **Automatic `*.test` domains** — every site is reachable at `name.test`.
- **Site types when you add a site:**
  - **WordPress** — creates the database, downloads the latest WordPress, pre-fills `wp-config.php`.
  - **PHP** — creates a database named after the site.
  - **Others** — just the domain (static / your own app), no database.
  - **Node app** — run a **frontend (e.g. Next.js) + optional backend/API (e.g. Laravel)**;
    BHServe supervises both and reverse-proxies them at your domain. Manage start/stop/restart,
    edit `.env`, run `npm install`, all from the app.
  - **Python app** — run a **Flask / Django / FastAPI / Gunicorn / Uvicorn** app; BHServe creates a
    virtualenv, supervises the process, and reverse-proxies it at your domain. Start/stop/restart and
    `pip install` from the app.
- **Per-site custom root folder** — default folder, or point a site at any folder on disk.
- **Node.js** — multiple versions via `fnm`. **Python** — managed interpreter for Python apps.
- **Share a site publicly** — one-click **Cloudflare Tunnel** gives a temporary public
  `https://…trycloudflare.com` URL. No account, no port-forwarding.
- **Live dashboard** — CPU / RAM / disk / network + per-service status cards.
- **Tray / menu-bar resident** — starts your services at login and sits quietly in the
  **menu bar** (macOS) / **system tray** (Windows). Open the dashboard whenever you want.
- **In-app auto-updater** — Settings ▸ Updates.

*(The engine differs per OS — Homebrew on macOS, portable downloads on Windows, native
apt + the Ondřej PHP repo on Linux — but the app, the features, and your sites work the
same everywhere.)*

### 🐧 Linux (Ubuntu/Debian)

```bash
# download bhserve_<version>_all.deb from the linux-v* release, then:
sudo apt install ./bhserve_<version>_all.deb
bhserve-gui        # or launch “BHServe” from your apps menu
```

The `.deb` provides the engine + a **GTK4 / libadwaita** control panel (the same 8 panes
as macOS/Windows). The servers themselves are installed on demand via apt — PHP comes from
the **Ondřej Surý** repo (7.4 → 8.4), with a portable static build for versions the distro
can't provide. `*.test` resolves via a managed `/etc/hosts` block by default (wildcard
dnsmasq is opt-in). Closing the window keeps BHServe in the top-bar tray. Tested on
**Ubuntu 24.04 + GNOME**.

---

## ⬇️ Download & Install

Grab the latest build for your OS from the
[**Releases**](https://github.com/wpexpertinbd/BHServe/releases) page.

> BHServe owns ports **80/443** and the `*.test` domain — quit any other local stack
> (ServBay / Herd / Laragon / XAMPP) before first run.

### 🍎 macOS

Download **`BHServe-x.y.z.pkg`** (installer) or **`.dmg`** (drag to Applications).

**⚠️ First launch — "unidentified developer" / "damaged" (read this):** BHServe is free and
open-source but **not notarized by Apple** (that needs a paid Apple Developer account), so
macOS shows a one-time warning the **first** time you open it. You only do this **once**:

- **`.pkg`:** if macOS says *"unidentified developer"* → **right-click the `.pkg` → Open → Open**.
- **`.dmg`:** drag **BHServe** to Applications and open it. If it says *"can't be opened…"*
  or *"is damaged"* → **System Settings → Privacy & Security**, scroll down to
  *"BHServe was blocked…"* → **Open Anyway** → **Open**. *(Older macOS: right-click → Open → Open.)*

The "damaged"/"can't be checked" message is just the download-quarantine flag on an
un-notarized app — nothing is actually wrong. After the first launch, **Settings ▸ Updates**
handles future versions.

### 🪟 Windows

1. **Download and run `BHServe-Setup-x.x.x.exe`.**
2. It's **unsigned**, so Windows SmartScreen shows *"Windows protected your PC"* →
   click **More info → Run anyway**. (One-time; signing certs are costly — the code is all here.)
3. Finish the installer and launch BHServe.

**🛡️ Recommended FIRST — add an antivirus folder exclusion (do this *before* installing).**
BHServe is unsigned and downloads server binaries (PHP, nginx, MariaDB, redis, memcached…), so
some antivirus engines false-positive and quarantine them — sometimes *after* a clean install
(on the next scan), which silently breaks things. Excluding BHServe's two folders avoids this
entirely, and you can **leave your antivirus on**. Add **both**:

```
C:\Program Files\BHServe
```
```
C:\Users\<your-user>\AppData\Local\BHServe
```

The first is the app + CLI; the second is BHServe's data dir (config, downloaded server binaries,
your site vhosts, certs). Replace `<your-user>` with your Windows username.

How to add a **folder exclusion** in common antivirus:

- **Windows Security (Defender):** Virus & threat protection → **Manage settings** → Exclusions →
  **Add or remove exclusions** → **Add an exclusion → Folder** → pick each folder above.
- **ESET** (NOD32 / Internet Security): Advanced setup (**F5**) → **Detection engine → Exclusions →
  Performance exclusions → Edit → Add**, and enter each folder path (e.g. `C:\Program Files\BHServe`).
  (For on-execute blocks also check **HIPS / Detection engine → Real-time file system protection**.)
- **Avast / AVG:** Menu → Settings → **General → Exceptions → Add exception** → paste each folder.
- **Bitdefender:** Protection → Antivirus → **Settings → Manage exceptions → Add an exception** → folder.
- **Kaspersky:** Settings → **Security settings → Exclusions / trusted apps → Manage exclusions → Add**.
- **Malwarebytes:** Settings → **Allow list → Add → Allow a file or folder** → pick each folder.
- **Other AVs:** look for **Exclusions / Exceptions / Allow list / Trusted folders** and add the two
  paths above.

> ⚠️ An exclusion tells your antivirus to skip those folders — only do this because you trust
> BHServe (the full source is in this repo). If you ever uninstall BHServe, remove the exclusions.

If BHServe was already installed and a server won't start (its binary got quarantined), add the
exclusions above, **restore** the quarantined file from your antivirus, then reinstall that service
(Services tab) or relaunch BHServe.

**🚫 "An Application Control policy has blocked this file" / "we can't confirm who published
BHServe.App.exe"** — this is **Smart App Control** (a Windows 11 feature), not a virus. It blocks
**unsigned** apps it doesn't recognize. Two cases:

- **SmartScreen** (*"Windows protected your PC"*, blue dialog) — click **More info → Run anyway**.
  You can also right-click the downloaded `BHServe-Setup-x.x.x.exe` → **Properties** → tick
  **Unblock** → **OK**, then run it.
- **Smart App Control** (the *"Application Control policy has blocked this file"* error) — unlike
  SmartScreen, SAC has **no per-app "allow"**. To run an unsigned app you have to turn it off:
  **Settings → Privacy & security → Windows Security → App & browser control → Smart App Control
  settings → Off**.
  > ⚠️ **This is a one-way change** — once Smart App Control is **Off**, it can only be turned back
  > **On** by resetting/reinstalling Windows. It's a security trade-off; only do it if you understand
  > that. If you'd rather not, wait for a signed build.

> **Why this happens:** BHServe is currently **unsigned** (code-signing certificates are an ongoing
> cost). Each new release is a fresh unsigned file with no reputation yet, so SmartScreen/Smart App
> Control can flag it. A signed build (planned) removes this entirely.

### 🐧 Linux (Ubuntu/Debian)

Download **`bhserve_x.y.z_all.deb`** from the latest **`linux-v*`** release and install it:

```bash
cd ~/Downloads && sudo apt install ./bhserve_*.deb
bhserve-gui        # or launch “BHServe” from your apps menu
```

Future updates: `bhserve self-update`. Full guide → [`linux/README.md`](linux/README.md).
Debian/Ubuntu only (uses `apt`); tested on Ubuntu 24.04 + 26.04.

---

## ✅ Before you start — install & turn these on

Before adding a site, install these from the **Services** tab (the first run usually installs
the core set for you) and make sure each shows **running / active**, or just click **Start All**:

| Service | Why | Needed for |
|---------|-----|-----------|
| **nginx** | the web server | every site |
| **PHP** (≥ one version, e.g. 8.4) | runs your PHP code | every PHP/WordPress site |
| **MariaDB / MySQL** | the database | WordPress + any DB-backed site *(skip for static "Others")* |
| **DNS** (dnsmasq) | makes `*.test` resolve | every site *(macOS — Windows handles this via the hosts file automatically)* |
| **mkcert** *(optional)* | trusted local HTTPS | only if you want `https://` |

> If a site shows **"This site can't be reached" / `DNS_PROBE_FINISHED_NXDOMAIN`**, the **DNS**
> service isn't running — open **Services** and **Start dnsmasq** (it asks for admin once). If you
> see **502 Bad Gateway**, the site's **PHP version or nginx** isn't running — start them.

---

## 🚀 Quick start

1. **Add a site** — Sites ▸ **+**. Enter a name (e.g. `myshop`), pick the **type**
   (WordPress / PHP / Others / **Node app**), PHP version, and web server.
2. Open **`http://myshop.test`** in your browser.
3. Want HTTPS? The site's **"…"** menu ▸ **Enable HTTPS** (trusted, no warnings) → `https://myshop.test`.
4. Building WordPress? Pick **WordPress** → BHServe downloads WP, creates the DB, and pre-fills
   the config. Just finish the title + admin step in the browser.

Each site row has quick actions: open in browser, open folder, view logs, start/stop, share
publicly, a **"…"** menu (change PHP / root folder / switch nginx↔Apache / enable HTTPS / delete),
and — for Node apps — start/stop/restart, edit `.env`, and `npm install`.

---

## 📦 Moving sites from XAMPP / Local / Laragon / ServBay / Herd

Already have sites in another local stack? Bringing them into BHServe is three steps — copy the
files, import the database, point the app at BHServe — and works the same on macOS and Windows.
See the **[Migration guide → `docs/MIGRATING.md`](docs/MIGRATING.md)** for per-stack file
locations, database export/import, WordPress URL search-replace, and troubleshooting.

---

## 🗄️ Databases & phpMyAdmin — important for new users

For convenience on a local machine, **all databases use the `root` user with _no password_**
(blank). Nothing is reachable from outside your machine (see Security), so this is safe.

| Setting   | Value                          |
|-----------|--------------------------------|
| Host      | `localhost` (or `127.0.0.1`)   |
| Port      | `3306`                         |
| User      | `root`                         |
| Password  | *(leave blank — no password)*  |

- **phpMyAdmin** (`http://phpmyadmin.test`) / **Adminer** (`http://adminer.test`): log in as
  **`root`** with an **empty password**. WordPress and other sites BHServe sets up are already wired this way.
- Open these from the **Web tools** card — each has an on/off switch.
- **Want a password?** Set one anytime in **Databases ▸ Root password** (then update your app configs to match).

---

## 🌍 Share a site publicly (Cloudflare Tunnel)

Each site has a **Share** button → BHServe starts a **Cloudflare quick tunnel** and gives you a
temporary public **`https://…trycloudflare.com`** URL to send a client or open on your phone.
No Cloudflare account, no router config. **Stop sharing** when done. (cloudflared installs on first use.)

---

## 🔒 Security

BHServe is a **local development** tool, hardened accordingly:

- **Loopback-only:** nginx, Apache, MySQL, Mailpit, and your Node apps listen on **`127.0.0.1`**
  — your sites, phpMyAdmin/Adminer, and Mailpit are **never exposed to your network**.
- **DBs use `root` / no password by design** — safe because nothing is reachable off this machine.
- Site / DB / log names are validated (no path traversal or config injection); DB inputs are
  SQL-escaped; passwords pass via environment, never on the command line.
- Privileged steps are minimal and explicit — binding `:80/:443` and `*.test` DNS (macOS sudo /
  Windows UAC). On macOS the optional password-less helper grants `sudo` to **only** the `nginx` binary.
- The one exception to loopback-only is a **Cloudflare Tunnel you start yourself** — that
  intentionally exposes that one site publicly while it's running.

---

## 🛠️ Build from source

Everything is in this repo:

- **macOS** — Bash engine (`engine/bhserve`) + SwiftUI app (`app/`). `cd app && ./build-app.sh`
  then `./make-dist.sh` → `.dmg` + `.pkg`.
- **Windows** — C# / .NET + WinUI (`windows/`). See **`windows/README.md`** (`build.ps1`).
- **Linux** — Bash engine (`engine/bhserve` + `engine/platform-linux.sh`) + GTK4/PyGObject app
  (`linux/`). `cd linux && ./build.sh` → `dist/bhserve_<ver>_all.deb`. See **`linux/README.md`**.

Data lives in `~/.bhserve/` (macOS) / `%LOCALAPPDATA%\BHServe` (Windows); sites default to
`~/BHServe/www/`. The engine is usable directly too (`bhserve doctor`, `site add`, `secure`, `status`, …).

---

— BiswasHost · <https://www.biswashost.com>

## ☕ Support

BHServe is free and open-source. If it saved you time, you can **buy me a coffee** — it
genuinely helps me keep building and maintaining free tools like this. 🙏

- **bKash** (Personal · *Send Money*): **`01710378396`**

ধন্যবাদ! / Thank you!
