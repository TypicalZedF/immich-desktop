<#
.SYNOPSIS
  Ships this PC's current commit as a beta (only when the user says "push to beta"; until then changes are
  tried with ./build/dev.ps1): syncs with origin, runs the tests, pushes to the beta branch, then presses
  Ship → Beta (./build/ship.ps1) and follows it.

.DESCRIPTION
  Pushing alone only runs CI; the Ship workflow publishes the pre-release (vX.Y.Z-beta.N) that installed apps on the
  Beta channel update to. A stable release removes the beta branch, so the first push after one starts it again.
  Works from any local branch. Uncommitted changes stop the script unless you pass -Message (then everything is committed with it).

.EXAMPLE
  ./build/beta.ps1                         # push the commits you have
  ./build/beta.ps1 -Message "Fix the map"  # commit everything first
  ./build/beta.ps1 -NoWait                 # push, press Ship and return (the run's link is printed)
#>
param(
    [string]$Message,
    [switch]$SkipTests,
    [switch]$NoWait
)
. "$PSScriptRoot/common.ps1"
Require-Gh

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

Step "Syncing with GitHub"
GitRun fetch --quiet --prune origin | Out-Null
$hasBeta = Test-RemoteBranch beta
$behind = if ($hasBeta) { [int](GitRun rev-list --count HEAD..origin/beta) } else { 0 }
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
Merge-Main
if ($hasBeta) {
    $ahead = [int](GitRun rev-list --count origin/beta..HEAD)
    if ($ahead -eq 0) { Note "Nothing new to push."; exit 0 }
}
else {
    $ahead = [int](GitRun rev-list --count origin/main..HEAD)
    if ($ahead -eq 0) { Note "Nothing new since the last stable release."; exit 0 }
}

if (-not $SkipTests) {
    Step "Running the tests"
    $result = dotnet test (Join-Path $script:Root "tests/ImmichSplash.Core.Tests") --nologo 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) {
        ($result -split "`n" | Where-Object { $_ -match 'error|Failed|Assert' } | Select-Object -First 20) | Write-Host
        Fail "Tests failed; nothing was pushed."
    }
    Ok (($result -split "`n" | Where-Object { $_ -match 'Passed!' } | Select-Object -First 1).Trim())
}

if ($hasBeta) { Step "Pushing $ahead commit(s) to beta" } else { Step "Starting the beta branch ($ahead commit(s) after main)" }
GitRun push --quiet origin HEAD:beta | Out-Null
$sha = GitRun rev-parse HEAD
Ok (GitRun log -1 --format="%h %s")

& "$PSScriptRoot/ship.ps1" -Channel Beta -NoWait:$NoWait
exit $LASTEXITCODE
