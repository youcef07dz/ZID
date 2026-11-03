@echo off
rem Run this before every code change. Snapshots source, extensions and build files under backup\<timestamp>.
setlocal
set "ROOT=%~dp0"
set "STAMP="
for /f "usebackq delims=" %%I in (`powershell -NoProfile -Command "Get-Date -Format yyyyMMdd-HHmmss"`) do set "STAMP=%%I"
if "%STAMP%"=="" set "STAMP=manual"
set "DEST=%ROOT%backup\%STAMP%"
if exist "%DEST%" set "DEST=%DEST%-%RANDOM%"

mkdir "%DEST%" >nul 2>&1
if errorlevel 1 (
    echo [ERROR] Cannot create "%DEST%".
    exit /b 1
)

xcopy "%ROOT%src" "%DEST%\src" /E /I /Y /Q >nul
if errorlevel 1 (
    echo [ERROR] Could not copy src.
    exit /b 1
)
for %%F in (1-build.cmd 2-make-setup.cmd 3-makehash.cmd 4-push.cmd tests.cmd setup.iss smoke-recovery.ps1 check-lang.ps1 sync-extensions.ps1 make-xpi.ps1 version.json task.md README.html) do (
    if exist "%ROOT%%%F" copy /Y "%ROOT%%%F" "%DEST%\%%F" >nul
)

echo [OK] Backup created: %DEST%
exit /b 0
