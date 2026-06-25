# Security policy & threat model — BHServe for Windows

BHServe is a **local development** web stack. It is meant to run on a developer's own
machine and serve sites at `*.test` over loopback. It is **not** a production server and
should never be exposed to the public internet.

## Security posture

**Everything binds to loopback (`127.0.0.1`) only.** No BHServe-managed service listens on
a routable interface, so nothing it runs is reachable from the LAN or the internet:

| Service     | Bind                              |
|-------------|-----------------------------------|
| nginx       | `127.0.0.1:80` / `:443`           |
| Apache (opt)| `127.0.0.1:8080`                  |
| php-cgi     | `127.0.0.1:91xx`                  |
| MySQL       | `127.0.0.1:3306` (`--bind-address`) |
| Redis       | `127.0.0.1:6379` (`--bind` + protected-mode) |
| Memcached   | `127.0.0.1:11211` (`-l`, UDP off `-U 0`) |
| Mailpit     | `127.0.0.1:8025` / `:1025`        |

**Passwordless `root` on MySQL** is the universal local-dev convention (Laragon, XAMPP, MAMP
do the same). It is safe here only because (a) the account is `root@localhost` — host-
restricted to loopback — and (b) the server binds to `127.0.0.1`. Do not change the bind
address or create `'user'@'%'` accounts unless you understand the exposure. Per-database
users with passwords are available (`bhserve db create <name> <password>`).

## Input handling

- **Site names** are validated `^[a-z0-9][a-z0-9-]*$`; **database names** `^[A-Za-z0-9_]+$`
  (interpolated only as backtick-quoted identifiers) — no SQL or path-traversal surface.
- **The elevated helper** (`bhserve-elevate.exe`, used only for hosts-file edits + the mkcert
  CA) validates every domain against a strict hostname regex before writing, so a crafted
  value cannot inject extra lines into the hosts file. Args are passed via `ArgumentList`
  (never a concatenated command line).
- **Downloads** go only to hardcoded **HTTPS** endpoints of official sources (nginx.org,
  windows.php.net, dev.mysql.com, github.com release assets, wordpress.org, etc.) and are
  fetched/extracted by Windows' Microsoft-signed `curl.exe` + `tar.exe` via `ArgumentList`.

## Known, accepted trade-offs (by design for a local tool)

- **The installer/app is unsigned** (no paid code-signing certificate). Windows SmartScreen
  shows an "unknown publisher" prompt, and some antivirus may flag the on-demand downloader —
  see [`ANTIVIRUS.md`](ANTIVIRUS.md). This is a publisher-reputation matter, not a code flaw.
- **No checksum pinning** on downloaded binaries beyond TLS to the official source. This
  matches mainstream tools (scoop, Chocolatey community packages). Integrity rests on HTTPS +
  the source's own distribution.
- **Adminer / phpMyAdmin** connect as passwordless root and are served only on a loopback
  `*.test` host — same local-dev convention as above.

## Reporting a vulnerability

Found something? Please **don't** open a public issue with exploit details. Email
`benjamin dot biswas at gmail dot com` (or open a GitHub security advisory) with steps to
reproduce. Because this is a loopback-only local dev tool, the most useful reports are ones
showing a path to **privilege escalation** (e.g. via the elevated helper) or a way for a
**remote/LAN** actor to reach a BHServe service. Those are taken seriously and fixed quickly.
