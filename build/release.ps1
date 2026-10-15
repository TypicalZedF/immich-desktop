<#
.SYNOPSIS
  Makes a stable release of this PC's current commit: tests it, moves main to it, waits for CI, tags vX.Y.Z (which
  publishes it), then waits for the Windows/Linux release and the Flatpak repository and reports in a few lines.

.DESCRIPTION
  No beta needed first. Once it's out, every beta release is removed, and so is the beta branch when everything on it
  is in this release (release.yml), leaving only main. Beta-channel installs move to this release.
  Updates merged on GitHub's main (Dependabot) are merged in here first.
  The version is the next patch after the latest stable release unless you say otherwise.
  Release notes are written by GitHub from the commits (release.yml).

.EXAMPLE
  ./build/release.ps1               # next patch, e.g. 1.6.0 -> 1.6.1 (asks before publishing)
  ./build/release.ps1 -Minor        # 1.6.x -> 1.7.0
  ./build/release.ps1 -Version 2.0.0 -Yes
  ./build/release.ps1 -DryRun       # show what would be released, change nothing
#>
param(
    [string]$Version,
    [switch]$Minor,
    [switch]$Major,
    [switch]$Yes,
    [switch]$NoWait,
    [switch]$DryRun
)
. "$PSScriptRoot/common.ps1"
Require-Gh

Step "Checking this commit"
GitRun fetch --quiet --tags origin | Out-Null
if (GitRun status --porcelain --untracked-files=no) { Fail "There are uncommitted changes. Commit them first." }
$sha = GitRun rev-parse HEAD
if (-not (Test-Ancestor origin/main $sha)) {
    if ($DryRun) { Note "main on GitHub has commits that aren't here; they'd be merged in first." }
    else { Merge-Main; $sha = GitRun rev-parse HEAD }
}
Ok "Releasing $(GitRun log -1 --format='%h %s' $sha)"
if ((Test-RemoteBranch beta) -and -not (Test-Ancestor origin/beta $sha)) {
    Note "beta has commits that aren't in this release; the beta branch will be kept for them."
}

# ---- Version ----
$last = Get-LatestStable
if (-not $Version) {
    if ($Major) { $Version = "$($last.Major + 1).0.0" }
    elseif ($Minor) { $Version = "$($last.Major).$($last.Minor + 1).0" }
    else { $Version = "$($last.Major).$($last.Minor).$($last.Build + 1)" }
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { Fail "The version must look like 1.6.0." }
$tag = "v$Version"
if (GitRun tag --list $tag) { Fail "$tag already exists." }

$changes = @(GitRun log --format="  - %s" "v$last..$sha" --no-merges)
Write-Host ""
Write-Host "Release $tag (after v$last): $($changes.Count) commits" -ForegroundColor White
$changes | Select-Object -First 15 | Write-Host
if ($changes.Count -gt 15) { Write-Host "  … and $($changes.Count - 15) more" }
Write-Host ""
if ($changes.Count -eq 0) { Fail "Nothing new since v$last." }
if ($DryRun) { Note "Dry run: nothing was changed."; exit 0 }
if (-not $Yes) {
    $answer = Read-Host "Publish $tag to everyone? (y/N)"
    if ($answer -notmatch '^(y|yes)$') { Note "Cancelled; nothing was changed."; exit 0 }
}

# ---- Publish ----
Step "Testing here"
& "$PSScriptRoot/check.ps1"
if ($LASTEXITCODE -ne 0) { Fail "Tests failed; nothing was pushed." }

Step "Moving main to this commit (CI runs on it)"
GitRun push --quiet origin "${sha}:refs/heads/main" | Out-Null
Start-Sleep -Seconds 10
if (-not (Wait-Workflows $sha @("CI") "main" 30)) { Fail "CI didn't pass on main, so it isn't tagged and nothing was released. Fix it and run this again." }

Step "Tagging $tag (this publishes it)"
GitRun tag $tag $sha | Out-Null
GitRun push --quiet origin $tag | Out-Null
Ok "Pushed main and $tag"

if ($NoWait) { Note "Check on it later: ./build/watch.ps1 -Ref $tag"; exit 0 }
Start-Sleep -Seconds 10
$ok = Wait-Workflows $sha @("Release", "Flatpak") $tag 60
if (-not $ok) { exit 1 }

$flatpakref = "https://typicalzedf.github.io/immich-splash/io.github.TypicalZedF.ImmichSplash.flatpakref"
try { $code = (Invoke-WebRequest $flatpakref -UseBasicParsing -Method Head).StatusCode } catch { $code = 0 }
if ($code -eq 200) { Ok "Flatpak repository is live: $flatpakref" } else { Note "The Flatpak page isn't answering yet (GitHub Pages can take a few minutes): $flatpakref" }
Ok "Released: https://github.com/$($script:Repo)/releases/tag/$tag"
