# BHServe — your own free local web server for macOS

A native, self-controlled local development stack by **BiswasHost** — a free
alternative to ServBay / Herd. A SwiftUI **menu-bar app** drives a transparent
**Bash engine** over Homebrew: latest versions, nothing hidden, fully yours.

Manage multiple PHP versions, nginx + Apache, MySQL/MariaDB/PostgreSQL, Redis,
Memcached, phpMyAdmin/Adminer/Mailpit and Node — with automatic `*.test` domains,
trusted local HTTPS, one-click WordPress sites, and public sharing via Cloudflare
Tunnel. 100% open-source.

> 🟢 **Stable.** Runs the author's daily dev work (WordPress, OpenCart, WHMCS, Blesta, Laravel).

---

## ✨ Features

- **Multiple PHP versions** — 7.4 and 8.1 → 8.6, **per site**. Each site gets its own
  PHP-FPM pool. ionCube + the common extensions for WordPress / OpenCart / WHMCS /
  Blesta are enabled.
- **nginx _and_ Apache** — pick per site. Apache mode gives full **`.htaccess`** support
  (via nginx → Apache reverse proxy). nginx is the fast default.
- **Databases** — MariaDB / MySQL + PostgreSQL. Create/drop databases, set passwords,
  all from the GUI. (Default login is **root with no password** — see below.)
- **Caching** — Redis + Memcached.
- **Web tools** — **phpMyAdmin · Adminer · Mailpit**, each with a one-click on/off.
  phpMyAdmin allows **uploads up to 2 GB**.
- **Trusted HTTPS** — one click issues a locally-trusted certificate (mkcert) for any
  site. No browser warnings.
- **Automatic `*.test` domains** — every site is reachable at `name.test` (dnsmasq).
- **Site types when you add a site:**
  - **WordPress** — creates the database, downloads the latest WordPress, and pre-fills
    `wp-config.php`. Just open the site and set the title + admin user.
  - **PHP** — creates a database named after the site.
  - **Others** — just the domain (static / your own app), no database.
- **Per-site custom root folder** — use the default folder or point a site at any folder
  on disk (set when adding, or change later in Edit).
- **Node.js** — multiple versions via `fnm`.
- **Share a site publicly** — one-click **Cloudflare Tunnel** gives a temporary public
  `https://…trycloudflare.com` URL. No account, no port-forwarding.
- **Live dashboard** — CPU / RAM / disk / network, plus per-service status cards.
- **Menu-bar resident** — starts your services at login and sits **silently in the menu
  bar** (no Dock icon). Open the dashboard whenever you want.
- **In-app auto-updater** — Settings ▸ Updates.

---

## ⬇️ Install

Grab the latest **`BHServe-x.y.z.pkg`** (or `.dmg`) from the
[**Releases**](https://github.com/wpexpertinbd/BHServe/releases) page.

> BHServe owns ports **80/443** and the `*.test` domain — **quit ServBay/Herd first**
> if you have them running.

### ⚠️ First launch — "unidentified developer" / "damaged" (read this!)

BHServe is **free and open-source but not notarized by Apple** (notarization needs a
paid Apple Developer account), so macOS shows a one-time security warning the **first
time** you open it. This is normal — you only do it **once**:

**Installed the `.pkg`?**
1. Double-click it. If macOS says *"cannot be opened … unidentified developer"* →
   **right-click (Control-click) the `.pkg` → Open → Open**.
2. Click through the installer. Done.

**Using the `.dmg`?**
1. Drag **BHServe** to Applications, then open it. macOS says *"can't be opened because
   Apple cannot check it"* (or *"is damaged"*) — that's just the download quarantine flag.
2. Open **System Settings → Privacy & Security**, scroll down → *"BHServe was blocked…"*
   with an **"Open Anyway"** button → click it → **Open**.
   *(Older macOS: right-click the app → Open → Open.)*

> **Why?** Any app not signed with a paid Apple Developer ID triggers this. BHServe is
> fully open-source — every line is in this repo. After the first launch, the in-app
> **Settings ▸ Updates** handles future versions for you.

The first run installs the core services via Homebrew (you'll be prompted for admin to
bind ports 80/443 and set up `*.test` DNS).

---

## 🚀 Quick start

1. **Add a site** — Sites ▸ **+** (or the dashboard). Enter a name (e.g. `myshop`),
   pick the **type** (WordPress / PHP / Others), PHP version, and web server.
2. Open **`http://myshop.test`** in your browser.
3. Want HTTPS? Open the site's **Edit** ▸ **Enable HTTPS** (trusted, no warnings) →
   `https://myshop.test`.
4. Building WordPress? Pick **WordPress** as the type → BHServe downloads WP, creates the
   DB, and pre-fills the config. Just finish the title + admin step in the browser.

Each site row has quick actions: open in browser, open folder, **edit** (PHP version /
nginx↔Apache / HTTPS / **root folder**), view logs, start/stop, share publicly, delete.

---

## 🗄️ Databases & phpMyAdmin — important for new users

For convenience on a local machine, **all databases use the `root` user with _no
password_** (blank). Nothing is reachable from outside your Mac (see Security), so this
is safe and saves you fiddling with credentials.

**Connection details for any app / framework:**

| Setting   | Value                               |
|-----------|-------------------------------------|
| Host      | `localhost` (or `127.0.0.1`)        |
| User      | `root`                              |
| Password  | *(leave blank — no password)*       |
| Socket    | `/tmp/mysql.sock`                   |

- **phpMyAdmin** (`http://phpmyadmin.test`): log in with username **`root`** and **leave
  the password field empty**. (WordPress, etc., that BHServe sets up are already wired to
  `root` / no password.)
- **Adminer** (`http://adminer.test`): Server `localhost`, Username `root`, Password
  empty.
- Open these from the **Web tools** card. Each tool has an **on/off** switch — keep only
  the ones you want; the rest of the panel keeps working regardless.

**Want a password instead?** Go to **Databases ▸ root** and set one anytime — then use it
in phpMyAdmin / your apps. (If you set a root password, remember to update `wp-config.php`
and any app configs to match.)

---

## 🌍 Share a site publicly (Cloudflare Tunnel)

Each site has a **Share** button. Click it → BHServe starts a **Cloudflare quick tunnel**
and gives you a temporary public **`https://…trycloudflare.com`** URL you can send to a
client or open on your phone. No Cloudflare account, no router config. Copy/open the link,
and **Stop sharing** when you're done. (cloudflared installs from the Share panel on first
use.)

---

## 🔒 Security

BHServe is a **local development** tool, hardened accordingly:

- **Loopback-only:** nginx (and Apache `:8080`, Mailpit `:8025`) listen on **`127.0.0.1`**
  — your sites, **phpMyAdmin/Adminer**, and Mailpit are **never exposed to your network**.
- **DBs use `root` / no password by design** — safe because nothing is reachable off this
  machine. Set a root password anytime in **Databases ▸ root**.
- Site / DB / log names are validated (no path traversal or config injection); DB inputs
  are SQL-escaped; passwords pass via environment, never on the command line.
- The optional **password-less control** helper grants `sudo` to **only** the `nginx`
  binary (so it can bind `:80/:443`) — the same approach Laravel Valet uses.
- The one exception to loopback-only is a **Cloudflare Tunnel you start yourself** — that
  intentionally exposes that one site publicly while it's running.

---

## 🛠️ Build from source

Everything is in this repo — the Bash engine (`engine/bhserve`) and the SwiftUI app
(`app/`). The built app bundles the engine, so it's self-contained.

```bash
cd app
./build-app.sh     # → dist/BHServe.app (self-contained, ad-hoc signed)
./make-dist.sh     # → dist/BHServe-<ver>.dmg + branded .pkg installer
```

Config and generated files live in **`~/.bhserve/`** (kept separate from your system /
Homebrew defaults). Sites live in **`~/BHServe/www/`** by default (or any custom folder).

The engine is usable directly too:

```bash
engine/bhserve doctor                       # check deps + ports
engine/bhserve site add myapp --php 8.4 --type wordpress
engine/bhserve secure myapp.test            # trusted local HTTPS
engine/bhserve tunnel start myapp           # public Cloudflare URL
engine/bhserve status
```

---

— BiswasHost · <https://www.biswashost.com>

## ☕ Support

BHServe is free and open-source. If it saved you time setting up your local dev stack, you
can **buy me a coffee** — it genuinely helps me keep building and maintaining free tools
like this. 🙏

- **bKash** (Personal · *Send Money*): **`01710378396`**

ধন্যবাদ! / Thank you!
