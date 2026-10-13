<#
.SYNOPSIS
  Builds a release and packages the Windows installer (Setup.exe) with Velopack.

.EXAMPLE
  ./build/publish.ps1 -Version 1.0.0
  Output: artifacts/releases/win-x64/ImmichDesktop-win-Setup.exe (+ update packages for GitHub Releases)
#>
param(
    [string]$Version = "1.0.0",
    [ValidateSet("win-x64", "win-arm64")]
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root "src/ImmichDesktop.App/ImmichDesktop.App.csproj"
$publishDir = Join-Path $root "artifacts/publish/$Runtime"
$releaseDir = Join-Path $root "artifacts/releases/$Runtime"
$vpkVersion = "1.2.161"   # keep in sync with the Velopack package in Directory.Packages.props

Write-Host "==> Running tests" -ForegroundColor Cyan
dotnet test (Join-Path $root "tests/ImmichDesktop.Core.Tests") -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw "Tests failed" }

Write-Host "==> Publishing $Runtime $Version" -ForegroundColor Cyan
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
dotnet publish $project -c Release -r $Runtime --self-contained true `
    -p:Version=$Version -p:PublishReadyToRun=true -p:DebugType=none `
    -o $publishDir --nologo
if ($LASTEXITCODE -ne 0) { throw "Publish failed" }

Write-Host "==> Packaging installer" -ForegroundColor Cyan
if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
    dotnet tool install -g vpk --version $vpkVersion
    $env:PATH += ";$env:USERPROFILE\.dotnet\tools"
}

vpk pack `
    --packId ImmichDesktop `
    --packVersion $Version `
    --packDir $publishDir `
    --mainExe ImmichDesktop.exe `
    --packTitle "Immich Desktop" `
    --packAuthors "Immich Desktop" `
    --icon (Join-Path $root "src/ImmichDesktop.App/Assets/app.ico") `
    --runtime $Runtime `
    --outputDir $releaseDir
if ($LASTEXITCODE -ne 0) { throw "Packaging failed" }

Write-Host ""
Write-Host "Installer ready:" -ForegroundColor Green
Get-ChildItem $releaseDir -Filter "*Setup.exe" | ForEach-Object { Write-Host "  $($_.FullName)  ($([math]::Round($_.Length / 1MB, 1)) MB)" }
