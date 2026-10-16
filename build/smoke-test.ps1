<#
.SYNOPSIS
  Proves the Windows installer works: installs it silently for this user, starts the app, asks it to quit,
  then uninstalls it, failing loudly at the first thing that goes wrong. Used by CI and by Ship.
.EXAMPLE
  ./build/smoke-test.ps1    # after ./build/publish.ps1
#>
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$setup = Join-Path $root "artifacts/installer/ImmichSplash-Setup.exe"
Start-Process $setup -ArgumentList "/VERYSILENT", "/SUPPRESSMSGBOXES", "/CURRENTUSER", "/NORESTART" -Wait
$dir = "$env:LOCALAPPDATA\Programs\Immich Splash"
if (-not (Test-Path "$dir\ImmichSplash.exe")) { throw "App was not installed to $dir" }
if (-not (Test-Path "$dir\unins000.exe")) { throw "Uninstaller missing" }
Write-Host "Installed OK"
$env:IMMICH_SPLASH_DATA_DIR = Join-Path ([IO.Path]::GetTempPath()) "immich-splash-smoke" # a throwaway profile
$app = Start-Process "$dir\ImmichSplash.exe" -ArgumentList "--background" -PassThru
Start-Sleep -Seconds 10
if ($app.HasExited) { throw "App exited during startup (code $($app.ExitCode))" }
Write-Host "App running OK"
Start-Process "$dir\ImmichSplash.exe" -ArgumentList "--quit" -Wait
Start-Sleep -Seconds 3
if (-not $app.HasExited) { throw "App did not quit when asked" }
Write-Host "Quit OK"
Start-Process "$dir\unins000.exe" -ArgumentList "/VERYSILENT", "/SUPPRESSMSGBOXES" -Wait
Start-Sleep -Seconds 3
if (Test-Path "$dir\ImmichSplash.exe") { throw "Uninstall left the app behind" }
Write-Host "Uninstalled OK"
