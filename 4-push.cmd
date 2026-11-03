@echo off
rem Builds the setup, hashes it, refreshes version.json and publishes to GitHub main.
setlocal EnableExtensions
title Publish ZyZoom Internet Downloader
cd /d "%~dp0"

set "REPO=https://github.com/youcef07dz/ZID.git"

echo.
echo === [1/4] Building setup ===
call 2-make-setup.cmd
if errorlevel 1 (
    echo [ERROR] Setup build failed.
    exit /b 1
)

echo.
echo === [2/4] Hashing setup ===
rem call waits for the child batch; 3-makehash writes a fresh bin\hash.txt
rem for the newest setup_*.exe and fails if it cannot.
del /q "bin\hash.txt" >nul 2>&1
call 3-makehash.cmd
if errorlevel 1 (
    echo [ERROR] Hashing failed.
    exit /b 1
)

echo.
echo === [3/4] Updating version.json ===
rem Fills setup name, version, sha256 and build time from bin\hash.txt, and stages
rem the installer in dist\ where the app's update check downloads it from.
rem \x22 is the regex escape for a double quote; writing real quotes here would
rem break the cmd-level quoting of the whole -Command argument.
powershell -NoProfile -ExecutionPolicy Bypass -Command "$n=''; $s=''; foreach($l in [IO.File]::ReadAllLines('bin\hash.txt')){ if($l -match 'hash of (.+?):\s*$'){ $n=[IO.Path]::GetFileName($Matches[1]) } elseif($n){ $h=$l -replace '\s',''; if($h -match '^[0-9a-fA-F]{64}$'){ $s=$h } } }; if(-not $n -or -not $s){ Write-Error 'bin\hash.txt has no setup hash'; exit 1 }; if(-not (Test-Path ('bin\'+$n))){ Write-Error ('setup not found: bin\'+$n); exit 1 }; $v=''; if($n -match '^setup_(.+)\.exe$'){ $v=$Matches[1] }; $t=[IO.File]::ReadAllText('version.json'); $t=[regex]::Replace($t,'(\x22setup\x22\s*:\s*\x22)[^\x22]*','${1}'+$n); $t=[regex]::Replace($t,'(\x22sha256\x22\s*:\s*\x22)[^\x22]*','${1}'+$s); $t=[regex]::Replace($t,'(\x22build_date\x22\s*:\s*\x22)[^\x22]*','${1}'+(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')); if($v){ $t=[regex]::Replace($t,'(\x22version\x22\s*:\s*\x22)[^\x22]*','${1}'+$v) }; if($t.IndexOf($s) -lt 0){ Write-Error 'version.json was not updated with the new hash'; exit 1 }; [IO.File]::WriteAllText('version.json',$t,(New-Object Text.UTF8Encoding($false))); New-Item -ItemType Directory -Force -Path 'dist' | Out-Null; Copy-Item ('bin\'+$n) 'dist\' -Force; $x=@(Get-ChildItem 'bin\Release\zid-firefox-*.xpi'); if($x.Count -eq 0){ Write-Error 'bin\Release\zid-firefox-*.xpi not found'; exit 1 }; $x | Copy-Item -Destination 'dist' -Force; Write-Host ('[OK] version.json -> '+$n+' '+$s)"
if errorlevel 1 (
    echo [ERROR] version.json update failed.
    exit /b 1
)

echo.
echo === [4/4] Pushing to GitHub main ===
set "FRESH="
if not exist ".git" set "FRESH=1"

git init -b main >nul 2>&1
if errorlevel 1 (
    echo [ERROR] git init failed.
    exit /b 1
)

git remote get-url origin >nul 2>&1
if errorlevel 1 (
    git remote add origin %REPO%
) else (
    git remote set-url origin %REPO%
)

rem Fresh folder + existing remote main: base our commit on it so the push
rem fast-forwards instead of being rejected. Files on disk are left alone.
if defined FRESH (
    git fetch origin main
    git rev-parse --verify --quiet FETCH_HEAD >nul 2>&1
    if not errorlevel 1 (
        git update-ref refs/heads/main FETCH_HEAD
        git reset >nul
    )
)

rem git refuses to commit without an identity; set a repo-local default only
rem when none exists, so a configured one is never overwritten.
git config user.email >nul 2>&1
if errorlevel 1 (
    git config user.name "youcef07dz"
    git config user.email "youcef07dz@users.noreply.github.com"
)

git add -A
if errorlevel 1 (
    echo [ERROR] git add failed.
    exit /b 1
)

git diff --cached --quiet
if errorlevel 1 (
    git commit -m "Release %DATE% %TIME%"
    if errorlevel 1 (
        echo [ERROR] git commit failed.
        exit /b 1
    )
) else (
    echo [INFO] Nothing new to commit.
)

git push -u origin main
if errorlevel 1 (
    echo [ERROR] git push failed.
    exit /b 1
)

echo.
echo [OK] Published to https://github.com/youcef07dz/ZID
echo.
exit /b 0
