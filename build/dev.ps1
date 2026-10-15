<#
.SYNOPSIS
  Puts the code you have right now on your own machines in about a minute, without a beta release.

.DESCRIPTION
  Windows: builds the app and swaps it into your installed copy (%LOCALAPPDATA%\Programs\Immich Splash),
  then starts it. Your settings, sign-in and backup state are kept.
  Linux (-Linux): builds the Linux app on this PC and, with -LinuxHost, copies it over SSH to a Linux
  machine, installs it to ~/.local/opt/immich-splash (like the tarball) and starts it there.

  Dev builds are versioned <next>-dev.<time>, e.g. 1.5.2-dev.10151342: newer than the current betas, so the
  app doesn't "update" back to them, and the next stable release or newer beta replaces it as usual.

.EXAMPLE
  ./build/dev.ps1                                # this PC
  ./build/dev.ps1 -Linux -LinuxHost me@fedora.local   # this PC and the laptop (the host is remembered)
  ./build/dev.ps1 -Linux -NoWindows              # only the laptop
#>
param(
    [switch]$Linux,
    [string]$LinuxHost,
    [switch]$NoWindows
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root "src/ImmichSplash.App/ImmichSplash.App.csproj"
$devDir = Join-Path $root "artifacts/dev"
New-Item -ItemType Directory -Force $devDir | Out-Null

# ---- Version: the next release, marked as a dev build ----
git -C $root fetch --tags --quiet 2>$null
$latest = git -C $root tag --list "v*" --sort=-v:refname | Select-Object -First 1
$base = "1.0.0"
if ($latest -match '^v(\d+)\.(\d+)\.(\d+)(-.+)?$') {
    $patch = [int]$Matches[3]
    if (-not $Matches[4]) { $patch++ }   # after a stable release, the next one is a patch up (like the betas)
    $base = "$($Matches[1]).$($Matches[2]).$patch"
}
$version = "$base-dev.$(Get-Date -Format MMddHHmm)"
Write-Host "==> Dev build $version" -ForegroundColor Cyan

# ---- Windows ----
if (-not $NoWindows) {
    $installDir = Join-Path $env:LOCALAPPDATA "Programs\Immich Splash"
    # Installs made before the rename keep their original folder.
    $oldDir = Join-Path $env:LOCALAPPDATA "Programs\Immich Desktop"
    if (-not (Test-Path $installDir) -and (Test-Path $oldDir)) { $installDir = $oldDir }
    $installedExe = Join-Path $installDir "ImmichSplash.exe"

    # libmpv (videos): reuse the installed app's copy if this checkout doesn't have it yet.
    $mpvDll = Join-Path $root "lib/mpv/libmpv-2.dll"
    $installedMpv = Join-Path $installDir "libmpv-2.dll"
    if (-not (Test-Path $mpvDll) -and (Test-Path $installedMpv)) {
        New-Item -ItemType Directory -Force (Split-Path $mpvDll) | Out-Null
        Copy-Item $installedMpv $mpvDll
    }
    if (-not (Test-Path $mpvDll)) {
        Write-Warning "libmpv-2.dll is missing, so videos won't play in this build. Run ./build/publish.ps1 once to download it."
    }

    $out = Join-Path $devDir "win-x64"
    Write-Host "==> Building for Windows" -ForegroundColor Cyan
    dotnet publish $project -c Release -r win-x64 --self-contained true -p:Version=$version -p:DebugType=none -o $out --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "The Windows build failed" }

    if (Test-Path $installedExe) {
        Write-Host "==> Replacing the installed app" -ForegroundColor Cyan
        & $installedExe --quit | Out-Null
        $deadline = (Get-Date).AddSeconds(15)
        while ((Get-Process ImmichSplash -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $installedExe }) -and (Get-Date) -lt $deadline) {
            Start-Sleep -Milliseconds 300
        }
        # Copy over the installed files (the uninstaller and its data stay).
        robocopy $out $installDir /E /NFL /NDL /NJH /NJS /NP /R:5 /W:1 | Out-Null
        if ($LASTEXITCODE -ge 8) { throw "Couldn't copy the build into $installDir (robocopy exit $LASTEXITCODE)" }
        Start-Process $installedExe
        Write-Host "    Running $version from $installDir" -ForegroundColor Green
    }
    else {
        Write-Host "    Immich Splash isn't installed here; starting the build from $out" -ForegroundColor Yellow
        Start-Process (Join-Path $out "ImmichSplash.exe")
    }
}

# ---- Linux ----
if ($Linux) {
    $hostFile = Join-Path $devDir "linux-host.txt"
    if ($LinuxHost) { Set-Content -Path $hostFile -Value $LinuxHost -Encoding ascii }
    elseif (Test-Path $hostFile) { $LinuxHost = (Get-Content $hostFile -Raw).Trim() }

    Write-Host "==> Building for Linux" -ForegroundColor Cyan
    $out = Join-Path $devDir "linux-x64"
    if (Test-Path $out) { Remove-Item -Recurse -Force $out }
    dotnet publish $project -c Release -r linux-x64 --self-contained true -p:Version=$version -p:DebugType=none -o $out --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "The Linux build failed" }

    # Same layout as the release tarball: immich-splash/{app, immich-splash.png, install.sh}
    $stage = Join-Path $devDir "stage"
    if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
    $pkg = Join-Path $stage "immich-splash"
    New-Item -ItemType Directory -Force $pkg | Out-Null
    Copy-Item -Recurse $out (Join-Path $pkg "app")
    Copy-Item (Join-Path $root "src/ImmichSplash.App/Assets/logo-512.png") (Join-Path $pkg "immich-splash.png")
    $install = (Get-Content (Join-Path $root "build/linux-install.sh") -Raw) -replace "`r`n", "`n"
    [IO.File]::WriteAllText((Join-Path $pkg "install.sh"), $install)
    $tarball = Join-Path $devDir "ImmichSplash-dev-linux-x64.tar.gz"
    tar -C $stage -czf $tarball immich-splash
    if ($LASTEXITCODE -ne 0) { throw "Couldn't pack the Linux build" }

    if ($LinuxHost) {
        Write-Host "==> Installing on $LinuxHost" -ForegroundColor Cyan
        scp -q $tarball "${LinuxHost}:/tmp/immich-splash-dev.tar.gz"
        if ($LASTEXITCODE -ne 0) { throw "Couldn't copy the build to $LinuxHost (is SSH set up? See CONTRIBUTING.md)" }
        # Close the Flatpak copy too (only one may run), install, then start it in the desktop session.
        $remote = 'set -e; d=$(mktemp -d); tar -xzf /tmp/immich-splash-dev.tar.gz -C "$d"; ' +
                  'flatpak kill io.github.TypicalZedF.ImmichSplash 2>/dev/null || true; ' +
                  'bash "$d/immich-splash/install.sh"; rm -rf "$d"; ' +
                  'systemd-run --user --collect --quiet "$HOME/.local/opt/immich-splash/ImmichSplash"'
        ssh $LinuxHost $remote
        if ($LASTEXITCODE -ne 0) { throw "Installing on $LinuxHost failed" }
        Write-Host "    Running $version on $LinuxHost" -ForegroundColor Green
    }
    else {
        Write-Host "    Ready: $tarball" -ForegroundColor Green
        Write-Host "    Copy it to the Linux machine, extract it and run ./immich-splash/install.sh"
        Write-Host "    (or pass -LinuxHost user@machine to do that over SSH)."
    }
}
