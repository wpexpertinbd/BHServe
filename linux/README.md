# BHServe for Linux — Ubuntu / Debian

A free local web server for Linux — the same app, features and sites as the macOS/Windows builds, as a
**GTK4 / libadwaita** control panel + a `bhserve` CLI. Multiple PHP versions per site, nginx & Apache,
MariaDB / MySQL / PostgreSQL, Redis & Memcached, Node & Python apps, trusted HTTPS + `*.test` domains,
one-click WordPress — all installed **on demand**, so the download stays small.

> ✅ **Stable.** Works end-to-end (verified on Ubuntu 24.04 and 26.04). Please report any issues.

---

## Install

**1. Download the latest `.deb`** from the [**Releases** page](https://github.com/wpexpertinbd/BHServe/releases)
(the newest **`linux-vX.Y.Z`** release → `bhserve_X.Y.Z_all.deb`), then install it:

```bash
cd ~/Downloads     # wherever the .deb downloaded
sudo apt install ./bhserve_*.deb
```

Or grab it straight from the terminal (bump the version to the latest release):

```bash
cd /tmp
wget https://github.com/wpexpertinbd/BHServe/releases/download/linux-v1.0.19/bhserve_1.0.19_all.deb
sudo apt install ./bhserve_1.0.19_all.deb
```

**2. Launch it** — open **“BHServe”** from your applications menu, or run:

```bash
bhserve-gui        # the GTK control panel
bhserve --help     # the CLI
```

**3. First run** — the dashboard opens with nothing installed yet. Click **Install** for the core stack
(nginx + PHP + MariaDB + mkcert), then **Add site** to create your first WordPress / PHP / Node / Python
site. Server binaries are fetched on demand (PHP via the Ondřej Surý repo, distro packages otherwise).

> ⚠️ BHServe owns ports **80/443** and the `*.test` domain — quit any other local stack
> (a running Apache/nginx, XAMPP, etc.) before first run.

---

## Update

```bash
bhserve self-update
```

Checks the latest release and installs it if you're behind — no version-tracking or manual re-download.
(You can also just `sudo apt install ./bhserve_<newer>.deb` over the top.)

---

## Uninstall

```bash
sudo apt remove bhserve          # remove the app + CLI
rm -rf ~/.bhserve                # optional: also wipe BHServe's data (config, certs, servers, vhosts)
```

---

## What it installs (on demand)

| | |
|---|---|
| **Web** | nginx (serves PHP directly), Apache (optional `.htaccess` backend behind nginx) |
| **PHP** | 7.4 → 8.5 per site — distro packages, the Ondřej PPA, or a portable static build where neither has it |
| **Databases** | MariaDB, MySQL, PostgreSQL |
| **Cache** | Redis, Memcached |
| **Runtimes** | Node.js (via fnm), Python (venv) |
| **Tools** | phpMyAdmin, Adminer, Mailpit, trusted HTTPS (mkcert), `*.test` domains |

`*.test` resolves via a managed `/etc/hosts` block by default (wildcard dnsmasq is opt-in).

---

## Requirements

- A **Debian/Ubuntu**-based distro (uses `apt`). Tested on Ubuntu 24.04 LTS and 26.04.
- `sudo` access (BHServe elevates via `pkexec`/`sudo` only for install + service control).
- ~200 MB for the app; server binaries add more as you install them.

---

## Notes & troubleshooting

- **PHP version not available?** On a brand-new release the Ondřej PPA may not have built yet — BHServe
  falls back to a portable static PHP for 8.0–8.5. PHP **7.4** is end-of-life, so it's only available on
  an LTS where Ondřej still ships it.
- **Native `apt install bhserve`** (a signed apt repo, so `apt upgrade` handles updates) is wired up but
  **opt-in** — see [`apt-repo/README.md`](apt-repo/README.md) to enable it. Until then, use the `.deb` +
  `bhserve self-update` above.
- **No tray icon after closing the window?** The top-bar icon uses GNOME's AppIndicator support, which
  ships enabled on Ubuntu. On a vanilla GNOME desktop, enable the *"AppIndicator and KStatusNotifierItem
  Support"* extension (or install `gnome-shell-extension-appindicator`). Without it, closing the window
  just quits normally.
- **RHEL / Fedora / openSUSE** (dnf/zypper) aren't supported yet — this build is Debian/Ubuntu (`apt`) only.

---

## For developers / contributors

The engine is the shared `../engine/bhserve` (bash, same as macOS) plus a Linux delta layer
(`../engine/platform-linux.sh`, sourced only on Linux — macOS untouched). The GUI is `app/bhserve/`
(Python + GTK4/libadwaita), driving the engine via its `api` JSON. Build the `.deb` with `build.sh` →
`dist/bhserve_<ver>_all.deb`. Architecture spec: [`../docs/LINUX-PORT.md`](../docs/LINUX-PORT.md);
engine deltas: [`engine/DELTAS.md`](engine/DELTAS.md).
