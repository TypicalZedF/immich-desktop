<#
.SYNOPSIS
  Waits for CI on a branch and reports in a few lines (errors only when something fails).
  Releases are followed by ./build/ship.ps1 (or on the Ship run's page in Actions).
.EXAMPLE
  ./build/watch.ps1                 # CI on origin/beta
  ./build/watch.ps1 -Ref main
#>
param(
    [string]$Ref = "beta",
    [string[]]$Workflows,
    [int]$TimeoutMinutes = 45
)
. "$PSScriptRoot/common.ps1"
Require-Gh

GitRun fetch --quiet --tags origin | Out-Null
if (-not (Test-RemoteBranch $Ref)) { Fail "There's no $Ref branch on GitHub (a stable release removes beta)." }
$sha = GitRun rev-parse "origin/$Ref"
if (-not $Workflows) { $Workflows = @("CI") }
if (-not (Wait-Workflows $sha $Workflows $Ref $TimeoutMinutes)) { exit 1 }
