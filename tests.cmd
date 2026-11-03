@echo off
setlocal
set "ROOT=%~dp0"
set "CSC="
for %%P in (
    "%windir%\Microsoft.NET\Framework\v2.0.50727\csc.exe"
    "%windir%\Microsoft.NET\Framework64\v2.0.50727\csc.exe"
) do (
    if not defined CSC if exist "%%~P" set "CSC=%%~P"
)
if not defined CSC ( echo [ERROR] .NET Framework 2.0 csc.exe not found. & exit /b 1 )

if not exist "%ROOT%bin\Release" mkdir "%ROOT%bin\Release" 2>nul
set "REFS=/r:System.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Security.dll /r:System.Xml.dll"
"%CSC%" /nologo /target:exe /platform:x86 %REFS% /out:"%ROOT%bin\Release\tests.exe" ^
    "%ROOT%src\parser.cs" "%ROOT%src\AriaRpc.cs" "%ROOT%src\browsers.cs" "%ROOT%src\neatlink.cs" "%ROOT%src\mp4mux.cs" "%ROOT%src\Playlist.cs" ^
    "%ROOT%src\DownloadItem.cs" "%ROOT%src\DownloadState.cs" "%ROOT%src\DownloadManager.cs" "%ROOT%src\HistoryService.cs" "%ROOT%src\SchedulePolicy.cs" "%ROOT%src\SettingsStore.cs" "%ROOT%src\MediaService.cs" "%ROOT%src\AddService.cs" "%ROOT%src\ProgressPolicy.cs" "%ROOT%src\WorkerState.cs" "%ROOT%src\DownloadWorker.cs" "%ROOT%src\NetworkRecovery.cs" "%ROOT%src\CapturePolicy.cs" "%ROOT%src\MetadataProbe.cs" "%ROOT%src\UpdateChecker.cs" "%ROOT%src\EngineHost.cs" "%ROOT%src\QueueManager.cs" "%ROOT%src\DownloadEngine.cs" "%ROOT%src\RetryPolicy.cs" "%ROOT%src\NetworkMonitor.cs" "%ROOT%src\Logger.cs" "%ROOT%src\Utilities.cs" "%ROOT%src\AppPaths.cs" "%ROOT%src\Recovery.cs" "%ROOT%src\Reconciler.cs" "%ROOT%src\StateStore.cs" "%ROOT%src\bench.cs" "%ROOT%src\Theme.cs" "%ROOT%src\tests.cs"
if errorlevel 1 ( echo === TESTS BUILD FAILED === & exit /b 1 )
"%ROOT%bin\Release\tests.exe"
exit /b %errorlevel%
