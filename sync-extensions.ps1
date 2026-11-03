# zid-crx is the single source for content.js, common.js, injected/youtube.js,
# capture.json and the icons. Keep manifest.json per target; zid-crx109 is MV2
# and needs browserAction where zid-crx and zid-xpi use action.
# -Check reports drift without writing, for CI.
param([switch]$Check)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$base = Join-Path $root 'src'
$source = Join-Path $base 'zid-crx'
$targets = @('zid-crx109', 'zid-xpi')
$shared = @('content.js', 'common.js', 'capture.json', 'injected\youtube.js',
            'icons\icon16.png', 'icons\icon48.png', 'icons\icon128.png')
$drift = 0

function Same-Bytes([string]$a, [string]$b) {
    if (-not (Test-Path $a) -or -not (Test-Path $b)) { return $false }
    return (Get-FileHash $a -Algorithm SHA256).Hash -eq (Get-FileHash $b -Algorithm SHA256).Hash
}

function Put-Bytes([string]$path, [byte[]]$bytes) {
    if ($Check) {
        if (-not (Test-Path $path) -or
            [Convert]::ToBase64String([IO.File]::ReadAllBytes($path)) -ne [Convert]::ToBase64String($bytes)) {
            Write-Host "[DRIFT] $path"
            $script:drift++
        }
        return
    }
    $dir = Split-Path -Parent $path
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    [IO.File]::WriteAllBytes($path, $bytes)
}

$utf8 = New-Object System.Text.UTF8Encoding($false)
$background = [IO.File]::ReadAllBytes((Join-Path $source 'background.js'))

foreach ($target in $targets) {
    foreach ($file in $shared) {
        $to = Join-Path (Join-Path $base $target) $file
        if ($Check) {
            if (-not (Same-Bytes (Join-Path $source $file) $to)) {
                Write-Host "[DRIFT] $to"
                $drift++
            }
        } else {
            $dir = Split-Path -Parent $to
            if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
            Copy-Item (Join-Path $source $file) $to -Force
        }
    }

    $bg = $background
    if ($target -eq 'zid-crx109') {
        $text = $utf8.GetString($background).Replace(
            'chrome.action.onClicked', 'chrome.browserAction.onClicked')
        $bg = $utf8.GetBytes($text)
    }
    Put-Bytes (Join-Path (Join-Path $base $target) 'background.js') $bg
}

if ($drift -gt 0) {
    throw "$drift extension file(s) differ from zid-crx; run sync-extensions.ps1"
}
Write-Host "[OK] Extensions match zid-crx"
