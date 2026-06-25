# BHServe (Windows) - build distributables.
#
#   .\build.ps1              # DEFAULT: small installer (just the dashboard + CLI). Services
#                            # are downloaded on demand - and that's antivirus-safe because
#                            # BHServe fetches/extracts via Windows' SIGNED curl.exe + tar.exe
#                            # (so bhserve.exe is never the process that drops executables).
#   .\build.ps1 -Bundle      # OPTIONAL offline variant: also ship the server binaries inside
#                            # the installer (bigger download, no on-demand fetch).
#
# Signing is OPTIONAL - it only affects the SmartScreen "unknown publisher" prompt (users
# click "Run anyway"). No certificate purchase is needed. Pass -CertPath / -CertSubject only
# if you happen to have one (e.g. a free SignPath.io OSS cert).
param(
    [string]$Rid = "win-x64",
    [string]$Configuration = "Release",
    [switch]$Bundle,                 # pre-download the server stack into the installer payload
    [string]$CertPath = "",          # path to a .pfx code-signing cert (or use -CertSubject for an installed cert)
    [string]$CertPassword = "",
    [string]$CertSubject = "",       # alternatively, the subject name of a cert in the Windows store
    [string]$TimestampUrl = "http://timestamp.sectigo.com"
)
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

# ── publish the three exes self-contained ───────────────────────────────────────
Write-Host "build  publishing app + cli + elevate ($Rid)..." -ForegroundColor Cyan
$publish = Join-Path $PSScriptRoot "publish"
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

dotnet publish src/BHServe.App/BHServe.App.csproj -c $Configuration -r $Rid --self-contained true -p:WindowsAppSDKSelfContained=true -o $publish
dotnet publish src/BHServe.Cli/BHServe.Cli.csproj -c $Configuration -r $Rid --self-contained true -o $publish
dotnet publish src/BHServe.Elevate/BHServe.Elevate.csproj -c $Configuration -r $Rid --self-contained true -o $publish

# ── optional: bundle the server binaries (no runtime downloads -> no "dropper" AV flag) ──
$payloadBin = Join-Path $PSScriptRoot "payload\bin"
if ($Bundle) {
    Write-Host "build  bundling server binaries (this downloads nginx/php/mysql/redis/...)..." -ForegroundColor Cyan
    $payload = Join-Path $PSScriptRoot "payload"
    if (Test-Path $payload) { Remove-Item $payload -Recurse -Force }
    New-Item -ItemType Directory -Force $payload | Out-Null
    # Use the freshly-published CLI to populate payload\bin via BHSERVE_HOME.
    $cli = Join-Path $publish "bhserve.exe"
    $env:BHSERVE_HOME = $payload
    & $cli init | Out-Null
    # Bundle every exe-producing tool so a user NEVER triggers a runtime download
    # (that download behavior is what antivirus flags as a "dropper"). Site tools
    # (adminer/phpMyAdmin/WordPress) are plain PHP files, not exes - safe on demand.
    foreach ($t in @("nginx", "php@8.4", "mariadb", "mkcert", "redis", "memcached", "mailpit", "fnm", "cloudflared")) {
        Write-Host "         install $t" -ForegroundColor DarkGray
        & $cli install $t
    }
    Remove-Item Env:\BHSERVE_HOME
    if (-not (Test-Path (Join-Path $payloadBin "nginx"))) { Write-Warning "bundle payload looks incomplete - check the install output above." }
}

# ── code signing (the part that actually silences antivirus/SmartScreen) ─────────
function Find-SignTool {
    $roots = @("${env:ProgramFiles(x86)}\Windows Kits\10\bin", "${env:ProgramFiles}\Windows Kits\10\bin")
    foreach ($r in $roots) {
        if (Test-Path $r) {
            $st = Get-ChildItem $r -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
                  Where-Object { $_.FullName -match 'x64' } | Sort-Object FullName -Descending | Select-Object -First 1
            if ($st) { return $st.FullName }
        }
    }
    return $null
}

$signing = $CertPath -or $CertSubject
if ($signing) {
    $signtool = Find-SignTool
    if (-not $signtool) { Write-Warning "signtool.exe not found (install the Windows SDK). Skipping signing." ; $signing = $false }
}
function Invoke-Sign($file) {
    if (-not $signing) { return }
    $args = @("sign", "/fd", "SHA256", "/tr", $TimestampUrl, "/td", "SHA256")
    if ($CertPath)    { $args += @("/f", $CertPath); if ($CertPassword) { $args += @("/p", $CertPassword) } }
    elseif ($CertSubject) { $args += @("/n", $CertSubject) }
    $args += $file
    & $signtool @args
    if ($LASTEXITCODE -ne 0) { throw "signtool failed for $file" }
}
if ($signing) {
    Write-Host "build  signing payload exes..." -ForegroundColor Cyan
    Get-ChildItem $publish -Filter *.exe | ForEach-Object { Invoke-Sign $_.FullName }
    if ($Bundle -and (Test-Path $payloadBin)) {
        Get-ChildItem $payloadBin -Recurse -Filter *.exe | ForEach-Object { Invoke-Sign $_.FullName }
    }
}

# ── installer ────────────────────────────────────────────────────────────────────
Write-Host "build  installer (Inno Setup)..." -ForegroundColor Cyan
$pf86 = [Environment]::GetFolderPath('ProgramFilesX86')
$pf   = [Environment]::GetFolderPath('ProgramFiles')
$lad  = [Environment]::GetFolderPath('LocalApplicationData')
$iscc = @(
    (Join-Path $pf86 'Inno Setup 6\ISCC.exe'),
    (Join-Path $pf   'Inno Setup 6\ISCC.exe'),
    (Join-Path $lad  'Programs\Inno Setup 6\ISCC.exe')
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    Write-Warning "Inno Setup not found. Install from https://jrsoftware.org/isdl.php to build the installer."
    Write-Host "Published app is in: $publish" -ForegroundColor Yellow
    exit 0
}
# Pass /DBundle=1 to Inno when a payload exists so it ships {app}\bin.
$bundleDef = if ($Bundle -and (Test-Path $payloadBin)) { @("/DBundle=1") } else { @() }
& $iscc @bundleDef "installer\bhserve.iss"

# Sign the installer itself (last, so the file users download is trusted).
$setup = Get-ChildItem (Join-Path $PSScriptRoot "installer\dist") -Filter *.exe -ErrorAction SilentlyContinue |
         Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($signing -and $setup) { Write-Host "build  signing installer..." -ForegroundColor Cyan; Invoke-Sign $setup.FullName }

Write-Host "done   installer is in windows\installer\dist" -ForegroundColor Green
if (-not $signing) { Write-Host "NOTE: unsigned - Windows SmartScreen shows 'unknown publisher' (More info -> Run anyway). That's expected; no cert needed." -ForegroundColor DarkGray }
