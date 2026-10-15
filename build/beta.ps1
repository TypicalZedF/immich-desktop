<#
.SYNOPSIS
  Ships what's committed on beta as a beta release: syncs with origin, runs the tests, pushes,
  then waits for CI and the beta release and reports in a few lines.

.DESCRIPTION
  Every push to beta becomes a pre-release (vX.Y.Z-beta.N); installed apps on the Beta channel update to it.
  Uncommitted changes stop the script unless you pass -Message (then everything is committed with it).

.EXAMPLE
  ./build/beta.ps1                         # push the commits you have
  ./build/beta.ps1 -Message "Fix the map"  # commit everything first
  ./build/beta.ps1 -NoWait                 # push and return; check later with ./build/watch.ps1
#>
param(
    [string]$Message,
    [switch]$SkipTests,
    [switch]$NoWait
)
. "$PSScriptRoot/common.ps1"
Require-Gh

if ((GitRun rev-parse --abbrev-ref HEAD) -ne "beta") { Fail "Switch to the beta branch first (git switch beta)." }

if (@(GitRun status --porcelain).Count -gt 0) {
    if (-not $Message) { Fail "There are uncommitted changes. Commit them, or pass -Message `"...`" to commit everything." }
    Step "Committing"
    GitRun add -A | Out-Null
    $file = [IO.Path]::GetTempFileName()
    [IO.File]::WriteAllText($file, $Message)
    GitRun commit --quiet -F $file | Out-Null
    Remove-Item $file
    Ok (GitRun log -1 --format="%h %s")
}

Step "Syncing with origin/beta"
GitRun fetch --quiet origin | Out-Null
$behind = [int](GitRun rev-list --count HEAD..origin/beta)
if ($behind -gt 0) {
    $ErrorActionPreference = "Continue"
    git -C $script:Root rebase --quiet origin/beta 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) {
        git -C $script:Root rebase --abort 2>&1 | Out-Null
        Fail "Someone else pushed changes that conflict with yours. Rebase onto origin/beta by hand, then run this again."
    }
    $ErrorActionPreference = "Stop"
    Ok "Rebased onto $behind new commit(s)"
}
$ahead = [int](GitRun rev-list --count origin/beta..HEAD)
if ($ahead -eq 0) { Note "Nothing new to push."; exit 0 }

if (-not $SkipTests) {
    Step "Running the tests"
    $result = dotnet test (Join-Path $script:Root "tests/ImmichDesktop.Core.Tests") --nologo 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) {
        ($result -split "`n" | Where-Object { $_ -match 'error|Failed|Assert' } | Select-Object -First 20) | Write-Host
        Fail "Tests failed; nothing was pushed."
    }
    Ok (($result -split "`n" | Where-Object { $_ -match 'Passed!' } | Select-Object -First 1).Trim())
}

Step "Pushing $ahead commit(s) to beta"
GitRun push --quiet origin HEAD:beta | Out-Null
$sha = GitRun rev-parse HEAD
Ok (GitRun log -1 --format="%h %s")

if ($NoWait) { Note "Check on it later: ./build/watch.ps1"; exit 0 }
Start-Sleep -Seconds 10
if (Wait-Workflows $sha @("CI", "Release") "beta") {
    $ErrorActionPreference = "Continue"
    $tag = gh release list --repo $script:Repo --limit 5 --json tagName,isPrerelease --jq '[.[] | select(.isPrerelease)][0].tagName' 2>$null
    Ok "Beta $tag is out; apps on the Beta channel pick it up within a few hours (or Settings > Updates > Check now)."
}
else { exit 1 }
