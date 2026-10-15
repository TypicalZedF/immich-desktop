<#
.SYNOPSIS
  The interesting part of the installed app's log: warnings and errors (repeats collapsed), then the last few lines.
.EXAMPLE
  ./build/log.ps1                    # today's warnings/errors + last 10 lines
  ./build/log.ps1 -Days 3            # the last three days
  ./build/log.ps1 -Grep "upload|album" -Tail 30   # any pattern instead of warnings/errors
#>
param(
    [int]$Days = 1,
    [int]$Tail = 10,
    [int]$Max = 20,
    [string]$Grep
)
$dir = Join-Path $env:LOCALAPPDATA "Immich Splash\logs"
if (-not (Test-Path $dir)) { Write-Host "No logs at $dir"; exit 0 }
$files = Get-ChildItem $dir -Filter "immich-*.log" | Sort-Object Name | Select-Object -Last $Days
if (-not $files) { Write-Host "No log files in $dir"; exit 0 }
$lines = $files | ForEach-Object { Get-Content $_.FullName }
Write-Host "Log: $($files.Name -join ', ')  ($($lines.Count) lines)"

function Short([string]$s) { if ($s.Length -gt 220) { $s.Substring(0, 220) + "..." } else { $s } }

# Warnings and errors (or -Grep matches), the same message counted once with its last time.
$pattern = if ($Grep) { $Grep } else { '\[(WRN|ERR|FTL)\]' }
$hits = $lines | Where-Object { $_ -match $pattern }
# Ids and numbers are blanked so "thumbnail for <id>" repeated for many photos is one line.
$groups = @($hits | Group-Object { ($_ -replace '^\S+ \S+ ', '') -replace '[0-9a-f]{8}-[0-9a-f-]{27}', '<id>' -replace '\d+', '#' } |
    Sort-Object { $_.Group[-1] })
if ($groups.Count -gt $Max) { Write-Host "($($groups.Count - $Max) older kinds of message not shown; -Max to see more)"; $groups = $groups | Select-Object -Last $Max }
foreach ($g in $groups) {
    $last = $g.Group[-1]
    $time = ($last -split ' ')[1] -replace '\.\d+$', ''
    $count = if ($g.Count -gt 1) { " (x$($g.Count))" } else { "" }
    Write-Host (Short "$time$count $($last -replace '^\S+ \S+ ', '')")
}
if (-not $groups) { Write-Host "No $(if ($Grep) { "matches for '$Grep'" } else { 'warnings or errors' })." }

if ($Tail -gt 0) {
    Write-Host "--- last $Tail lines"
    # Back-to-back repeats (timers) are folded into one line.
    $prev = $null; $n = 0
    $out = foreach ($l in $lines | Select-Object -Last ($Tail * 5)) {
        $msg = $l -replace '^\S+ \S+ ', ''
        if ($msg -eq $prev) { $n++; continue }
        if ($prev) { if ($n -gt 1) { "$last (x$n)" } else { $last } }
        $prev = $msg; $n = 1; $last = $l -replace '^\S+ ', ''
    }
    $out = @($out) + $(if ($n -gt 1) { "$last (x$n)" } else { $last })
    $out | Select-Object -Last $Tail | ForEach-Object { Write-Host (Short $_) }
}
