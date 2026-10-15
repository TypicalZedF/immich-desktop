<#
.SYNOPSIS
  Makes a stable release from what's on beta: checks CI passed, moves main up to beta, tags vX.Y.Z,
  then waits for the Windows/Linux release and the Flatpak repository and reports in a few lines.

.DESCRIPTION
  The version is the next patch after the latest stable release unless you say otherwise.
  Release notes are written by GitHub from the commits (release.yml).

.EXAMPLE
  ./build/release.ps1               # next patch, e.g. 1.5.2 -> 1.5.3 (asks before publishing)
  ./build/release.ps1 -Minor        # 1.5.x -> 1.6.0
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

Step "Checking beta"
GitRun fetch --quiet --tags origin | Out-Null
$sha = GitRun rev-parse origin/beta
$ErrorActionPreference = "Continue"
git -C $script:Root merge-base --is-ancestor origin/main $sha 2>$null
$canFastForward = $LASTEXITCODE -eq 0
$ErrorActionPreference = "Stop"
if (-not $canFastForward) { Fail "main has commits that aren't on beta. Merge main into beta first." }

$ci = Get-Runs $sha "beta" | Where-Object { $_.workflowName -eq "CI" }
if (-not $ci) { Fail "No CI run found for $($sha.Substring(0,7)) yet. Wait a minute and try again." }
if ($ci.status -ne "completed") {
    if (-not (Wait-Workflows $sha @("CI") "beta")) { Fail "CI didn't pass; not releasing." }
}
elseif ($ci.conclusion -ne "success") { Fail "CI failed on $($sha.Substring(0,7)): $($ci.url)" }
Ok "CI passed on $(GitRun log -1 --format='%h %s' $sha)"

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
if ($DryRun) { Note "Dry run: nothing was changed."; exit 0 }
if (-not $Yes) {
    $answer = Read-Host "Publish $tag to everyone on the Stable channel? (y/N)"
    if ($answer -notmatch '^(y|yes)$') { Note "Cancelled; nothing was changed."; exit 0 }
}

# ---- Publish ----
Step "Moving main to beta and tagging $tag"
GitRun push --quiet origin "${sha}:refs/heads/main" | Out-Null
GitRun tag $tag $sha | Out-Null
GitRun push --quiet origin $tag | Out-Null
Ok "Pushed main and $tag"

if ($NoWait) { Note "Check on it later: ./build/watch.ps1 -Ref $tag"; exit 0 }
Start-Sleep -Seconds 10
$ok = Wait-Workflows $sha @("Release", "Flatpak") $tag 60
if (-not $ok) { exit 1 }

$flatpakref = "https://typicalzedf.github.io/immich-splash/io.github.TypicalZedF.ImmichDesktop.flatpakref"
try { $code = (Invoke-WebRequest $flatpakref -UseBasicParsing -Method Head).StatusCode } catch { $code = 0 }
if ($code -eq 200) { Ok "Flatpak repository is live: $flatpakref" } else { Note "The Flatpak page isn't answering yet (GitHub Pages can take a few minutes): $flatpakref" }
Ok "Released: https://github.com/$($script:Repo)/releases/tag/$tag"
