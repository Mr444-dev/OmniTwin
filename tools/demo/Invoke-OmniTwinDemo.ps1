[CmdletBinding()]
param(
    [switch]$SkipDotNetTests,
    [switch]$SkipUnityBuild,
    [string]$ExistingPlayerPath
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$unityProject = Join-Path $repoRoot 'unity\OmniTwin.Unity'
$demoRoot = Join-Path $repoRoot 'local\demo'
$runId = Get-Date -Format 'yyyyMMdd-HHmmss'
$runRoot = Join-Path $demoRoot $runId
$buildRoot = Join-Path $runRoot 'StandaloneWindows64'
$playerPath = Join-Path $buildRoot 'OmniTwin.exe'
$buildMode = 'UnityStandaloneBuild'
if (-not [string]::IsNullOrWhiteSpace($ExistingPlayerPath)) {
    if (-not $SkipUnityBuild) { throw 'Pass -SkipUnityBuild when reusing an existing Standalone Player.' }
    $playerPath = (Resolve-Path -LiteralPath $ExistingPlayerPath).Path
    $buildRoot = Split-Path -Parent $playerPath
    $buildMode = 'ReusedExistingPlayer'
}
$rawVideoPath = Join-Path $runRoot 'desktop-capture.mp4'
$finalVideoPath = Join-Path $demoRoot 'OmniTwin-Hackathon-Demo.mp4'
if (Test-Path -LiteralPath $finalVideoPath) {
    $finalVideoPath = Join-Path $demoRoot "OmniTwin-Hackathon-Demo-$runId.mp4"
}
$statusMarker = Join-Path $runRoot 'demo-status.txt'
$backendLogPath = Join-Path $runRoot 'backend.log'
$playerLogPath = Join-Path $runRoot 'player.log'
$ffmpegLogPath = Join-Path $runRoot 'capture.log'
$verificationPath = Join-Path $runRoot 'verification.json'
$backendProcess = $null
$playerProcess = $null
$captureProcess = $null
$backendHandle = $null
$playerHandle = $null
$captureHandle = $null
$muxHandle = $null
$previousPort = $env:OmniTwin__Port
$createdVideo = $false

function Resolve-Executable([string]$name) {
    $command = Get-Command $name -ErrorAction Stop
    return $command.Source
}

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

function Get-WaveDurationSeconds([string]$path) {
    $stream = [System.IO.File]::OpenRead($path)
    try {
        $reader = [System.IO.BinaryReader]::new($stream)
        try {
            if ([System.Text.Encoding]::ASCII.GetString($reader.ReadBytes(4)) -ne 'RIFF') { throw "Invalid WAV header: $path" }
            [void]$reader.ReadUInt32()
            if ([System.Text.Encoding]::ASCII.GetString($reader.ReadBytes(4)) -ne 'WAVE') { throw "Invalid WAV format: $path" }
            $bytesPerSecond = 0
            $dataBytes = 0
            while ($stream.Position + 8 -le $stream.Length) {
                $chunkName = [System.Text.Encoding]::ASCII.GetString($reader.ReadBytes(4))
                $chunkLength = [int64]$reader.ReadUInt32()
                if ($chunkLength -gt $stream.Length - $stream.Position) { throw "Truncated WAV chunk: $path" }
                if ($chunkName -eq 'fmt ') {
                    if ($chunkLength -lt 16) { throw "Invalid WAV format chunk: $path" }
                    [void]$reader.ReadUInt16()
                    [void]$reader.ReadUInt16()
                    [void]$reader.ReadUInt32()
                    $bytesPerSecond = [int64]$reader.ReadUInt32()
                    $stream.Position += $chunkLength - 12
                }
                elseif ($chunkName -eq 'data') {
                    $dataBytes = $chunkLength
                    $stream.Position += $chunkLength
                }
                else { $stream.Position += $chunkLength }
                if (($chunkLength % 2) -ne 0 -and $stream.Position -lt $stream.Length) { $stream.Position++ }
            }
            if ($bytesPerSecond -le 0 -or $dataBytes -le 0) { throw "WAV has no readable PCM data: $path" }
            return [double]$dataBytes / $bytesPerSecond
        }
        finally { $reader.Dispose() }
    }
    finally { $stream.Dispose() }
}

function Write-DemoNarration([string]$destination) {
    Add-Type -AssemblyName System.Speech
    $synth = [System.Speech.Synthesis.SpeechSynthesizer]::new()
    $segments = @(
        [pscustomobject]@{ Start = 1; Limit = 25; Chapter = 'CHAPTER 1/6 · REAL-TO-SIM SPATIAL PERCEPTION'; Caption = 'Synthetic tabletop replay · local contours · paired masks'; Text = 'OmniTwin turns a tabletop view into a spatial digital twin. This recording uses a clearly labeled synthetic replay and local contour tracking; its masks are not presented as a SAM response. Five agents share the same workspace model.' },
        [pscustomobject]@{ Start = 26; Limit = 25; Chapter = 'CHAPTER 2/6 · ONE WORKSPACE, THREE CAMERA PRESETS'; Caption = 'Map and chase views inspect the same world state'; Text = 'The map and chase views inspect the same workspace at different scales. Ground vehicles and aerial agents publish telemetry, planned routes, and obstacle-aware controls against the same current observation.' },
        [pscustomobject]@{ Start = 51; Limit = 24; Chapter = 'CHAPTER 3/6 · CORDON AND GRID SEARCH'; Caption = 'Validated mission commands · route checks · safe hold'; Text = 'Operators can switch from perimeter patrol to hazard quarantine and grid search. New geometry is checked against planned routes; an affected agent is held safely when its route becomes blocked.' },
        [pscustomobject]@{ Start = 76; Limit = 34; Chapter = 'CHAPTER 4/6 · MUSE SPARK OPEN, ROSLYN, APPCONTAINER'; Caption = 'Scripted text command · real backend result · isolated worker'; Text = 'This recording inserts a scripted mission as text, without claiming microphone transcription. With a configured Baseten credential, Muse Spark Open can request a policy. Roslyn verifies the source, then the isolated Windows PolicyWorker executes validated directives. The HUD reports the actual result.' },
        [pscustomobject]@{ Start = 111; Limit = 24; Chapter = 'CHAPTER 5/6 · OBJECT SEMANTICS AND MUSE IMAGE'; Caption = 'Synthetic replay does not claim model classification'; Text = 'Voice, object semantics, and Muse Image are separate services. This local-CV replay does not claim a model classification. A surface generation request is sent only when the backend reports a configured Meta credential, and its status comes from the service.' },
        [pscustomobject]@{ Start = 136; Limit = 18; Chapter = 'CHAPTER 6/6 · OMNITWIN DIGITAL-TWIN PIPELINE'; Caption = '.NET 10 · Unity · local perception · isolated policy worker'; Text = 'OmniTwin combines .NET services, Unity simulation, LiDAR, and isolated policy execution. Provider and sandbox cards show observed readiness. This concludes the standalone presentation.' }
    )
    $durations = [System.Collections.Generic.List[double]]::new()
    $srt = [System.Collections.Generic.List[string]]::new()
    try {
        $voice = $synth.GetInstalledVoices() | Where-Object { $_.VoiceInfo.Culture.Name -eq 'en-US' } | Select-Object -First 1
        if ($null -eq $voice) { throw 'No installed English (United States) System.Speech voice is available.' }
        $synth.SelectVoice($voice.VoiceInfo.Name)
        $synth.Rate = -1
        $synth.Volume = 100
        for ($index = 0; $index -lt $segments.Count; $index++) {
            $segment = $segments[$index]
            $wavPath = Join-Path $destination ("narration-{0}.wav" -f ($index + 1))
            $synth.SetOutputToWaveFile($wavPath)
            $synth.Speak($segment.Text)
            $synth.SetOutputToNull()
            $duration = Get-WaveDurationSeconds $wavPath
            if ($duration -gt ($segment.Limit - 2)) { throw "Narration chapter $($index + 1) takes $([math]::Round($duration,1)) seconds; its scene is only $($segment.Limit) seconds." }
            $durations.Add($duration)
            $captionEnd = [math]::Min($segment.Start + $segment.Limit - 1, $segment.Start + $duration + 2)
            $startTime = [TimeSpan]::FromSeconds($segment.Start)
            $endTime = [TimeSpan]::FromSeconds($captionEnd)
            $srt.Add([string]($index + 1))
            $srt.Add(('{0:hh\:mm\:ss\,fff} --> {1:hh\:mm\:ss\,fff}' -f $startTime, $endTime))
            $srt.Add($segment.Chapter + "`r`n" + $segment.Caption)
            $srt.Add('')
        }
        [System.IO.File]::WriteAllText((Join-Path $destination 'subtitles.srt'), ($srt -join "`r`n"), [System.Text.UTF8Encoding]::new($false))
        return [pscustomobject]@{ Segments = $segments; Durations = $durations.ToArray(); Voice = $voice.VoiceInfo.Name }
    }
    finally { $synth.Dispose() }
}

function Start-NativeProcess([string]$filePath, [string[]]$arguments, [string]$workingDirectory, [string]$stdoutPath, [string]$stderrPath, [bool]$redirectInput = $false) {
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
    return [pscustomobject]@{ Process = $process; Stdout = $stdoutTask; Stderr = $stderrTask; StdoutPath = $stdoutPath; StderrPath = $stderrPath }
}

function Save-NativeProcessLogs($handle) {
    if ($null -eq $handle) { return }
    try {
        if (-not $handle.Process.HasExited) { return }
        [void]$handle.Process.WaitForExit(5000)
        $stdout = $handle.Stdout.GetAwaiter().GetResult()
        $stderr = $handle.Stderr.GetAwaiter().GetResult()
        [System.IO.File]::WriteAllText($handle.StdoutPath, $stdout, [System.Text.UTF8Encoding]::new($false))
        if ($handle.StderrPath -ne $handle.StdoutPath) {
            [System.IO.File]::WriteAllText($handle.StderrPath, $stderr, [System.Text.UTF8Encoding]::new($false))
        }
        elseif (-not [string]::IsNullOrWhiteSpace($stderr)) {
            [System.IO.File]::AppendAllText($handle.StdoutPath, "`r`n--- STDERR ---`r`n" + $stderr, [System.Text.UTF8Encoding]::new($false))
        }
    }
    catch { Write-Warning 'Could not persist a child-process diagnostic log.' }
}

function Get-FreeLoopbackPort {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    try { $listener.Start(); return ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port }
    finally { $listener.Stop() }
}

try {
    New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
    New-Item -ItemType Directory -Path $buildRoot -Force | Out-Null
    if (-not (Test-Path -LiteralPath $unityProject -PathType Container)) { throw "Unity project not found: $unityProject" }

    $dotnetPath = (Resolve-Executable 'dotnet')
    $localSdkRoot = Join-Path $repoRoot '..\..\work\dotnet-10.0.403'
    if (Test-Path -LiteralPath (Join-Path $localSdkRoot 'dotnet.exe')) {
        $env:DOTNET_ROOT = (Resolve-Path $localSdkRoot).Path
        $env:PATH = "$env:DOTNET_ROOT;$env:PATH"
        $dotnetPath = Join-Path $env:DOTNET_ROOT 'dotnet.exe'
    }
    Push-Location $repoRoot
    try {
        $sdkVersion = (& $dotnetPath --version 2>&1 | Out-String).Trim()
        if ($LASTEXITCODE -ne 0 -or $sdkVersion -notmatch '^10\.') { throw "OmniTwin requires the repository's .NET 10 SDK; detected '$sdkVersion'." }
        $credentialStatus = [ordered]@{
            MetaConfigured = -not [string]::IsNullOrWhiteSpace($env:META_API_KEY)
            BasetenConfigured = -not [string]::IsNullOrWhiteSpace($env:BASETEN_API_KEY)
            ValuesPrinted = $false
        }
        Write-Host "SDK: .NET $sdkVersion · META_API_KEY: $($credentialStatus.MetaConfigured) · BASETEN_API_KEY: $($credentialStatus.BasetenConfigured)"
        Write-Host 'Credential values are never printed or written by this script.'

        if (-not $SkipDotNetTests) {
            Write-Host 'Running .NET solution tests...'
            & $dotnetPath test OmniTwin.sln -c Release --no-restore
            if ($LASTEXITCODE -ne 0) { throw "dotnet test failed with exit code $LASTEXITCODE." }
        }
        & $dotnetPath build src/OmniTwin.Backend/OmniTwin.Backend.csproj -c Release --no-restore --no-incremental
        if ($LASTEXITCODE -ne 0) { throw "Backend build failed with exit code $LASTEXITCODE." }

        $unityCli = Resolve-Executable 'unity'
        $ffmpeg = Resolve-Executable 'ffmpeg'
        $ffprobe = Resolve-Executable 'ffprobe'
        $encoderCheck = & $ffmpeg -hide_banner -encoders 2>&1 | Select-String '\blibx264\b'
        if (-not $encoderCheck) { throw 'Installed ffmpeg does not provide the libx264 encoder.' }

        $narration = Write-DemoNarration $runRoot
        if (-not $SkipUnityBuild) {
            Write-Host 'Building StandaloneWindows64 from the existing Unity project...'
            $unityBuildLog = Join-Path $runRoot 'unity-build.log'
            $editorProcess = Get-CimInstance Win32_Process -Filter "name='Unity.exe'" -ErrorAction SilentlyContinue |
                Where-Object { $_.CommandLine -and $_.CommandLine.IndexOf($unityProject, [StringComparison]::OrdinalIgnoreCase) -ge 0 -and $_.CommandLine -notmatch 'AssetImportWorker|batchMode' } |
                Select-Object -First 1
            if ($editorProcess) {
                $runArguments = @('command', '--project-path', $unityProject, 'build', 'run', '--target', 'StandaloneWindows64', '--outputPath', $playerPath,
                    '--scenes', '["Assets/_OmniTwin/Scenes/TacticalDesk.unity"]', '--confirm', 'true', '--timeout', '1200')
                $runOutput = (& $unityCli @runArguments 2>&1 | Tee-Object -FilePath $unityBuildLog | Out-String)
                if ($LASTEXITCODE -ne 0) { throw "Unity Editor build request failed. See $unityBuildLog" }
                $runJsonMatch = [regex]::Match($runOutput, '\{[^\r\n]+\}')
                if (-not $runJsonMatch.Success) { throw "Unity Editor returned no build status. See $unityBuildLog" }
                $runResult = $runJsonMatch.Value | ConvertFrom-Json
                if ($runResult.status -eq 'error' -or $runResult.status -ne 'queued') { throw "Unity Editor refused the build: $($runResult.message)" }
                $buildDeadline = [DateTimeOffset]::UtcNow.AddMinutes(20)
                $buildComplete = $false
                while ([DateTimeOffset]::UtcNow -lt $buildDeadline -and -not $buildComplete) {
                    Start-Sleep -Seconds 5
                    $statusArguments = @('command', '--project-path', $unityProject, 'build', 'status')
                    $statusOutput = (& $unityCli @statusArguments 2>&1 | Tee-Object -FilePath $unityBuildLog -Append | Out-String)
                    if ($LASTEXITCODE -ne 0) { throw "Could not poll Unity build status. See $unityBuildLog" }
                    $statusJsonMatch = [regex]::Match($statusOutput, '\{[^\r\n]+\}')
                    if (-not $statusJsonMatch.Success) { throw "Unity Editor returned no build status. See $unityBuildLog" }
                    $buildStatus = $statusJsonMatch.Value | ConvertFrom-Json
                    if ($buildStatus.status -eq 'error') { throw "Unity Editor build failed: $($buildStatus.message)" }
                    if ($buildStatus.status -eq 'completed') {
                        if ($buildStatus.success -ne $true) { throw "Unity Editor build report marked the build unsuccessful. See $unityBuildLog" }
                        $buildComplete = $true
                    }
                }
                if (-not $buildComplete) { throw "Unity Editor build exceeded 20 minutes. See $unityBuildLog" }
            }
            else {
                & $unityCli build $unityProject --target StandaloneWindows64 --output-path $playerPath --allow-dirty-build --timeout 1200 --provenance-path (Join-Path $runRoot 'unity-build-provenance.json') --no-accelerator
                if ($LASTEXITCODE -ne 0) { throw "Unity build failed with exit code $LASTEXITCODE." }
            }
        }
        if (-not (Test-Path -LiteralPath $playerPath -PathType Leaf)) { throw "Standalone Player was not produced: $playerPath" }
        $projectVersionText = Get-Content -Raw (Join-Path $unityProject 'ProjectSettings/ProjectVersion.txt')
        $unityVersionMatch = [regex]::Match($projectVersionText, '(?m)^m_EditorVersion:\s*(\S+)')
        if (-not $unityVersionMatch.Success) { throw 'Could not identify the Unity Editor version from ProjectVersion.txt.' }
        $playerFile = Get-Item -LiteralPath $playerPath
        $buildProvenance = [ordered]@{
            CreatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
            BuildTarget = 'StandaloneWindows64'
            BuildMode = $buildMode
            UnityEditorVersion = $unityVersionMatch.Groups[1].Value
            PlayerPath = $playerFile.FullName
            PlayerLengthBytes = $playerFile.Length
            PlayerLastWriteUtc = $playerFile.LastWriteTimeUtc.ToString('O')
            UnityBuildLog = if (Test-Path -LiteralPath (Join-Path $runRoot 'unity-build.log')) { Join-Path $runRoot 'unity-build.log' } else { $null }
            UnityCliProvenance = if (Test-Path -LiteralPath (Join-Path $runRoot 'unity-build-provenance.json')) { Join-Path $runRoot 'unity-build-provenance.json' } else { $null }
        }
        [System.IO.File]::WriteAllText((Join-Path $runRoot 'build-provenance.json'), ($buildProvenance | ConvertTo-Json -Depth 5), [System.Text.UTF8Encoding]::new($false))

        $port = Get-FreeLoopbackPort
        $env:OmniTwin__Port = [string]$port
        $backendArguments = @('run', '--project', 'src/OmniTwin.Backend/OmniTwin.Backend.csproj', '-c', 'Release', '--no-build', '--', '--synthetic-tabletop-replay')
        $backendHandle = Start-NativeProcess $dotnetPath $backendArguments $repoRoot $backendLogPath $backendLogPath
        $backendProcess = $backendHandle.Process
        $origin = "http://127.0.0.1:$port"
        $ready = $false
        $deadline = [DateTimeOffset]::UtcNow.AddSeconds(40)
        while ([DateTimeOffset]::UtcNow -lt $deadline -and -not $ready) {
            if ($backendProcess.HasExited) { throw "Backend exited before becoming ready. See $backendLogPath" }
            try {
                $status = Invoke-RestMethod -Uri "$origin/api/status" -TimeoutSec 2
                $world = Invoke-RestMethod -Uri "$origin/api/world/latest" -TimeoutSec 2
                $ready = $status.state -eq 'running' -and $status.isObservationCurrent -and
                    ($world.sourceMode -eq 1 -or $world.sourceMode -eq 'SyntheticTabletopReplay')
                if ($ready) {
                    $tracks = @($world.world.obstacles).Count
                    if ($tracks -lt 3) { $ready = $false }
                }
            }
            catch { Start-Sleep -Milliseconds 300 }
        }
        if (-not $ready) { throw "Synthetic backend did not publish a current three-object replay on $origin within 40 seconds." }
        Write-Host "Replay backend ready on loopback port $port with $tracks obstacle tracks; source mode is explicitly synthetic."

        Remove-Item -LiteralPath $statusMarker -Force -ErrorAction SilentlyContinue
        $playerArguments = @('-screen-fullscreen', '1', '-screen-width', '1920', '-screen-height', '1080', '--omnitwin-demo', "--omnitwin-backend-origin=$origin", "--omnitwin-demo-marker=$statusMarker")
        $playerHandle = Start-NativeProcess $playerPath $playerArguments $buildRoot $playerLogPath $playerLogPath
        $playerProcess = $playerHandle.Process
        $markerDeadline = [DateTimeOffset]::UtcNow.AddSeconds(35)
        while ([DateTimeOffset]::UtcNow -lt $markerDeadline -and -not (Test-Path -LiteralPath $statusMarker)) {
            if ($playerProcess.HasExited) { throw "Standalone Player exited before the demo timeline started. See $playerLogPath" }
            Start-Sleep -Milliseconds 100
        }
        if (-not (Test-Path -LiteralPath $statusMarker) -or (Get-Content -Raw $statusMarker) -notmatch '^DEMO_STARTED') {
            throw "Standalone Player did not signal demo startup. See $playerLogPath"
        }

        $captureArgs = @('-hide_banner', '-loglevel', 'warning', '-f', 'gdigrab', '-framerate', '30', '-video_size', '1920x1080', '-i', 'desktop', '-t', '165', '-an', '-c:v', 'libx264', '-preset', 'medium', '-crf', '18', '-pix_fmt', 'yuv420p', '-movflags', '+faststart', $rawVideoPath)
        $captureHandle = Start-NativeProcess $ffmpeg $captureArgs $runRoot $ffmpegLogPath $ffmpegLogPath $true
        $captureProcess = $captureHandle.Process
        Write-Host 'Recording fullscreen Player at 1920×1080 / 30 FPS for the authored six-act timeline...'
        $playerDeadline = [DateTimeOffset]::UtcNow.AddSeconds(163)
        while (-not $playerProcess.HasExited -and [DateTimeOffset]::UtcNow -lt $playerDeadline) { Start-Sleep -Milliseconds 250 }
        if (-not $playerProcess.HasExited) {
            $playerProcess.Kill($true)
            throw 'Standalone Player exceeded the 163-second timeline limit.'
        }
        if ($captureProcess -and -not $captureProcess.HasExited) {
            $captureProcess.StandardInput.WriteLine('q')
            $captureProcess.StandardInput.Flush()
            if (-not $captureProcess.WaitForExit(15000)) { $captureProcess.Kill($true); throw 'ffmpeg did not close the desktop capture cleanly.' }
        }
        if ($captureProcess.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $rawVideoPath)) { throw "Desktop capture failed. See $ffmpegLogPath" }

        $audioInputs = [System.Collections.Generic.List[string]]::new()
        $audioFilters = [System.Collections.Generic.List[string]]::new()
        for ($index = 0; $index -lt $narration.Segments.Count; $index++) {
            $inputNumber = $index + 1
            $delayMs = [int]($narration.Segments[$index].Start * 1000)
            $audioInputs.Add('-i'); $audioInputs.Add((Join-Path $runRoot ("narration-{0}.wav" -f ($index + 1))))
            $audioFilters.Add("[$inputNumber`:a]adelay=delays=$delayMs`:all=1[a$index]")
        }
        $labels = for ($index = 0; $index -lt $narration.Segments.Count; $index++) { "[a$index]" }
        $audioFilters.Add(($labels -join '') + 'amix=inputs=6:duration=longest:normalize=0,apad[aout]')
        $filterComplex = $audioFilters -join ';'
        $muxArgs = @('-hide_banner', '-loglevel', 'warning', '-i', $rawVideoPath) + $audioInputs.ToArray() + @('-i', (Join-Path $runRoot 'subtitles.srt'), '-filter_complex', $filterComplex, '-map', '0:v:0', '-map', '[aout]', '-map', '7:0', '-c:v', 'libx264', '-preset', 'medium', '-crf', '18', '-pix_fmt', 'yuv420p', '-fps_mode', 'cfr', '-r', '30', '-c:a', 'aac', '-b:a', '192k', '-c:s', 'mov_text', '-t', '165', '-shortest', '-movflags', '+faststart', $finalVideoPath)
        $muxHandle = Start-NativeProcess $ffmpeg $muxArgs $runRoot (Join-Path $runRoot 'mux.log') (Join-Path $runRoot 'mux.log')
        if (-not $muxHandle.Process.WaitForExit(120000)) { $muxHandle.Process.Kill($true); throw 'MP4 mux exceeded two minutes.' }
        Save-NativeProcessLogs $muxHandle
        if ($muxHandle.Process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $finalVideoPath)) { throw 'H.264/AAC MP4 creation failed; inspect mux.log.' }
        $createdVideo = $true

        $probeText = & $ffprobe -v error -show_entries 'format=duration,size:stream=codec_type,codec_name,width,height,avg_frame_rate' -of json $finalVideoPath 2>&1 | Out-String
        if ($LASTEXITCODE -ne 0) { throw "ffprobe rejected the final MP4: $probeText" }
        $probe = $probeText | ConvertFrom-Json
        $video = $probe.streams | Where-Object { $_.codec_type -eq 'video' } | Select-Object -First 1
        $audio = $probe.streams | Where-Object { $_.codec_type -eq 'audio' } | Select-Object -First 1
        $subtitles = $probe.streams | Where-Object { $_.codec_type -eq 'subtitle' } | Select-Object -First 1
        $durationSeconds = [double]$probe.format.duration
        if ($null -eq $video -or $null -eq $audio -or $null -eq $subtitles -or $video.width -ne 1920 -or $video.height -ne 1080 -or $video.avg_frame_rate -ne '30/1' -or $durationSeconds -lt 120 -or $durationSeconds -gt 175) {
            throw "MP4 validation failed: duration=$durationSeconds, video=$($video.width)x$($video.height), audio=$($null -ne $audio), subtitles=$($null -ne $subtitles)."
        }
        $blackOutput = & $ffmpeg -hide_banner -i $finalVideoPath -vf 'blackdetect=d=0.05:pix_th=0.015' -an -f null NUL 2>&1 | Out-String
        if ($LASTEXITCODE -ne 0) { throw 'Black-frame validation could not decode the MP4.' }
        $blackIntervals = ([regex]::Matches($blackOutput, 'black_start:')).Count
        if ($blackIntervals -gt 0) { throw "Black-frame validation found $blackIntervals black interval(s) lasting at least 50 ms." }

        $verification = [ordered]@{
            CreatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
            BuildTarget = 'StandaloneWindows64'
            UnityProject = $unityProject
            Video = $finalVideoPath
            DurationSeconds = [math]::Round($durationSeconds, 3)
            Resolution = '1920x1080'
            VideoCodec = $video.codec_name
            AudioCodec = $audio.codec_name
            SubtitleCodec = $subtitles.codec_name
            FrameRate = $video.avg_frame_rate + ' CFR'
            BlackIntervalsAt50msThreshold = $blackIntervals
            NarrationVoice = $narration.Voice
            SourceMode = 'SyntheticTabletopReplay; local CV only; no SAM output represented'
            MetaCredentialConfiguredAtLaunch = $credentialStatus.MetaConfigured
            BasetenCredentialConfiguredAtLaunch = $credentialStatus.BasetenConfigured
            CredentialsPrinted = $false
            BuildProvenance = Join-Path $runRoot 'build-provenance.json'
            BackendLog = $backendLogPath
            PlayerLog = $playerLogPath
        }
        [System.IO.File]::WriteAllText($verificationPath, ($verification | ConvertTo-Json -Depth 5), [System.Text.UTF8Encoding]::new($false))
        Write-Host "DEMO COMPLETE: $finalVideoPath"
        Write-Host "Duration $([math]::Round($durationSeconds,1)) s · 1920×1080 · $($video.codec_name)/$($audio.codec_name) · 30 fps CFR · switchable subtitles embedded; HUD captions remain visible · no detected black interval ≥50 ms."
        Write-Host "Run logs and verification: $runRoot"
    }
    finally { Pop-Location }
}
catch {
    if ($createdVideo -and (Test-Path -LiteralPath $verificationPath)) { Write-Host "A partial result was created; see $verificationPath" }
    Write-Error $_
    exit 1
}
finally {
    if ($captureProcess -and -not $captureProcess.HasExited) { try { $captureProcess.Kill($true) } catch { } }
    if ($playerProcess -and -not $playerProcess.HasExited) { try { $playerProcess.Kill($true) } catch { } }
    if ($backendProcess -and -not $backendProcess.HasExited) { try { $backendProcess.Kill($true) } catch { } }
    foreach ($handle in @($captureHandle, $playerHandle, $backendHandle)) {
        if ($handle -and -not $handle.Process.HasExited) { try { [void]$handle.Process.WaitForExit(5000) } catch { } }
        Save-NativeProcessLogs $handle
    }
    if ($null -eq $previousPort) { Remove-Item Env:OmniTwin__Port -ErrorAction SilentlyContinue }
    else { $env:OmniTwin__Port = $previousPort }
}
