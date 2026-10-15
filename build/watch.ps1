<#
.SYNOPSIS
  Waits for the GitHub checks on a commit or tag and reports in a few lines (errors only when something fails).
.EXAMPLE
  ./build/watch.ps1                 # the checks on origin/beta
  ./build/watch.ps1 -Ref v1.5.2     # a release tag (Release + Flatpak)
#>
param(
    [string]$Ref = "beta",
    [string[]]$Workflows,
    [int]$TimeoutMinutes = 45
)
. "$PSScriptRoot/common.ps1"
Require-Gh

GitRun fetch --quiet --tags origin | Out-Null
$isTag = $Ref -match '^v\d'
if (-not $isTag -and -not (Test-RemoteBranch $Ref)) { Fail "There's no $Ref branch on GitHub (a stable release removes beta)." }
if ($isTag) { $sha = GitRun rev-list -n 1 $Ref } else { $sha = GitRun rev-parse "origin/$Ref" }
if (-not $Workflows) {
    # Releases and Flatpaks are built for tags and beta pushes only; main just runs CI.
    if ($isTag) { $Workflows = @("Release", "Flatpak") } elseif ($Ref -eq "main") { $Workflows = @("CI") } else { $Workflows = @("CI", "Release", "Flatpak") }
}
if (-not (Wait-Workflows $sha $Workflows $Ref $TimeoutMinutes)) { exit 1 }
