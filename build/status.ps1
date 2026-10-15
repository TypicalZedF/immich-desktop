<#
.SYNOPSIS
  One short screen: where beta and main are, local changes, the latest releases and the latest checks.
.EXAMPLE
  ./build/status.ps1
#>
. "$PSScriptRoot/common.ps1"
Require-Gh

GitRun fetch --quiet --tags origin | Out-Null
$branch = GitRun rev-parse --abbrev-ref HEAD
$dirty = @(GitRun status --porcelain).Count
$hasBeta = Test-RemoteBranch beta
$upstream = if (Test-RemoteBranch $branch) { "origin/$branch" } else { "origin/main" }
$aheadBehind = (GitRun rev-list --left-right --count "$upstream...HEAD") -split '\s+'

Write-Host "Branch   $branch  (ahead $($aheadBehind[1]), behind $($aheadBehind[0]) of $upstream; $dirty changed files)"
if ($hasBeta) {
    $betaAhead = GitRun rev-list --count origin/main..origin/beta
    Write-Host "Beta     $(GitRun log -1 --format='%h %s' origin/beta)  ($betaAhead commits not yet stable)"
}
else { Write-Host "Beta     none (removed by the last stable release; ./build/beta.ps1 starts it again)" }
Write-Host "Main     $(GitRun log -1 --format='%h %s' origin/main)"

$ErrorActionPreference = "Continue"
$releases = gh release list --repo $script:Repo --limit 20 --json tagName,isPrerelease,isLatest,publishedAt 2>$null | ConvertFrom-Json
$stable = $releases | Where-Object { -not $_.isPrerelease } | Select-Object -First 1
$beta = $releases | Where-Object { $_.isPrerelease } | Select-Object -First 1
Write-Host "Stable   $($stable.tagName)  ($(([datetime]$stable.publishedAt).ToLocalTime().ToString('g')))"
if ($beta) { Write-Host "Beta rel $($beta.tagName)  ($(([datetime]$beta.publishedAt).ToLocalTime().ToString('g')))" }

$checkRef = if ($hasBeta) { "beta" } else { "main" }
$sha = GitRun rev-parse "origin/$checkRef"
$runs = @(Get-Runs $sha $checkRef)
$line = ($runs | ForEach-Object { if ($_.status -eq "completed") { "$($_.workflowName) $($_.conclusion)" } else { "$($_.workflowName) $($_.status)" } }) -join ", "
if (-not $line) { $line = "none yet" }
Write-Host "Checks   $line  (on $checkRef)"
