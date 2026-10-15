<#
.SYNOPSIS
  Build and test in one call, printing only errors, failed tests and a summary line.
.EXAMPLE
  ./build/check.ps1                     # build everything, run all tests
  ./build/check.ps1 -Filter SyncEngine  # only tests whose name contains SyncEngine
  ./build/check.ps1 -NoTest             # build only
#>
param(
    [string]$Filter,
    [switch]$NoTest
)
. "$PSScriptRoot/common.ps1"
$ErrorActionPreference = "Continue"
$started = Get-Date

$build = dotnet build "$script:Root/ImmichDesktop.slnx" -v q -nologo 2>&1 | ForEach-Object { "$_" }
$errors = @($build | Where-Object { $_ -match ': error ' } | ForEach-Object { ($_ -replace [regex]::Escape("$script:Root\"), '' -replace '\s+\[[^\]]+\.csproj\]$', '').Trim() } | Select-Object -Unique)
if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed ($($errors.Count) errors)" -ForegroundColor Red
    $errors | Select-Object -First 20 | ForEach-Object { "  $_" } | Write-Host
    if ($errors.Count -eq 0) { $build | Select-Object -Last 15 | Write-Host }
    exit 1
}
$warnings = @($build | Where-Object { $_ -match ': warning ' } | Select-Object -Unique).Count
Write-Host "Build ok ($warnings warnings)" -ForegroundColor Green
if ($NoTest) { exit 0 }

$testArgs = @("test", "$script:Root/tests/ImmichDesktop.Core.Tests", "--no-build", "-v", "m", "--nologo")
if ($Filter) { $testArgs += @("--filter", "FullyQualifiedName~$Filter") }
$test = & dotnet @testArgs 2>&1 | ForEach-Object { "$_" }
$code = $LASTEXITCODE
# Failed test names plus their first few assertion/exception lines.
$show = 0
foreach ($line in $test) {
    if ($line -match '^\s*(Failed|\[FAIL\])\s') { Write-Host "  $($line.Trim())" -ForegroundColor Red; $show = 4; continue }
    if ($show -gt 0 -and $line.Trim()) { Write-Host "    $($line.Trim())"; $show-- }
}
$summary = $test | Where-Object { $_ -match '(Passed|Failed)!|Total tests|total:|No test matches' } | Select-Object -Last 1
$elapsed = [int]((Get-Date) - $started).TotalSeconds
if ("$summary" -match 'No test matches') { Write-Host "No tests match '$Filter'  (${elapsed}s)" -ForegroundColor Yellow; exit 1 }
if ($code -ne 0) { Write-Host "Tests failed: $($summary.Trim())  (${elapsed}s)" -ForegroundColor Red; exit 1 }
Write-Host "Tests ok: $("$summary".Trim())  (${elapsed}s)" -ForegroundColor Green
