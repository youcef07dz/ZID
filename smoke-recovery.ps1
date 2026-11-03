# App-level Critical Recovery Test (plan §25.2), automated part:
# start ZID against a loopback Range server in an isolated data folder, kill it
# at ~60%, restart, and verify the download resumes and the file is identical.
# It never touches %AppData%\ZID; HKCU\Software\Classes\zid is
# exported before the run and restored in finally.

param(
    [int]$SizeMB = 32,
    [int]$MaxSpeedKbps = 1024,
    [double]$KillAt = 0.6,
    [switch]$Keep
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = Join-Path $root 'bin\Release\ZID.exe'
if (-not (Test-Path $exe)) {
    Write-Host '[FAIL] bin\Release\ZID.exe is missing; run build.cmd first.'
    exit 1
}

# The bridge port is fixed, so a second instance would break the test.
$busy = $false
$probeClient = New-Object System.Net.Sockets.TcpClient
try { $probeClient.Connect('127.0.0.1', 10007); $busy = $true } catch { } finally { try { $probeClient.Close() } catch { } }
if ($busy) {
    Write-Host '[FAIL] port 10007 is already in use (close the running ZID instance).'
    exit 1
}

$work = Join-Path $env:TEMP ('zid-recovery-' + [guid]::NewGuid().ToString('N'))
$appDir = Join-Path $work 'app'
$dataDir = Join-Path $work 'data'
$log = Join-Path $work 'server.log'
$ready = Join-Path $work 'server.ready'
$payload = Join-Path $work 'payload.bin'
$download = Join-Path $dataDir 'downloads\big.bin'
$stateFile = Join-Path $dataDir 'state.json'
$regFile = Join-Path $work 'zid.reg'
$regKey = 'HKCU\Software\Classes\zid'

$job = $null
$app = $null
$hadKey = $false
$prevDataDir = $env:ZID_DATA_DIR

function Stop-App {
    param($Process)
    if ($null -eq $Process) { return }
    try { $Process.Refresh() } catch { }
    if (-not $Process.HasExited) {
        Stop-Process -Id $Process.Id -Force -ErrorAction SilentlyContinue
    }
    $end = (Get-Date).AddSeconds(10)
    while ((Get-Date) -lt $end) {
        try { $Process.Refresh() } catch { break }
        if ($Process.HasExited) { break }
        Start-Sleep -Milliseconds 200
    }
}

# aria2 is launched with --stop-with-process, so it should exit with the app;
# force only what is still alive after a short grace period.
function Stop-Aria2 {
    param([int]$AppPid)
    $pattern = '--stop-with-process=' + $AppPid
    $end = (Get-Date).AddSeconds(8)
    while ((Get-Date) -lt $end) {
        $left = @(Get-CimInstance Win32_Process -Filter "Name='aria2c.exe'" -ErrorAction SilentlyContinue |
                  Where-Object { $_.CommandLine -and $_.CommandLine.Contains($pattern) })
        if ($left.Count -eq 0) { return }
        Start-Sleep -Milliseconds 500
    }
    foreach ($p in $left) {
        Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue
    }
}

function Wait-Port {
    param([int]$Port, [int]$Seconds)
    $end = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $end) {
        $c = New-Object System.Net.Sockets.TcpClient
        try {
            $c.Connect('127.0.0.1', $Port)
            $c.Close()
            return $true
        } catch {
            try { $c.Close() } catch { }
            Start-Sleep -Milliseconds 300
        }
    }
    return $false
}

try {
    New-Item -ItemType Directory -Force $appDir | Out-Null
    New-Item -ItemType Directory -Force $dataDir | Out-Null
    Copy-Item -Path (Join-Path $root 'bin\Release\*') -Destination $appDir -Recurse -Force

    & cmd /c ('reg query "' + $regKey + '" >nul 2>&1')
    if ($LASTEXITCODE -eq 0) {
        & cmd /c ('reg export "' + $regKey + '" "' + $regFile + '" /y >nul 2>&1')
        $hadKey = ($LASTEXITCODE -eq 0)
    }

    # Deterministic payload: 1 MB random block repeated, so every range is real.
    $block = New-Object byte[] (1MB)
    $rng = New-Object System.Security.Cryptography.RNGCryptoServiceProvider
    $rng.GetBytes($block)
    $rng.Dispose()
    $out = [IO.File]::Create($payload)
    try { for ($i = 0; $i -lt $SizeMB; $i++) { $out.Write($block, 0, $block.Length) } }
    finally { $out.Close() }
    $total = (Get-Item $payload).Length
    $expectedHash = (Get-FileHash $payload -Algorithm SHA256).Hash
    $token = [guid]::NewGuid().ToString('N')

    # One connection and a speed cap make the 60% kill point reachable.
    Set-Content -Path (Join-Path $dataDir 'config.txt') -Encoding ASCII -Value @(
        'language=en',
        'maxConcurrent=1',
        'partsPerDownload=1',
        ('maxSpeedKbps=' + $MaxSpeedKbps),
        'clipboardWatch=0',
        'shutdownWhenDone=0',
        ('neatToken=' + $token)
    )

    $probe = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, 0)
    $probe.Start()
    $port = $probe.LocalEndpoint.Port
    $probe.Stop()

    $job = Start-Job -ArgumentList $payload, $port, $log, $ready -ScriptBlock {
        param($file, $port, $log, $ready)
        $listener = New-Object System.Net.HttpListener
        $listener.Prefixes.Add("http://127.0.0.1:$port/")
        try { $listener.Start() } catch {
            [IO.File]::WriteAllText($ready, 'error: ' + $_.Exception.Message)
            return
        }
        [IO.File]::WriteAllText($ready, 'ok')
        $total = (Get-Item -LiteralPath $file).Length
        while ($listener.IsListening) {
            $ctx = $null
            try { $ctx = $listener.GetContext() } catch { break }
            $start = [long]0
            $end = $total - 1
            $range = $ctx.Request.Headers['Range']
            if ($range -and ($range -match 'bytes=(\d+)-(\d*)')) {
                $start = [long]$Matches[1]
                if ($Matches[2]) { $end = [long]$Matches[2] }
            }
            if ($start -lt 0) { $start = 0 }
            if ($start -ge $total) { $start = 0 }
            if ($end -ge $total) { $end = $total - 1 }
            if ($end -lt $start) { $end = $total - 1 }
            $len = $end - $start + 1
            try {
                [IO.File]::AppendAllText($log, ((Get-Date -Format 'HH:mm:ss.fff') +
                    ' ' + $ctx.Request.HttpMethod + ' start=' + $start + ' end=' + $end +
                    ' range=' + $range + [Environment]::NewLine))
                $res = $ctx.Response
                $res.AddHeader('Accept-Ranges', 'bytes')
                $res.AddHeader('Content-Disposition', 'attachment; filename="big.bin"')
                if ($range) {
                    $res.StatusCode = 206
                    $res.AddHeader('Content-Range', "bytes $start-$end/$total")
                } else {
                    $res.StatusCode = 200
                }
                $res.ContentType = 'application/octet-stream'
                $res.ContentLength64 = $len
                if ($ctx.Request.HttpMethod -ne 'HEAD' -and $len -gt 0) {
                    $fs = [IO.File]::Open($file, 'Open', 'Read', 'ReadWrite')
                    try {
                        $fs.Position = $start
                        $buf = New-Object byte[] (1MB)
                        $left = $len
                        while ($left -gt 0) {
                            $n = [int][Math]::Min([long]$buf.Length, $left)
                            $r = $fs.Read($buf, 0, $n)
                            if ($r -le 0) { break }
                            $res.OutputStream.Write($buf, 0, $r)
                            $left -= $r
                        }
                    } finally { $fs.Dispose() }
                }
            } catch { }
            finally { try { $ctx.Response.Close() } catch { } }
        }
    }

    $end = (Get-Date).AddSeconds(20)
    while ((Get-Date) -lt $end -and -not (Test-Path $ready)) { Start-Sleep -Milliseconds 200 }
    if (-not (Test-Path $ready) -or (Get-Content $ready -Raw).Trim() -ne 'ok') {
        throw ('loopback server did not start: ' + (Get-Content $ready -Raw -ErrorAction SilentlyContinue))
    }
    $url = "http://127.0.0.1:$port/big.bin"

    $env:ZID_DATA_DIR = $dataDir
    $app = Start-Process -FilePath $exe -WorkingDirectory $appDir -PassThru
    if (-not (Wait-Port 10007 25)) { throw 'ZID bridge did not open port 10007' }

    # Inject the download over the same WebSocket the extension uses.
    $ws = New-Object System.Net.WebSockets.ClientWebSocket
    $ws.Options.AddSubProtocol('token.' + $token)
    $cts = New-Object System.Threading.CancellationTokenSource
    $cts.CancelAfter(15000)
    $ws.ConnectAsync([Uri]'ws://127.0.0.1:10007/download', $cts.Token).Wait()
    $message = "1:GET`n2:$url`n6:file`n7:$total`n9:1`nContent-Disposition: attachment; filename=`"big.bin`""
    $bytes = [Text.Encoding]::UTF8.GetBytes($message)
    $ws.SendAsync((New-Object System.ArraySegment[byte] -ArgumentList @(,$bytes)),
        [System.Net.WebSockets.WebSocketMessageType]::Text, $true, $cts.Token).Wait()
    $replyBuf = New-Object byte[] 32
    $reply = $ws.ReceiveAsync((New-Object System.ArraySegment[byte] -ArgumentList @(,$replyBuf)),
        $cts.Token).Result
    $word = [Text.Encoding]::UTF8.GetString($replyBuf, 0, $reply.Count)
    $ws.Dispose()
    if ($word -ne 'ok') { throw ('bridge refused the request: ' + $word) }

    # Kill at the requested share of the file.
    $target = [long]($total * $KillAt)
    $end = (Get-Date).AddSeconds(180)
    $reached = $false
    while ((Get-Date) -lt $end) {
        try { $app.Refresh() } catch { }
        if ($app.HasExited) { throw 'ZID exited before the kill point' }
        if (Test-Path $download) {
            $have = (Get-Item $download).Length
            if ($have -ge $total) { throw 'download finished before the kill point; lower MaxSpeedKbps or raise SizeMB' }
            if ($have -ge $target) { $reached = $true; break }
        }
        Start-Sleep -Milliseconds 150
    }
    if (-not $reached) { throw 'kill point was not reached in time' }
    $partial = (Get-Item $download).Length

    Stop-App $app
    Stop-Aria2 $app.Id
    $app = $null

    $linesBefore = 0
    if (Test-Path $log) { $linesBefore = @(Get-Content $log).Count }
    if (-not (Test-Path $stateFile)) { throw 'state.json was not written before the kill' }
    $saved = Get-Content $stateFile -Raw | ConvertFrom-Json
    $savedItem = @($saved.items) | Where-Object { $_.url -eq $url } | Select-Object -First 1
    if ($null -eq $savedItem) { throw 'the killed run did not persist the download row' }

    # Restart with the same isolated profile; Recovery should resume the row.
    $app = Start-Process -FilePath $exe -WorkingDirectory $appDir -PassThru
    if (-not (Wait-Port 10007 25)) { throw 'ZID bridge did not reopen after restart' }

    $end = (Get-Date).AddSeconds(240)
    $done = $false
    while ((Get-Date) -lt $end) {
        try { $app.Refresh() } catch { }
        if ($app.HasExited) { throw 'ZID exited during the resume' }
        $done = (Test-Path $download) -and ((Get-Item $download).Length -eq $total) -and
                -not (Test-Path ($download + '.aria2'))
        if ($done) { break }
        Start-Sleep -Milliseconds 300
    }
    if (-not $done) { throw 'the resumed download did not finish in time' }

    Start-Sleep -Seconds 1
    $final = Get-Content $stateFile -Raw | ConvertFrom-Json
    $row = @($final.items) | Where-Object { $_.url -eq $url } | Select-Object -First 1
    $hash = (Get-FileHash $download -Algorithm SHA256).Hash
    $resumeLines = @()
    if (Test-Path $log) {
        $resumeLines = @(Get-Content $log | Select-Object -Skip $linesBefore |
                         Where-Object { $_ -match 'start=(\d+)' -and [long]$Matches[1] -gt 0 })
    }

    Write-Host ('partial before kill: ' + $partial + '/' + $total)
    Write-Host ('resume requests after restart: ' + $resumeLines.Count)

    $ok = $true
    if ($null -eq $row -or $row.status -ne 'Done' -or $row.progress -ne 100) {
        Write-Host '[FAIL] row is not Done/100 after restart'; $ok = $false
    }
    if ($hash -ne $expectedHash) { Write-Host '[FAIL] SHA256 differs from the source'; $ok = $false }
    if ($resumeLines.Count -eq 0) { Write-Host '[FAIL] no resumed range request (start > 0) was served'; $ok = $false }
    if (Test-Path ($download + '.aria2')) { Write-Host '[FAIL] .aria2 control file was left behind'; $ok = $false }

    if ($ok) {
        Write-Host '[PASS] kill, restart, resume and SHA256 verified'
        exit 0
    }
    exit 1
}
catch {
    Write-Host ('[FAIL] ' + $_.Exception.Message)
    exit 1
}
finally {
    Stop-App $app
    if ($app) { Stop-Aria2 $app.Id }
    if ($job) { Stop-Job $job -ErrorAction SilentlyContinue; Remove-Job $job -Force -ErrorAction SilentlyContinue }
    if ($hadKey -and (Test-Path $regFile)) {
        & cmd /c ('reg import "' + $regFile + '" >nul 2>&1')
    } elseif (-not $hadKey) {
        & cmd /c ('reg delete "' + $regKey + '" /f >nul 2>&1')
    }
    $env:ZID_DATA_DIR = $prevDataDir
    if ($Keep) {
        Write-Host ('kept: ' + $work)
    } else {
        try { Remove-Item -Recurse -Force $work -ErrorAction Stop } catch { Write-Host ('could not remove ' + $work + ': ' + $_.Exception.Message) }
    }
}
