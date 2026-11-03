# Reports language files whose key set differs from en.txt.
# Missing keys fall back to English at runtime, so this is a report, not a build
# failure unless -Strict is passed.
param([switch]$Strict)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$dir  = Join-Path $root "lang"
$base = Join-Path $dir "en.txt"

function Get-Keys([string]$path) {
    $keys = New-Object System.Collections.Generic.HashSet[string]([StringComparer]::Ordinal)
    foreach ($line in [IO.File]::ReadAllLines($path)) {
        if ($line -match '^\s*([A-Za-z0-9_.]+)\s*=') { [void]$keys.Add($Matches[1]) }
    }
    return $keys
}

$en = Get-Keys $base
$problems = 0

foreach ($file in Get-ChildItem $dir -Filter *.txt | Sort-Object Name) {
    if ($file.Name -eq "en.txt") { continue }

    $keys = Get-Keys $file.FullName
    $missing = @($en | Where-Object { -not $keys.Contains($_) })
    $extra   = @($keys | Where-Object { -not $en.Contains($_) })

    if ($missing.Count -eq 0 -and $extra.Count -eq 0) { continue }

    $problems++
    "{0}: {1} missing, {2} extra" -f $file.Name, $missing.Count, $extra.Count
    if ($missing.Count -gt 0) { "  missing: " + ($missing -join ", ") }
    if ($extra.Count -gt 0)   { "  extra:   " + ($extra -join ", ") }
}

if ($problems -eq 0) { "lang: all files match en.txt" }
elseif ($Strict) { exit 1 }
exit 0
