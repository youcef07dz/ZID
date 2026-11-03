@echo off
setlocal EnableExtensions
title Build ZyZoom Internet Downloader Setup

cd /d "%~dp0"

echo.
echo === Building ZyZoom Internet Downloader setup.exe ===
echo.

if not exist "README.html" (
    echo [ERROR] README.html not found.
    exit /b 1
)

call 1-build.cmd
if errorlevel 1 (
    echo [ERROR] Build failed.
    exit /b 1
)

rem Inno Setup 5.6.1 builds the installer.
set "ISCC="
if exist "src\inno\ISCC.exe" set "ISCC=src\inno\ISCC.exe"

if not defined ISCC (
    echo [ERROR] Inno Setup 5.6.1 compiler ^(ISCC.exe^) not found.
    echo Install Inno Setup 5.6.1
    echo or run this script on a machine that has it.
    exit /b 1
)

echo [INFO] Using compiler: %ISCC%
if not exist "bin" mkdir "bin" >nul 2>&1

"%ISCC%" "setup.iss"
if errorlevel 1 (
    echo [ERROR] Setup compilation failed.
    exit /b 1
)

if not exist "bin\setup*.exe" (
    echo [ERROR] No bin\setup*.exe was created.
    exit /b 1
)

echo.
echo [OK] Created the setup in %~dp0bin\
echo.
exit /b 0
