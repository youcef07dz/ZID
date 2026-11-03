# Packs zid-xpi into zid-firefox-<version>.xpi for the installer and the website.
# Version comes from version.json, the same field 4-push.cmd keeps up to date.
# ZipFile needs .NET 4.5+, which every current build machine has.
param(
    [Parameter(Mandatory = $true)][string]$Source,
    [Parameter(Mandatory = $true)][string]$OutputDir
)
$ErrorActionPreference = 'Stop'

if (-not (Test-Path (Join-Path $Source 'manifest.json'))) {
    throw "manifest.json not found in $Source"
}

Add-Type -AssemblyName System.IO.Compression.FileSystem

$version = '1.0'
$versionFile = Join-Path $PSScriptRoot 'version.json'
if (Test-Path $versionFile) {
    $parsed = (Get-Content $versionFile -Raw | ConvertFrom-Json).version
    if ($parsed) { $version = $parsed }
}

$output = Join-Path $OutputDir "zid-firefox-$version.xpi"
$temp = "$output.tmp"
$root = (Resolve-Path $Source).Path.TrimEnd('\') + '\'

if (-not (Test-Path $OutputDir)) { New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null }
if (Test-Path $temp) { Remove-Item $temp -Force }

$zip = [System.IO.Compression.ZipFile]::Open($temp, 'Create')
try {
    # Forward slashes and manifest.json at the root: what Firefox requires in an XPI.
    foreach ($file in Get-ChildItem -Path $root -Recurse -File) {
        $name = $file.FullName.Substring($root.Length).Replace('\', '/')
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $zip, $file.FullName, $name, [System.IO.Compression.CompressionLevel]::Optimal)
    }
}
finally {
    $zip.Dispose()
}

$zip = [System.IO.Compression.ZipFile]::OpenRead($temp)
try {
    $names = @($zip.Entries | ForEach-Object { $_.FullName })
}
finally {
    $zip.Dispose()
}
if ($names -notcontains 'manifest.json') {
    throw "manifest.json is not at the root of $temp"
}

# Same bytes already in place: leave the file alone, so a running browser holding
# the old xpi open cannot fail the build.
if ((Test-Path $output) -and
    (Get-FileHash $temp).Hash -eq (Get-FileHash $output).Hash) {
    Remove-Item $temp -Force
    Write-Host "[OK] $output is up to date"
    return
}

Move-Item $temp $output -Force
Write-Host "[OK] Created $output"
