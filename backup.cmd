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
for %%F in (build.cmd tests.cmd make-setup.cmd setup.iss smoke-recovery.ps1 task.md ui.html README.html) do (
    if exist "%ROOT%%%F" copy /Y "%ROOT%%%F" "%DEST%\%%F" >nul
)
for %%D in (zid-crx zid-crx109 zid-xpi) do (
    if exist "%ROOT%%%D" xcopy "%ROOT%%%D" "%DEST%\%%D" /E /I /Y /Q >nul
)

echo [OK] Backup created: %DEST%
exit /b 0
