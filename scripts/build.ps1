# Builds Pane, Pane Host and the one-click Pane Host Setup as single-file x64 exes into dist/.
# The host is published first, staged into the setup project, then embedded in the setup exe so the
# installer is a single double-clickable file. Run from anywhere:  pwsh scripts/build.ps1
$ErrorActionPreference = "Stop"

$root   = Split-Path -Parent $PSScriptRoot
$dist   = Join-Path $root "dist"
$embed  = Join-Path $root "src\PaneHostSetup\embed"
$dotnet = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) { $dotnet = "dotnet" }   # fall back to PATH
$env:DOTNET_CLI_TELEMETRY_OPTOUT = 1
$env:DOTNET_NOLOGO = 1

New-Item -ItemType Directory -Force -Path $dist, $embed | Out-Null

$common = @("-c","Release","-r","win-x64","--self-contained","true",
            "-p:PublishSingleFile=true","-p:IncludeNativeLibrariesForSelfExtract=true",
            "-p:EnableCompressionInSingleFile=true","-p:DebugType=none")

function Publish($proj, $outDir) {
    Write-Host "Publishing $proj ..." -ForegroundColor Cyan
    & $dotnet publish (Join-Path $root $proj) @common -o $outDir
    if ($LASTEXITCODE -ne 0) { throw "publish failed: $proj" }
}

$stage = Join-Path $env:TEMP "pane-publish"
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue

# 1. Host -> stage, then copy into dist and the setup embed folder.
Publish "src\PaneHost\PaneHost.csproj" (Join-Path $stage "host")
Copy-Item (Join-Path $stage "host\PaneHost.exe") (Join-Path $dist "PaneHost.exe") -Force
Copy-Item (Join-Path $stage "host\PaneHost.exe") (Join-Path $embed "PaneHost.exe") -Force

# 2. Setup (embeds the host staged above).
Publish "src\PaneHostSetup\PaneHostSetup.csproj" (Join-Path $stage "setup")
Copy-Item (Join-Path $stage "setup\PaneHostSetup.exe") (Join-Path $dist "PaneHostSetup.exe") -Force

# 3. Client.
Publish "src\Pane\Pane.csproj" (Join-Path $stage "client")
Copy-Item (Join-Path $stage "client\Pane.exe") (Join-Path $dist "Pane.exe") -Force

Write-Host ""
Write-Host "Done. Artifacts in dist\:" -ForegroundColor Green
Get-ChildItem $dist -File | Where-Object Extension -eq ".exe" |
    Select-Object Name, @{n="MB";e={[math]::Round($_.Length/1MB,1)}} | Format-Table -AutoSize
