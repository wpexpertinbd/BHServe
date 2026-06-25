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

The current state is a **scaffold**: `Engine` methods are stubs (they throw
`NotImplementedException` with a pointer to the port doc). Implement them in the
phase order from `WINDOWS-PORT.md` §6. The CLI verb surface and the GUI shell
(NavigationView with Dashboard/Services/Sites/Databases/Node/Logs/Settings) are
already wired so you build behavior, not boilerplate.

## Release

```powershell
.\build.ps1                 # -> windows\installer\dist\BHServe-Setup-<ver>.exe
```

Then cut a GitHub release with the `.exe` so the in-app updater (asset matcher:
`.exe`) finds it — same flow as the mac `.pkg`.
