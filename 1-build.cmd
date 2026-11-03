@echo off
setlocal enabledelayedexpansion
taskkill /f /im ZID.exe >nul 2>&1
taskkill /f /im ZyZoomDownloadManager.exe >nul 2>&1
taskkill /f /im IdmStyleApp.exe >nul 2>&1
ping -n 2 127.0.0.1 >nul
del /f /s /q "bin\Release\"
set "CONFIG=%~1"
if "%CONFIG%"=="" set "CONFIG=Release"

set "ROOT=%~dp0"
set "OUT=%ROOT%bin\%CONFIG%\"
set "EXE=%OUT%ZID.exe"

echo.
echo === Building ZID [%CONFIG%] with csc.exe ===
echo.

if not exist "%ROOT%src\Program.cs"  ( echo [ERROR] Program.cs not found.  & goto :fail )
set "UI=%ROOT%ui.html"
if exist "%ROOT%src\ui.html" set "UI=%ROOT%src\ui.html"
if not exist "%UI%" ( echo [ERROR] ui.html not found. & goto :fail )

set "CSC="
for %%P in (
    "%windir%\Microsoft.NET\Framework\v2.0.50727\csc.exe"
    "%windir%\Microsoft.NET\Framework64\v2.0.50727\csc.exe"
) do (
    if not defined CSC if exist "%%~P" set "CSC=%%~P"
)
if not defined CSC ( echo [ERROR] .NET Framework 2.0 csc.exe not found. & goto :fail )
echo [INFO] Using compiler: %CSC%

set "REFS=/r:System.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Security.dll /r:System.Xml.dll"
set "ICONOPT="
if exist "%ROOT%app.ico" set "ICONOPT=/win32icon:"%ROOT%app.ico""

if not exist "%OUT%" mkdir "%OUT%" 2>nul

set "DEFINE="
set "DEBUGOPT="
if /i "%CONFIG%"=="Debug" (
    set "DEFINE=/define:DEBUG"
    set "DEBUGOPT=/debug+ /optimize-"
) else (
    set "DEBUGOPT=/optimize+"
)

echo [INFO] Compiling sources...
"%CSC%" /nologo /target:winexe /platform:x86 %DEFINE% %DEBUGOPT% ^
    %ICONOPT% /out:"%EXE%" %REFS% ^
    "%ROOT%src\Program.cs" "%ROOT%src\AppPaths.cs" "%ROOT%src\Utilities.cs" "%ROOT%src\DownloadState.cs" "%ROOT%src\DownloadManager.cs" "%ROOT%src\HistoryService.cs" "%ROOT%src\SchedulePolicy.cs" "%ROOT%src\SettingsStore.cs" "%ROOT%src\MediaService.cs" "%ROOT%src\AddService.cs" "%ROOT%src\ProgressPolicy.cs" "%ROOT%src\WorkerState.cs" "%ROOT%src\DownloadWorker.cs" "%ROOT%src\NetworkRecovery.cs" "%ROOT%src\CapturePolicy.cs" "%ROOT%src\MetadataProbe.cs" "%ROOT%src\UpdateChecker.cs" "%ROOT%src\EngineHost.cs" "%ROOT%src\QueueManager.cs" "%ROOT%src\DownloadEngine.cs" "%ROOT%src\RetryPolicy.cs" "%ROOT%src\NetworkMonitor.cs" "%ROOT%src\Logger.cs" "%ROOT%src\Recovery.cs" "%ROOT%src\Reconciler.cs" "%ROOT%src\MainForm.cs" "%ROOT%src\ClipWatcher.cs" ^
    "%ROOT%src\TaskbarProgress.cs" "%ROOT%src\DownloadItem.cs" "%ROOT%src\StateStore.cs" "%ROOT%src\ScriptBridge.cs" ^
    "%ROOT%src\Theme.cs" "%ROOT%src\SlimBar.cs" "%ROOT%src\MsgBox.cs" ^
    "%ROOT%src\BrowserIntegrationForm.cs" "%ROOT%src\BrowserListForm.cs" ^
    "%ROOT%src\NeatDownloadForm.cs" "%ROOT%src\DownloadProgressForm.cs" ^
    "%ROOT%src\CredentialsForm.cs" ^
    "%ROOT%src\parser.cs" "%ROOT%src\AriaRpc.cs" "%ROOT%src\browsers.cs" "%ROOT%src\neatlink.cs" ^
    "%ROOT%src\mp4mux.cs" "%ROOT%src\Playlist.cs" "%ROOT%src\AssemblyInfo.cs"

if errorlevel 1 goto :fail

echo [INFO] Syncing extension copies from zid-crx...
powershell -NoProfile -ExecutionPolicy Bypass -File "%ROOT%sync-extensions.ps1"
if errorlevel 1 goto :fail

echo [INFO] Copying ui.html...
copy /Y "%UI%" "%OUT%ui.html" >nul
if errorlevel 1 goto :fail
if exist "%ROOT%README.html" copy /Y "%ROOT%README.html" "%OUT%README.html" >nul
if exist "%ROOT%app.ico" copy /Y "%ROOT%app.ico" "%OUT%app.ico" >nul
if exist "%ROOT%download.ico" copy /Y "%ROOT%download.ico" "%OUT%download.ico" >nul
if exist "%ROOT%tools" (
    echo [INFO] Copying tools...
    xcopy /E /I /Y /Q "%ROOT%tools" "%OUT%tools" >nul
)
rem aria2 needs the CA bundle; a half-copied tools folder breaks every HTTPS link.
if exist "%ROOT%tools\ca-certificates.crt" if not exist "%OUT%tools\ca-certificates.crt" (
    echo [ERROR] tools\ca-certificates.crt was not copied.
    goto :fail
)
if exist "%ROOT%lang" (
    echo [INFO] Copying languages...
    xcopy /E /I /Y /Q "%ROOT%lang" "%OUT%lang" >nul
)
if exist "%ROOT%zid-crx" (
    echo [INFO] Copying Chromium extension...
    xcopy /E /I /Y /Q "%ROOT%zid-crx" "%OUT%zid-crx" >nul
)
if exist "%ROOT%zid-crx109" (
    echo [INFO] Copying Chromium extension for Chrome 109 / Win7...
    xcopy /E /I /Y /Q "%ROOT%zid-crx109" "%OUT%zid-crx109" >nul
)
if exist "%ROOT%zid-xpi" (
    echo [INFO] Copying Firefox extension...
    xcopy /E /I /Y /Q "%ROOT%zid-xpi" "%OUT%zid-xpi" >nul
    echo [INFO] Packaging Firefox extension as .xpi...
    powershell -NoProfile -ExecutionPolicy Bypass -File "%ROOT%make-xpi.ps1" -Source "%ROOT%zid-xpi" -OutputDir "%ROOT%bin\%CONFIG%"
    if errorlevel 1 goto :fail
)

echo.
echo === BUILD SUCCEEDED ===
echo   Output: %EXE%
echo.
exit /b 0

:fail
echo.
echo === BUILD FAILED ===
exit /b 1