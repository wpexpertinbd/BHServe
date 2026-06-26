# Mac parity TODO — port these Windows features to the macOS build

Two features landed on the Windows build that the macOS build (engine `engine/bhserve` + the
Mac app) should mirror. Keep the update channels separate: **macOS = `v1.6.x` tags**, **Windows
= `win-v1.0.x` tags**.

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
