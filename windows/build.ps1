# BHServe (Windows) — build distributables.
# Publishes the WinUI app self-contained, then builds the Inno Setup installer.
# Run from the windows/ dir:  .\build.ps1  [-Rid win-x64]
param(
    [string]$Rid = "win-x64",          # or win-arm64
    [string]$Configuration = "Release"
)
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

Write-Host "build  publishing BHServe.App ($Rid)..." -ForegroundColor Cyan
$publish = Join-Path $PSScriptRoot "publish"
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

dotnet publish src/BHServe.App/BHServe.App.csproj `
    -c $Configuration -r $Rid --self-contained true `
    -p:WindowsAppSDKSelfContained=true `
    -o $publish

# also drop the CLI next to the app so `bhserve.exe` ships too
dotnet publish src/BHServe.Cli/BHServe.Cli.csproj `
    -c $Configuration -r $Rid --self-contained true `
    -o $publish

# and the privileged helper (bhserve-elevate.exe) — hosts file + mkcert CA via one UAC prompt
dotnet publish src/BHServe.Elevate/BHServe.Elevate.csproj `
    -c $Configuration -r $Rid --self-contained true `
    -o $publish

Write-Host "build  installer (Inno Setup)..." -ForegroundColor Cyan
# ISCC may live under Program Files, Program Files (x86), or a per-user winget install.
$pf86 = [Environment]::GetFolderPath('ProgramFilesX86')
$pf   = [Environment]::GetFolderPath('ProgramFiles')
$lad  = [Environment]::GetFolderPath('LocalApplicationData')
$isccCandidates = @(
    (Join-Path $pf86 'Inno Setup 6\ISCC.exe'),
    (Join-Path $pf   'Inno Setup 6\ISCC.exe'),
    (Join-Path $lad  'Programs\Inno Setup 6\ISCC.exe')
)
$iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    Write-Warning "Inno Setup not found. Install from https://jrsoftware.org/isdl.php to build the installer."
    Write-Host "Published app is in: $publish" -ForegroundColor Yellow
    exit 0
}
& $iscc "installer\bhserve.iss"
Write-Host "done   installer is in windows\installer\dist" -ForegroundColor Green
