# Publishing the Windows build — repo + installer release

> How the Windows session should ship: push code to the **one shared repo** and cut
> the **Windows installer release** without disturbing the macOS app or its in-app
> updater. Read this with `docs/WINDOWS-PORT.md` (build) and `docs/MAC-FEATURE-REFERENCE.md` (parity).

BHServe is a **single monorepo** (`github.com/wpexpertinbd/BHServe`) holding macOS,
Windows, and (later) Linux. **Do not create a separate repo.** The Windows app lives
under `windows/`; the Mac app under `macos/` + `engine/`.

---

## A. Publishing CODE to the repo

Same flow used so far (branch → PR → merge):

```bash
git checkout master
git pull origin master                 # always sync first — Mac releases land here too
git checkout -b windows-<short-topic>   # e.g. windows-gui-phase3
# …work…
git add windows/                        # keep changes scoped to windows/ (don't touch macos/ or engine/)
git commit -m "windows: <what changed>"
git push -u origin windows-<short-topic>
gh pr create --title "Windows: <summary>" --body "<what + how tested>"
```

Conventions (match the existing history):
- **Commit messages** end with: `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`
- **PR bodies** end with: `🤖 Generated with [Claude Code](https://claude.com/claude-code)`
- Keep Windows changes inside `windows/` (plus shared `docs/`). Never modify `macos/**`,
  `engine/**`, or the Mac build scripts in a Windows PR — that's how the Mac app stays safe.
- Benjamin reviews/merges PRs (or merge once approved). Small follow-ups (e.g. the two
  security fixes from the PR #1 review) can be added to the same branch before merge.

---

## B. Bump the version (two files, keep in sync)

Like the Mac (`build-app.sh` + `make-dist.sh`), the Windows version lives in **two** places — bump both:

1. `windows/Directory.Build.props` → `<Version>0.2.0</Version>`
2. `windows/installer/bhserve.iss` → `#define MyAppVersion "0.2.0"`

(The in-app updater compares this `Version` against the GitHub release tag.)

---

## C. Build the INSTALLER

On the Windows machine (VS 2022 + .NET 8 SDK + Windows App SDK + Inno Setup 6 installed):

```powershell
cd windows
.\build.ps1                 # dotnet publish (self-contained) + iscc
# → windows\installer\dist\BHServe-Setup-<ver>.exe
```

Smoke-test the `.exe` on a **clean** Windows user/VM: install → launch → `bhserve init` →
add a site → it serves over HTTP/HTTPS → tray + GUI work. Unsigned builds trip SmartScreen
("Windows protected your PC" → **More info → Run anyway**) — document that for users in the
release notes (a code-signing cert removes it later).

---

## D. Cut the GitHub RELEASE — as a **pre-release**, with a `win-` tag

**This is the critical part for not disturbing the Mac.** While Windows is pre-1.0:

```powershell
gh release create win-v0.2.0 `
  windows\installer\dist\BHServe-Setup-0.2.0.exe `
  --prerelease `
  --title "BHServe for Windows 0.2.0 (preview)" `
  --notes "Windows preview build. <what's new>. Unsigned — SmartScreen: More info -> Run anyway."
```

Why this shape:
- **`--prerelease`** → GitHub keeps it **out of "Latest"**. The **macOS in-app updater reads
  `releases/latest`**, so a Windows pre-release can never show up as a Mac update. (Belt-and-suspenders:
  the Mac updater also only matches a `.pkg` asset, and a Windows release ships only `.exe`.)
- **`win-vX.Y.Z` tag** → a separate tag namespace from the Mac's `vX.Y.Z`, so Windows and Mac
  releases never collide and the changelog stays readable.
- Attach **only the `.exe`** (the installer already bundles the app + CLI + elevate helper).

---

## E. The Windows in-app updater (`BHServe.App/Services/Updater.cs`)

Because Windows ships as a **pre-release** for now, the updater must NOT use `releases/latest`
(that returns the Mac stable). Instead query **all releases** and pick the newest **`win-*`** tag:

```
GET https://api.github.com/repos/wpexpertinbd/BHServe/releases
→ filter tag_name starts with "win-v"
→ take the highest version, find the asset ending ".exe"
→ if newer than this build's Version → offer download+run (then exit so the installer can replace it)
```

(Mirror the Mac flow in `macos/Sources/BHServe/AppState.swift` `checkForUpdate` / `downloadAndInstall`,
just with `.exe` instead of `.pkg`, and the `win-` tag filter instead of `/latest`.)

---

## F. When Windows reaches 1.0 — switch to **unified releases**

Once Windows is stable, stop using `win-` pre-releases and move to **one release per version
carrying every platform's installer**:

- Tag `vX.Y.Z` (shared), **not** a pre-release.
- Assets: `BHServe-<ver>.pkg` (mac) + `BHServe-<ver>.dmg` (mac) + `BHServe-Setup-<ver>.exe` (win) [+ `.deb`/`.AppImage` later].
- Each platform's updater reads `releases/latest` and picks **its own** asset (`.pkg` / `.exe` / `.deb`).
- At that point, switch the Windows updater from the `win-*` filter back to `/latest`.

This is the VS Code / Obsidian model: one repo, one version, per-OS assets, one changelog.

---

## Pre-publish checklist

- [ ] Synced `master`; Windows changes scoped to `windows/` (Mac app untouched)
- [ ] **PR #1 security fixes landed** (validate `domain` in the elevate helper + in `Secure()`)
- [ ] Version bumped in **both** `Directory.Build.props` and `bhserve.iss`
- [ ] `build.ps1` produced `BHServe-Setup-<ver>.exe`; installed + smoke-tested on a clean Windows
- [ ] Release cut with **`--prerelease`** and a **`win-v<ver>`** tag, `.exe` attached
- [ ] Updater verified to pick up the new `win-*` release
