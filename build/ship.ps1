<#
.SYNOPSIS
  Presses the Ship button from a terminal (the same as Actions → Ship → Run workflow), then follows the run
  and prints one line per step as it finishes.

.DESCRIPTION
  Ship ships the newest tested work on GitHub (the beta branch, or main when there's no beta): tests, builds
  Windows, Linux and the Flatpak, publishes the release, tidies up. See .github/workflows/ship.yml.
  Beta = a pre-release for the Beta update channel. Stable = a release for everyone.

.EXAMPLE
  ./build/ship.ps1                          # a beta
  ./build/ship.ps1 -Channel Stable          # the next stable version (1.6.2 -> 1.6.3)
  ./build/ship.ps1 -Channel Stable -Version 1.7.0
  ./build/ship.ps1 -NoWait                  # press it and return; the run's link is printed
#>
param(
    [ValidateSet("Beta", "Stable")]
    [string]$Channel = "Beta",
    [string]$Version,
    [switch]$NoWait
)
. "$PSScriptRoot/common.ps1"
Require-Gh
$ErrorActionPreference = "Continue"

# Started from main, like the button (GitHub only runs it from there); what gets shipped is decided inside the run.
$ref = "main"
$inputs = @("-f", "channel=$Channel")
if ($Version) { $inputs += @("-f", "version=$Version") }

Step "Pressing Ship ($Channel)"
$before = (gh run list --repo $script:Repo --workflow ship.yml --limit 1 --json databaseId --jq '.[0].databaseId' 2>$null)
gh workflow run ship.yml --repo $script:Repo --ref $ref @inputs
if ($LASTEXITCODE -ne 0) { Fail "GitHub didn't start it." }
$run = $null
for ($i = 0; $i -lt 15 -and -not $run; $i++) {
    Start-Sleep -Seconds 3
    $latest = gh run list --repo $script:Repo --workflow ship.yml --limit 1 --json databaseId,url | ConvertFrom-Json
    if ($latest -and "$($latest[0].databaseId)" -ne "$before") { $run = $latest[0] }
}
if (-not $run) { Fail "It started, but the run didn't show up yet. Look in Actions → Ship." }
Ok $run.url
if ($NoWait) { exit 0 }

$reported = @{}
while ($true) {
    $view = gh run view $run.databaseId --repo $script:Repo --json status,conclusion,jobs | ConvertFrom-Json
    foreach ($job in $view.jobs | Sort-Object name) {
        if ($job.status -ne "completed" -or $reported.ContainsKey($job.name)) { continue }
        $reported[$job.name] = $true
        switch ($job.conclusion) {
            "success" { Ok "$($job.name): done" }
            "skipped" { Note "$($job.name): not needed" }
            default { Write-Host "    $($job.name): $($job.conclusion)" -ForegroundColor Red }
        }
    }
    if ($view.status -eq "completed") { break }
    Start-Sleep -Seconds 20
}
if ($view.conclusion -eq "success") {
    $tag = gh release list --repo $script:Repo --limit 1 --json tagName --jq '.[0].tagName' 2>$null
    Ok "Shipped $tag  https://github.com/$($script:Repo)/releases/tag/$tag"
}
else {
    Show-Failure $run.databaseId
    Fail "Ship stopped. Fix it, then run this again (or press the button): it carries on with the same version."
}
