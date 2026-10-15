# Shared helpers for the build scripts (dot-sourced). Output is kept short on purpose:
# one line per step, details only when something fails.

$ErrorActionPreference = "Stop"
$script:Root = Split-Path $PSScriptRoot -Parent
$script:Repo = "TypicalZedF/immich-desktop"

function Step([string]$text) { Write-Host "==> $text" -ForegroundColor Cyan }
function Ok([string]$text) { Write-Host "    $text" -ForegroundColor Green }
function Note([string]$text) { Write-Host "    $text" -ForegroundColor Yellow }
function Fail([string]$text) { Write-Host "    $text" -ForegroundColor Red; exit 1 }

# Native tools write progress to stderr; Windows PowerShell would treat that as an error, hence "Continue" here.
function GitRun { $ErrorActionPreference = "Continue"; $out = & git -C $script:Root @args 2>&1 | ForEach-Object { "$_" }; if ($LASTEXITCODE -ne 0) { throw "git $($args -join ' ') failed: $out" }; $out }

function Require-Gh {
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { Fail "The GitHub CLI (gh) is needed: winget install GitHub.cli, then gh auth login" }
}

# Latest stable version tag (vX.Y.Z), as a [version].
function Get-LatestStable {
    $tags = GitRun tag --list "v*" | Where-Object { $_ -match '^v\d+\.\d+\.\d+$' }
    $tags | ForEach-Object { [version]$_.Substring(1) } | Sort-Object -Descending | Select-Object -First 1
}

# Workflow runs for a commit: name, status, conclusion, id, url (newest first, one per workflow).
function Get-Runs([string]$sha, [string]$ref) {
    $ErrorActionPreference = "Continue"
    $json = gh run list --repo $script:Repo --commit $sha --limit 30 --json databaseId,workflowName,status,conclusion,headBranch,url,event 2>$null
    if (-not $json) { return @() }
    $runs = ($json | ConvertFrom-Json) | Where-Object { $_.event -ne "dynamic" -and $_.workflowName -ne "Dependabot Updates" }
    if ($ref) { $runs = $runs | Where-Object { $_.headBranch -eq $ref } }
    $runs | Group-Object workflowName | ForEach-Object { $_.Group | Select-Object -First 1 }
}

# Waits until the named workflows for a commit have finished. Prints one line each; on failure, the failed
# step and its error lines. Returns $true when all succeeded.
function Wait-Workflows([string]$sha, [string[]]$names, [string]$ref, [int]$timeoutMinutes = 45) {
    $deadline = (Get-Date).AddMinutes($timeoutMinutes)
    $short = $sha.Substring(0, 7)
    Step "Waiting for $($names -join ', ') on $short"
    $reported = @{}
    $allOk = $true
    while ($true) {
        $runs = @(Get-Runs $sha $ref | Where-Object { $names -contains $_.workflowName })
        foreach ($run in $runs) {
            if ($run.status -ne "completed" -or $reported.ContainsKey($run.workflowName)) { continue }
            $reported[$run.workflowName] = $true
            if ($run.conclusion -eq "success") { Ok "$($run.workflowName): passed" }
            elseif ($run.conclusion -eq "skipped") { Note "$($run.workflowName): skipped" }
            else {
                $allOk = $false
                Write-Host "    $($run.workflowName): $($run.conclusion)  $($run.url)" -ForegroundColor Red
                Show-Failure $run.databaseId
            }
        }
        if ($reported.Count -ge $names.Count) { return $allOk }
        if ((Get-Date) -gt $deadline) {
            $missing = $names | Where-Object { -not $reported.ContainsKey($_) }
            Note "Still running after $timeoutMinutes min: $($missing -join ', ') (check later: ./build/watch.ps1)"
            return $false
        }
        Start-Sleep -Seconds 20
    }
}

# The failed steps of a run and their error lines (not the whole log).
function Show-Failure([long]$runId) {
    $ErrorActionPreference = "Continue"
    $jobs = (gh run view $runId --repo $script:Repo --json jobs | ConvertFrom-Json).jobs
    foreach ($job in $jobs | Where-Object { $_.conclusion -eq "failure" }) {
        $steps = $job.steps | Where-Object { $_.conclusion -eq "failure" } | ForEach-Object { $_.name }
        Write-Host "      job '$($job.name)', step: $($steps -join ', ')" -ForegroundColor Red
    }
    gh run view $runId --repo $script:Repo --log-failed 2>$null |
        Select-String -Pattern 'error|Error|failed|Failed|FAIL|Assert' |
        Where-Object { $_.Line -notmatch 'warning' } |
        Select-Object -First 15 |
        ForEach-Object { "      " + ($_.Line -replace '^\S+\t\S+\t\S+\s', '').Trim() } |
        ForEach-Object { if ($_.Length -gt 220) { $_.Substring(0, 220) } else { $_ } } |
        Write-Host
}
