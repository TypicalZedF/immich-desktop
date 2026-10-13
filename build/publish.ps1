<#
.SYNOPSIS
  Builds a release and the Windows installer.

.DESCRIPTION
  Produces, in artifacts/installer:
    ImmichDesktop-Setup.exe      setup wizard (choose install location, shortcuts, launch)
    ImmichDesktop-Portable.zip   no-install version

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
$project = Join-Path $root "src/ImmichDesktop.App/ImmichDesktop.App.csproj"
$publishDir = Join-Path $root "artifacts/publish/$Runtime"
$outputDir = Join-Path $root "artifacts/installer"

if (-not $SkipTests) {
    Write-Host "==> Running tests" -ForegroundColor Cyan
    dotnet test (Join-Path $root "tests/ImmichDesktop.Core.Tests") -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw "Tests failed" }
}

Write-Host "==> Publishing $Runtime $Version" -ForegroundColor Cyan
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
dotnet publish $project -c Release -r $Runtime --self-contained true `
    -p:Version=$Version -p:PublishReadyToRun=true -p:DebugType=none `
    -o $publishDir --nologo
if ($LASTEXITCODE -ne 0) { throw "Publish failed" }

New-Item -ItemType Directory -Force $outputDir | Out-Null

Write-Host "==> Portable zip" -ForegroundColor Cyan
$zip = Join-Path $outputDir "ImmichDesktop-Portable.zip"
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
