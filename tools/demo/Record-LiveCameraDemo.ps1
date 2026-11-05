[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$BackendOrigin,
    [Parameter(Mandatory = $true)]
    [string]$PlayerPath,
    [Parameter(Mandatory = $true)]
    [string]$CalibrationClipPath,
    [string]$OutputPath,
    [switch]$WaitForForeground
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$playerFullPath = (Resolve-Path -LiteralPath $PlayerPath).Path
$calibrationFullPath = (Resolve-Path -LiteralPath $CalibrationClipPath).Path
$finalizerPath = Join-Path $PSScriptRoot 'Finalize-LiveOmniTwinDemo.ps1'
$uri = [Uri]$BackendOrigin
$ipAddress = $null
if ($uri.Scheme -ne 'http' -or -not [System.Net.IPAddress]::TryParse($uri.DnsSafeHost, [ref]$ipAddress) -or -not [System.Net.IPAddress]::IsLoopback($ipAddress) -or $uri.UserInfo -or $uri.Query -or $uri.Fragment) {
    throw 'BackendOrigin must be a plain HTTP loopback URL such as http://127.0.0.1:5108.'
}
$origin = $uri.GetLeftPart([UriPartial]::Authority)
$runId = Get-Date -Format 'yyyyMMdd-HHmmss'
$runRoot = Join-Path $repoRoot "local\demo\live-camera-$runId"
$markerPath = Join-Path $runRoot 'demo-status.txt'
$liveRecordingPath = Join-Path $runRoot 'unity-live.mp4'
$compositePath = Join-Path $runRoot 'calibration-plus-live-unity.mp4'
$playerLogPath = Join-Path $runRoot 'player.log'
$captureLogPath = Join-Path $runRoot 'capture.log'
$compositeLogPath = Join-Path $runRoot 'compose.log'
$verificationPath = Join-Path $runRoot 'recording-verification.json'
$playerHandle = $null
$captureHandle = $null
$playerProcess = $null
$captureProcess = $null

function Quote-WindowsArgument([string]$value) {
    if ($value.Length -gt 0 -and $value -notmatch '[\s"]') { return $value }
    $builder = [System.Text.StringBuilder]::new()
    [void]$builder.Append('"')
    $slashes = 0
    foreach ($character in $value.ToCharArray()) {
        if ($character -eq '\') { $slashes++; continue }
        if ($character -eq '"') {
            for ($index = 0; $index -lt (2 * $slashes + 1); $index++) { [void]$builder.Append('\') }
            [void]$builder.Append('"')
            $slashes = 0
            continue
        }
        for ($index = 0; $index -lt $slashes; $index++) { [void]$builder.Append('\') }
        $slashes = 0
        [void]$builder.Append($character)
    }
    for ($index = 0; $index -lt (2 * $slashes); $index++) { [void]$builder.Append('\') }
    [void]$builder.Append('"')
    return $builder.ToString()
}

function Start-NativeProcess([string]$filePath, [string[]]$arguments, [string]$workingDirectory, [string]$logPath, [bool]$redirectInput = $false) {
    $info = [System.Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $filePath
    $info.WorkingDirectory = $workingDirectory
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.RedirectStandardInput = $redirectInput
    $info.Arguments = (($arguments | ForEach-Object { Quote-WindowsArgument $_ }) -join ' ')
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $info
    if (-not $process.Start()) { throw "Could not start process: $filePath" }
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    return [pscustomobject]@{ Process = $process; Stdout = $stdoutTask; Stderr = $stderrTask; LogPath = $logPath }
}

function Save-ProcessLog($handle) {
    if ($null -eq $handle -or -not $handle.Process.HasExited) { return }
    try {
        [void]$handle.Process.WaitForExit(5000)
        $stdout = $handle.Stdout.GetAwaiter().GetResult()
        $stderr = $handle.Stderr.GetAwaiter().GetResult()
        $content = $stdout
        if (-not [string]::IsNullOrWhiteSpace($stderr)) { $content += "`r`n--- STDERR ---`r`n" + $stderr }
        [System.IO.File]::WriteAllText($handle.LogPath, $content, [System.Text.UTF8Encoding]::new($false))
    }
    catch { Write-Warning 'Could not save a child-process log.' }
}

try {
    if (-not (Test-Path -LiteralPath $finalizerPath -PathType Leaf)) { throw 'The live demo finalizer is missing.' }
    if (-not [string]::IsNullOrWhiteSpace($OutputPath) -and (Test-Path -LiteralPath $OutputPath)) { throw "Output already exists: $OutputPath" }
    New-Item -ItemType Directory -Path $runRoot -Force | Out-Null

    $status = Invoke-RestMethod -Uri "$origin/api/status" -TimeoutSec 4
    $latest = Invoke-RestMethod -Uri "$origin/api/world/latest" -TimeoutSec 4
    $world = $latest.world
    $obstacleCount = @($world.obstacles).Count
    if ($status.state -ne 'running' -or -not $status.hasSnapshot -or -not $status.isObservationCurrent -or
        [int]$latest.sourceMode -ne 0 -or -not $world.isObservationCurrent -or
        [double]$world.sourceAgeMilliseconds -gt 8000 -or $obstacleCount -lt 1 -or
        [string]::IsNullOrWhiteSpace($world.calibrationId)) {
        throw 'The loopback backend does not have a fresh calibrated live-camera observation with a tracked object.'
    }
    Write-Host "Live backend ready · $obstacleCount current obstacle track(s) · calibration $($world.calibrationId) · credential values are not read or printed by this recorder."

    $ffmpeg = (Get-Command ffmpeg -ErrorAction Stop).Source
    $ffprobe = (Get-Command ffprobe -ErrorAction Stop).Source
    $encoder = & $ffmpeg -hide_banner -encoders 2>&1 | Select-String '\blibx264\b'
    if (-not $encoder) { throw 'FFmpeg with the libx264 encoder is required.' }

    $playerDirectory = Split-Path -Parent $playerFullPath
    $playerName = [System.IO.Path]::GetFileNameWithoutExtension($playerFullPath)
    $arguments = @('-screen-fullscreen', '1', '-screen-width', '1920', '-screen-height', '1080', '--omnitwin-demo', "--omnitwin-backend-origin=$origin", "--omnitwin-demo-marker=$markerPath")
    $playerHandle = Start-NativeProcess $playerFullPath $arguments $playerDirectory $playerLogPath
    $playerProcess = $playerHandle.Process
    $markerDeadline = [DateTimeOffset]::UtcNow.AddSeconds(35)
    while ([DateTimeOffset]::UtcNow -lt $markerDeadline -and -not (Test-Path -LiteralPath $markerPath)) {
        if ($playerProcess.HasExited) { throw "Standalone '$playerName' exited before demo startup. See $playerLogPath" }
        Start-Sleep -Milliseconds 100
    }
    if (-not (Test-Path -LiteralPath $markerPath) -or (Get-Content -Raw $markerPath) -notmatch '^(DEMO_STARTED|CHAPTER_1)') {
        throw "Standalone '$playerName' did not signal demo startup. See $playerLogPath"
    }

    if ($WaitForForeground) {
        $captureSignalPath = Join-Path $runRoot 'capture.start'
        Write-Host "PLAYER_READY_FOR_FOREGROUND · activate the OmniTwin window, then this run waits for $captureSignalPath"
        $foregroundDeadline = [DateTimeOffset]::UtcNow.AddSeconds(90)
        while ([DateTimeOffset]::UtcNow -lt $foregroundDeadline -and -not (Test-Path -LiteralPath $captureSignalPath)) {
            if ($playerProcess.HasExited) { throw "Standalone '$playerName' exited before foreground capture began." }
            Start-Sleep -Milliseconds 100
        }
        if (-not (Test-Path -LiteralPath $captureSignalPath)) { throw 'The foreground capture handshake did not complete within 90 seconds.' }
        Remove-Item -LiteralPath $captureSignalPath -Force
    }

    $captureArguments = @('-hide_banner', '-loglevel', 'warning', '-f', 'gdigrab', '-framerate', '30', '-video_size', '1920x1080', '-i', 'desktop', '-t', '165', '-an', '-c:v', 'libx264', '-preset', 'veryfast', '-crf', '20', '-pix_fmt', 'yuv420p', '-fps_mode', 'cfr', '-r', '30', '-movflags', '+faststart', $liveRecordingPath)
    $captureHandle = Start-NativeProcess $ffmpeg $captureArguments $runRoot $captureLogPath $true
    $captureProcess = $captureHandle.Process
    Write-Host 'Recording the live-camera standalone at native 1920x1080 / 30 fps. No window switching or microphone capture.'

    $demoDeadline = [DateTimeOffset]::UtcNow.AddSeconds(163)
    $nextProgress = [DateTimeOffset]::UtcNow.AddSeconds(30)
    while (-not $playerProcess.HasExited -and [DateTimeOffset]::UtcNow -lt $demoDeadline) {
        Start-Sleep -Milliseconds 250
        if ([DateTimeOffset]::UtcNow -ge $nextProgress) {
            Write-Host 'Live Unity presentation is still recording.'
            $nextProgress = [DateTimeOffset]::UtcNow.AddSeconds(30)
        }
    }
    if (-not $playerProcess.HasExited) {
        $playerProcess.Kill($true)
        throw 'Standalone exceeded the 163-second six-act timeline limit.'
    }
    if (-not $captureProcess.HasExited) {
        $captureProcess.StandardInput.WriteLine('q')
        $captureProcess.StandardInput.Flush()
        if (-not $captureProcess.WaitForExit(15000)) { $captureProcess.Kill($true); throw 'FFmpeg did not close the MP4 cleanly.' }
    }
    Save-ProcessLog $captureHandle
    Save-ProcessLog $playerHandle
    if ($captureProcess.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $liveRecordingPath -PathType Leaf)) {
        throw "Live capture failed. See $captureLogPath"
    }

    $liveProbeText = & $ffprobe -v error -show_entries 'format=duration:stream=codec_type,codec_name,width,height,avg_frame_rate' -of json $liveRecordingPath 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { throw 'FFprobe could not validate the live Unity capture.' }
    $liveProbe = $liveProbeText | ConvertFrom-Json
    $liveVideo = $liveProbe.streams | Where-Object { $_.codec_type -eq 'video' } | Select-Object -First 1
    $liveDuration = [double]$liveProbe.format.duration
    if ($null -eq $liveVideo -or $liveVideo.width -ne 1920 -or $liveVideo.height -ne 1080 -or $liveVideo.avg_frame_rate -ne '30/1' -or $liveDuration -lt 150 -or $liveDuration -gt 168) {
        throw "Live Unity capture validation failed: duration=$liveDuration, video=$($liveVideo.width)x$($liveVideo.height)@$($liveVideo.avg_frame_rate)."
    }

    $composeFilter = '[0:v]trim=start=0:duration=10,setpts=PTS-STARTPTS,fps=30,scale=1920:1080:flags=lanczos,setsar=1[v0];[1:v]setpts=PTS-STARTPTS,fps=30,scale=1920:1080:flags=lanczos,setsar=1[v1];[v0][v1]concat=n=2:v=1:a=0[v]'
    $composeArguments = @('-hide_banner', '-loglevel', 'warning', '-i', $calibrationFullPath, '-i', $liveRecordingPath, '-filter_complex', $composeFilter, '-map', '[v]', '-an', '-c:v', 'libx264', '-preset', 'medium', '-crf', '18', '-pix_fmt', 'yuv420p', '-fps_mode', 'cfr', '-r', '30', '-movflags', '+faststart', $compositePath)
    $composeHandle = Start-NativeProcess $ffmpeg $composeArguments $runRoot $compositeLogPath
    if (-not $composeHandle.Process.WaitForExit(120000)) { $composeHandle.Process.Kill($true); throw "Calibration and live-video composition exceeded two minutes. See $compositeLogPath" }
    Save-ProcessLog $composeHandle
    if ($composeHandle.Process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $compositePath -PathType Leaf)) { throw "Calibration/live-video composition failed. See $compositeLogPath" }

    $compositeProbeText = & $ffprobe -v error -show_entries 'format=duration:stream=codec_type,codec_name,width,height,avg_frame_rate' -of json $compositePath 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { throw 'FFprobe could not validate the composed recording.' }
    $compositeProbe = $compositeProbeText | ConvertFrom-Json
    $compositeVideo = $compositeProbe.streams | Where-Object { $_.codec_type -eq 'video' } | Select-Object -First 1
    $compositeDuration = [double]$compositeProbe.format.duration
    if ($null -eq $compositeVideo -or $compositeVideo.width -ne 1920 -or $compositeVideo.height -ne 1080 -or $compositeVideo.avg_frame_rate -ne '30/1' -or $compositeDuration -lt 150 -or $compositeDuration -gt 168) {
        throw "Composed recording validation failed: duration=$compositeDuration, video=$($compositeVideo.width)x$($compositeVideo.height)@$($compositeVideo.avg_frame_rate)."
    }

    if ([string]::IsNullOrWhiteSpace($OutputPath)) { $OutputPath = Join-Path $repoRoot "OmniTwin-Hackathon-Demo-Live-$runId.mp4" }
    $OutputPath = [System.IO.Path]::GetFullPath($OutputPath)
    if (Test-Path -LiteralPath $OutputPath) { throw "Output already exists: $OutputPath" }
    & $finalizerPath -RawRecordingPath $compositePath -OutputPath $OutputPath
    if (-not (Test-Path -LiteralPath $OutputPath -PathType Leaf)) { throw 'The live-video finalizer did not create a validated MP4.' }

    $verification = [ordered]@{
        CreatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        BackendOrigin = $origin
        SourceMode = 'Live camera backend (sourceMode 0)'
        CalibrationId = $world.calibrationId
        CurrentObstacleTracksAtStart = $obstacleCount
        CalibrationClip = $calibrationFullPath
        LiveUnityCapture = $liveRecordingPath
        ComposedRawCapture = $compositePath
        FinalVideo = $OutputPath
        LiveCaptureDurationSeconds = [math]::Round($liveDuration, 3)
        ComposedCaptureDurationSeconds = [math]::Round($compositeDuration, 3)
        Resolution = '1920x1080'
        FrameRate = '30/1 CFR'
        CaptureAudio = 'Disabled; final output uses generated English TTS only'
        CredentialValuesReadPrintedOrWritten = $false
    }
    [System.IO.File]::WriteAllText($verificationPath, ($verification | ConvertTo-Json -Depth 5), [System.Text.UTF8Encoding]::new($false))
    Write-Host "LIVE DEMO COMPLETE: $OutputPath"
    Write-Host "Calibration $($world.calibrationId) · $($verification.LiveCaptureDurationSeconds)s live Unity · 1920x1080/30 · generated English narration · verification $verificationPath"
}
finally {
    if ($captureProcess -and -not $captureProcess.HasExited) { try { $captureProcess.Kill($true) } catch { } }
    if ($playerProcess -and -not $playerProcess.HasExited) { try { $playerProcess.Kill($true) } catch { } }
    foreach ($handle in @($captureHandle, $playerHandle)) { Save-ProcessLog $handle }
}
