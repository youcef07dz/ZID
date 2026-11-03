@echo off
rem Writes the SHA256 of the newest bin\setup*.exe to bin\hash.txt.
rem Called by 4-push.cmd after the build; safe to run standalone too.
setlocal EnableExtensions
cd /d "%~dp0"

rem Newest first, so an older setup_*.exe left on disk is never hashed.
set "SETUP="
for /f "delims=" %%f in ('dir /b /a-d /o-d "bin\setup*.exe" 2^>nul') do if not defined SETUP set "SETUP=bin\%%f"
if not defined SETUP (
    echo [ERROR] No bin\setup*.exe found. Run 2-make-setup.cmd first.
    exit /b 1
)
if not exist "%SETUP%" (
    echo [ERROR] Setup file not found: %SETUP%
    exit /b 1
)

rem Truncate, so a repeated run cannot leave an older hash in the file.
del /q "bin\hash.txt" >nul 2>&1
certutil -hashfile "%SETUP%" SHA256 > "bin\hash.txt"
if errorlevel 1 (
    echo [ERROR] certutil could not hash %SETUP%.
    exit /b 1
)
if not exist "bin\hash.txt" (
    echo [ERROR] bin\hash.txt was not written.
    exit /b 1
)

echo [OK] Hashed %SETUP%
exit /b 0
