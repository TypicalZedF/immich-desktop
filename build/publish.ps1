<#
.SYNOPSIS
  Builds a release and the Windows installer.

.DESCRIPTION
  Produces, in artifacts/installer:
    ImmichSplash-Setup.exe      setup wizard (choose install location, shortcuts, launch)
    ImmichSplash-Portable.zip   no-install version

  Needs Inno Setup 6 for the installer: winget install JRSoftware.InnoSetup

.EXAMPLE
  ./build/publish.ps1 -Version 1.2.0
#>
param(
    [string]$Version = "1.0.0",
    [ValidateSet("win-x64")]
    [string]$Runtime = "win-x64",
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root "src/ImmichSplash.App/ImmichSplash.App.csproj"
$publishDir = Join-Path $root "artifacts/publish/$Runtime"
$outputDir = Join-Path $root "artifacts/installer"

if (-not $SkipTests) {
    Write-Host "==> Running tests" -ForegroundColor Cyan
    dotnet test (Join-Path $root "tests/ImmichSplash.Core.Tests") -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw "Tests failed" }
}

# libmpv plays videos (pinned build from github.com/shinchiro/mpv-winbuild-cmake, checked by SHA-256).
$mpvDll = Join-Path $root "lib/mpv/libmpv-2.dll"
$mpvDllSha = "154a1610c266563e131c328349af396dd9af693eeb049d0c04ef196324541539"
if (-not (Test-Path $mpvDll) -or (Get-FileHash $mpvDll -Algorithm SHA256).Hash -ne $mpvDllSha) {
    Write-Host "==> Downloading libmpv" -ForegroundColor Cyan
    $mpvUrl = "https://github.com/shinchiro/mpv-winbuild-cmake/releases/download/20261015/mpv-dev-x86_64-20261015-git-492bae0768.7z"
    $mpvArchiveSha = "5db58c1768587b35f2d8daa11b53de4aec58197c00ea1d676f998f183a358ba8"
    $archive = Join-Path ([IO.Path]::GetTempPath()) "mpv-dev.7z"
    Invoke-WebRequest $mpvUrl -OutFile $archive
    if ((Get-FileHash $archive -Algorithm SHA256).Hash -ne $mpvArchiveSha) { throw "libmpv download doesn't match its pinned SHA-256" }
    $sevenZip = @((Get-Command 7z.exe -ErrorAction SilentlyContinue).Source, "$env:ProgramFiles\7-Zip\7z.exe") | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
    if (-not $sevenZip) { throw "7-Zip is needed to unpack libmpv" }
    New-Item -ItemType Directory -Force (Split-Path $mpvDll) | Out-Null
    & $sevenZip e $archive "-o$(Split-Path $mpvDll)" libmpv-2.dll -y | Out-Null
    if ((Get-FileHash $mpvDll -Algorithm SHA256).Hash -ne $mpvDllSha) { throw "libmpv-2.dll doesn't match its pinned SHA-256" }
    Remove-Item $archive
}

Write-Host "==> Publishing $Runtime $Version" -ForegroundColor Cyan
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
dotnet publish $project -c Release -r $Runtime --self-contained true `
    -p:Version=$Version -p:PublishReadyToRun=true -p:DebugType=none `
    -o $publishDir --nologo
if ($LASTEXITCODE -ne 0) { throw "Publish failed" }

New-Item -ItemType Directory -Force $outputDir | Out-Null

Write-Host "==> Portable zip" -ForegroundColor Cyan
$zip = Join-Path $outputDir "ImmichSplash-Portable.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zip -CompressionLevel Optimal

Write-Host "==> Installer" -ForegroundColor Cyan
$iscc = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source
if (-not $iscc) {
    $iscc = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $iscc) { throw "Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup" }

$fileVersion = $Version.Split('-')[0]
& $iscc /Qp "/DAppVersion=$Version" "/DFileVersion=$fileVersion" "/DSourceDir=$publishDir" "/DOutputDir=$outputDir" (Join-Path $PSScriptRoot "installer.iss")
if ($LASTEXITCODE -ne 0) { throw "Installer build failed" }

Write-Host ""
Write-Host "Ready:" -ForegroundColor Green
Get-ChildItem $outputDir | ForEach-Object { Write-Host ("  {0}  ({1:N1} MB)" -f $_.FullName, ($_.Length / 1MB)) }
