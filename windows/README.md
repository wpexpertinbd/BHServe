# BHServe for Windows

Native Windows build of BHServe (C# / .NET 8 + WinUI 3). See the full architecture
and port mapping in [`../docs/WINDOWS-PORT.md`](../docs/WINDOWS-PORT.md).

> Build and test this **on Windows** — WinUI, Windows services, the hosts file, and
> the installer can't be built or verified on macOS/Linux.

## Layout

```
windows/
  BHServe.sln
  src/BHServe.Core/   shared brains (paths, models, php-cgi mgr, engine) — net8.0
  src/BHServe.Cli/    bhserve.exe — transparent CLI over Core           — net8.0
  src/BHServe.App/    WinUI 3 GUI (unpackaged)                          — net8.0-windows
  installer/          Inno Setup script → BHServe-Setup.exe
  build.ps1           dotnet publish + iscc
```

## Prerequisites

- Visual Studio 2022 (17.10+) with **.NET desktop** + **Windows App SDK** workloads,
  or the standalone .NET 8 SDK + Windows App SDK.
- (For the installer) [Inno Setup 6](https://jrsoftware.org/isdl.php).

## Develop

```powershell
git clone https://github.com/wpexpertinbd/BHServe
cd BHServe\windows
dotnet build BHServe.sln
dotnet run --project src\BHServe.Cli -- init      # try the CLI
dotnet run --project src\BHServe.Cli -- status
# the GUI (run from Visual Studio for the best XAML/debug experience, or:)
dotnet run --project src\BHServe.App
```

### Status

**Phase 1 + 2 are implemented and working via `bhserve.exe`** (CLI-first):

- `init`, `install <nginx|php@8.4|mkcert>` (portable-zip downloads; falls back to a
  local Laragon install on dev boxes), `site add/rm/php`, `start/stop/restart`,
  `enable/disable`, `secure <domain>`, `status`/`api`.
- PHP runs as **`php-cgi.exe` over TCP** (no php-fpm on Windows) — port scheme
  `9100 + maj*10 + min` (8.4→9184); nginx uses `fastcgi_pass 127.0.0.1:<port>`.
- **HTTPS** via mkcert (`secure`) — issues a trusted cert, re-renders the vhost's
  ssl block, reloads nginx.
- Admin-only steps (hosts file, mkcert CA install) go through
  **`bhserve-elevate.exe`** (requireAdministrator) for a single UAC prompt.

The **WinUI GUI** (`BHServe.App`, NavigationView shell) is still a scaffold — that's
phase 3 (needs the Windows App SDK). `Db`/`Node`/pma/adminer/mailpit are phase 4.
See `WINDOWS-PORT.md` §6 for the remaining phases.

## Release

```powershell
.\build.ps1                 # -> windows\installer\dist\BHServe-Setup-<ver>.exe
```

Then cut a GitHub release with the `.exe` so the in-app updater (asset matcher:
`.exe`) finds it — same flow as the mac `.pkg`.
