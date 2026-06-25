# Antivirus & SmartScreen notes

BHServe for Windows is **open-source and unsigned** (no paid code-signing certificate).
Because of that, two things can happen on a fresh machine — both are false positives, and
both are easy to clear.

## 1. SmartScreen "unknown publisher" on the installer

When you run `BHServe-Setup-0.1.0.exe`, Windows SmartScreen may show a blue "Windows
protected your PC" dialog. Click **More info → Run anyway**. This is expected for any
unsigned installer and does **not** mean the file is unsafe.

## 2. Antivirus may quarantine the app when it installs services

BHServe is a *local web-server manager*: it downloads server binaries (nginx, PHP, MySQL,
Redis, …) and runs them on demand. Some antivirus products (notably **ESET**) have a
*behavioral* scanner that flags **any unsigned program that downloads and launches other
executables** as a generic "dropper" (e.g. `Win64/Agent_AGen`). BHServe does exactly that —
legitimately — so it can trip that heuristic and the app may get deleted/quarantined.

BHServe already minimizes this: every download is performed by Windows' own **signed**
`curl.exe` + `tar.exe`, and known junk helper files are excluded. But the behavioral verdict
is about *reputation*, which only a code signature fully solves — so on a strict AV you may
still need to allow it once.

### Fix (one time, ~1 minute)

1. **Restore the app if it was already quarantined.**
   - ESET: **Tools → Quarantine** → select `BHServe.App.exe` → **Restore**. (Or just re-run the installer.)
2. **Add exclusions** so it won't be flagged again. Exclude these two folders:
   - `C:\Program Files\BHServe`
   - `%LOCALAPPDATA%\BHServe`  (i.e. `C:\Users\<you>\AppData\Local\BHServe`)

   **ESET:** Setup → **Computer** → **Detection exclusions** (and **Performance exclusions**) → **Add** → pick each folder.
   **Windows Defender:** Settings → Privacy & security → Windows Security → Virus & threat protection →
   *Manage settings* → *Exclusions* → **Add an exclusion → Folder** → pick each folder.
   Or, in an **admin** PowerShell:
   ```powershell
   Add-MpPreference -ExclusionPath "$env:ProgramFiles\BHServe", "$env:LOCALAPPDATA\BHServe"
   ```
3. Launch BHServe and install the services you need — it will work normally.

> Do this **before** installing services from the dashboard, so the very first download isn't interrupted.

### Why we don't just bundle everything

Bundling all the server binaries inside the installer would avoid the runtime download (and the
behavioral flag), but it makes the download ~300 MB instead of ~40 MB. We keep the installer small
and download on demand. If you'd rather have an offline/bundled build, it can be produced with
`build.ps1 -Bundle`.

### Maintainers: the real fix

A free **open-source code-signing certificate** (e.g. via the **SignPath Foundation**) removes both
the SmartScreen prompt and the behavioral false positive across all major AV engines. `build.ps1`
already supports signing — pass `-CertPath` / `-CertSubject`. Submitting the binaries to each AV
vendor's false-positive portal (ESET, Microsoft, etc.) is a free supplement but is per-vendor and
per-build.
