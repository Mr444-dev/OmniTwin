[CmdletBinding()]
param(
    [string]$CalibrationSourcePath = (Join-Path $PSScriptRoot '..\..\local\demo\live-camera-20261105-012413\calibration-plus-live-unity.mp4'),
    [string]$LiveUnitySourcePath = (Join-Path $PSScriptRoot '..\..\local\demo\live-camera-20261105-012945\unity-live.mp4'),
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\..\local\demo\OmniTwin-Hackathon-Demo-3min-Live-20261105-v2.mp4')
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$calibrationPath = (Resolve-Path -LiteralPath $CalibrationSourcePath).Path
$livePath = (Resolve-Path -LiteralPath $LiveUnitySourcePath).Path
$outputPathFull = [System.IO.Path]::GetFullPath($OutputPath)
$outputDirectory = Split-Path -Parent $outputPathFull
$runDirectory = Join-Path $repoRoot 'local\demo\final-three-minute-20261105'
$rawPath = Join-Path $runDirectory 'calibration-plus-live-camera.mp4'
$rawPartialPath = $rawPath + '.partial.mp4'
$outputPartialPath = $outputPathFull + '.partial.mp4'
$voiceDirectory = Join-Path $runDirectory 'voiceover'
$framesDirectory = Join-Path $runDirectory 'verification-frames'
$subtitlePath = Join-Path $voiceDirectory 'captions.srt'
$verificationPath = Join-Path $runDirectory 'final-video-verification.json'

function Resolve-Executable([string]$name) {
    return (Get-Command $name -ErrorAction Stop).Source
}

function Get-Probe([string]$path, [string]$entries) {
    $text = & $script:ffprobe -v error -show_entries $entries -of json $path 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { throw "FFprobe failed for '$path'." }
    return ($text | ConvertFrom-Json)
}

function Get-WaveDurationSeconds([string]$path) {
    $stream = [System.IO.File]::OpenRead($path)
    try {
        $reader = [System.IO.BinaryReader]::new($stream)
        try {
            if ([System.Text.Encoding]::ASCII.GetString($reader.ReadBytes(4)) -ne 'RIFF') { throw "Invalid WAV header: $path" }
            [void]$reader.ReadUInt32()
            if ([System.Text.Encoding]::ASCII.GetString($reader.ReadBytes(4)) -ne 'WAVE') { throw "Invalid WAV format: $path" }
            $bytesPerSecond = 0L
            $dataBytes = 0L
            while ($stream.Position + 8 -le $stream.Length) {
                $chunkName = [System.Text.Encoding]::ASCII.GetString($reader.ReadBytes(4))
                $chunkLength = [int64]$reader.ReadUInt32()
                if ($chunkLength -gt $stream.Length - $stream.Position) { throw "Truncated WAV chunk: $path" }
                if ($chunkName -eq 'fmt ') {
                    if ($chunkLength -lt 16) { throw "Invalid WAV format chunk: $path" }
                    [void]$reader.ReadUInt16(); [void]$reader.ReadUInt16(); [void]$reader.ReadUInt32()
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
            if ($bytesPerSecond -le 0 -or $dataBytes -le 0) { throw "WAV contains no PCM audio: $path" }
            return [double]$dataBytes / $bytesPerSecond
        }
        finally { $reader.Dispose() }
    }
    finally { $stream.Dispose() }
}

function ConvertTo-SrtTime([double]$seconds) {
    return [TimeSpan]::FromSeconds($seconds).ToString('hh\:mm\:ss\,fff')
}

function Assert-1080p30([string]$path, [string]$label, [double]$minimumDuration, [double]$maximumDuration) {
    $probe = Get-Probe $path 'format=duration:stream=codec_type,width,height,avg_frame_rate'
    $video = $probe.streams | Where-Object { $_.codec_type -eq 'video' } | Select-Object -First 1
    $duration = [double]$probe.format.duration
    if ($null -eq $video -or $video.width -ne 1920 -or $video.height -ne 1080 -or $video.avg_frame_rate -ne '30/1' -or $duration -lt $minimumDuration -or $duration -gt $maximumDuration) {
        throw "$label must be 1920x1080 at 30 fps and $minimumDuration-$maximumDuration seconds; got $($video.width)x$($video.height)@$($video.avg_frame_rate), $duration seconds."
    }
    return $probe
}

if (Test-Path -LiteralPath $outputPathFull) { throw "Refusing to overwrite an existing video: $outputPathFull" }
if (Test-Path -LiteralPath $outputPartialPath) { throw "Inspect the existing partial output before retrying: $outputPartialPath" }
New-Item -ItemType Directory -Force -Path $runDirectory, $voiceDirectory, $framesDirectory, $outputDirectory | Out-Null

$script:ffmpeg = Resolve-Executable 'ffmpeg'
$script:ffprobe = Resolve-Executable 'ffprobe'
$null = Assert-1080p30 $calibrationPath 'Calibration source' 10 200
$liveProbe = Assert-1080p30 $livePath 'Live Unity source' 120 145
$liveDuration = [double]$liveProbe.format.duration
$calibrationDuration = 10.0
$targetDuration = 180.0
$liveTargetDuration = $targetDuration - $calibrationDuration
$slowFactor = $liveTargetDuration / $liveDuration
$slowFactorText = $slowFactor.ToString('0.000000', [Globalization.CultureInfo]::InvariantCulture)

Write-Host "Preparing the 3-minute cut · live camera Unity segment $([math]::Round($liveDuration, 2)) s · playback factor $slowFactorText."
if (Test-Path -LiteralPath $rawPath -PathType Leaf) {
    $rawProbe = Assert-1080p30 $rawPath 'Existing composed source' 179.5 180.5
    Write-Host 'Reusing the previously validated calibration/live composition.'
}
else {
    if (Test-Path -LiteralPath $rawPartialPath) { throw "Inspect the existing partial composition before retrying: $rawPartialPath" }
    $composeFilter = "[0:v]trim=start=0:duration=10,setpts=PTS-STARTPTS,fps=30,scale=1920:1080:flags=lanczos,setsar=1,trim=duration=10[v0];[1:v]trim=start=0:duration=$liveDuration,setpts=(PTS-STARTPTS)*$slowFactorText,fps=30,trim=duration=$liveTargetDuration,scale=1920:1080:flags=lanczos,setsar=1[v1];[v0][v1]concat=n=2:v=1:a=0[outv]"
    $composeArguments = @('-hide_banner', '-loglevel', 'warning', '-i', $calibrationPath, '-i', $livePath, '-filter_complex', $composeFilter, '-map', '[outv]', '-an', '-frames:v', '5400', '-c:v', 'libx264', '-preset', 'medium', '-crf', '18', '-pix_fmt', 'yuv420p', '-fps_mode', 'cfr', '-r', '30', '-movflags', '+faststart', $rawPartialPath)
    & $script:ffmpeg @composeArguments
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $rawPartialPath -PathType Leaf)) { throw 'Could not assemble the calibration and live Unity footage.' }
    $rawProbe = Assert-1080p30 $rawPartialPath 'Composed source' 179.5 180.5
    Move-Item -LiteralPath $rawPartialPath -Destination $rawPath
}

$segments = @(
    [pscustomobject]@{ Start = 0.4; Limit = 9.0; Chapter = 'CHAPTER 1/6 · CAMERA CALIBRATION'; Caption = 'Four tabletop corners map the live camera into the 20 × 12.5 m world.'; Text = 'Four corners calibrate the live camera. Homography maps pixels into world coordinates.' },
    [pscustomobject]@{ Start = 11.5; Limit = 30.5; Chapter = 'CHAPTER 2/6 · LIVE CAMERA & OBJECT TRACKING'; Caption = 'Live camera · track source and freshness · local motion between updates.'; Text = 'The left panel shows the live camera and current object track. When available, each SAM-verified mask carries its capture sequence and confidence. Between updates, the local motion estimate keeps the track responsive, and a fresh SAM result can correct it. The HUD reports observation freshness, while the right panel shows agents acting in the mapped workspace.' },
    [pscustomobject]@{ Start = 43.0; Limit = 30.0; Chapter = 'CHAPTER 3/6 · HAZARD CORDON & GRID SEARCH'; Caption = 'Operator-tagged hazard · perimeter protection · systematic search.'; Text = 'The operator marks the current duck track as a hazard for this mission. The HUD keeps that manual override distinct from model inference. Ground vehicles establish a safety cordon around the tagged object, while aerial units patrol the outer perimeter. The mission then switches to grid search, covering free workspace while LiDAR checks nearby obstacles and other agents.' },
    [pscustomobject]@{ Start = 76.0; Limit = 42.0; Chapter = 'CHAPTER 4/6 · VOICE, ROSLYN & APPCONTAINER'; Caption = 'Typed command in this run · policy verification · isolated execution.'; Text = 'The command dock brings together voice input, policy synthesis, and execution status. This recording uses the typed mission command; no presenter speech is needed. The voice attempt in this run returned no recognized speech, and the typed fallback remains available. Muse Spark Open may propose a policy. Roslyn checks generated code against the contract, and the policy worker evaluates bounded directives in an isolated Windows AppContainer. The HUD shows the actual provider and fallback status.' },
    [pscustomobject]@{ Start = 121.0; Limit = 30.0; Chapter = 'CHAPTER 5/6 · OBJECT SEMANTICS & MUSE IMAGE'; Caption = 'Model classifications and operator overrides stay separate.'; Text = 'Object semantics keeps model classifications separate from the operator hazard tag, so the source of a decision stays clear. The scene generator can request a new surface, but this run reports the real provider status and retains the readable local tactical material when no remote image is returned. The grid remains visible beneath the agents and obstacles, keeping the mapped workspace legible.' },
    [pscustomobject]@{ Start = 154.0; Limit = 24.0; Chapter = 'CHAPTER 6/6 · THE LIVE DIGITAL TWIN'; Caption = 'One camera · five agents · obstacle-aware missions · isolated policies.'; Text = "This is OmniTwin's live digital twin: a calibrated camera, object tracks, five agents, LiDAR, and isolated policy execution. The HUD reports each service's actual status. No presenter speech is needed." }
)

Add-Type -AssemblyName System.Speech
$synth = [System.Speech.Synthesis.SpeechSynthesizer]::new()
$speechDurations = [System.Collections.Generic.List[double]]::new()
$subtitleEntries = [System.Collections.Generic.List[string]]::new()
try {
    $selectedVoice = $synth.GetInstalledVoices() | Where-Object { $_.VoiceInfo.Culture.Name -eq 'en-US' } | Select-Object -First 1
    if ($null -eq $selectedVoice) { throw 'No installed English (United States) text-to-speech voice is available.' }
    $synth.SelectVoice($selectedVoice.VoiceInfo.Name)
    $synth.Rate = -1
    $synth.Volume = 100
    for ($index = 0; $index -lt $segments.Count; $index++) {
        $segment = $segments[$index]
        $wavePath = Join-Path $voiceDirectory ("narration-{0}.wav" -f ($index + 1))
        $synth.SetOutputToWaveFile($wavePath)
        $synth.Speak($segment.Text)
        $synth.SetOutputToNull()
        $duration = Get-WaveDurationSeconds $wavePath
        $speechDurations.Add($duration)
        $cueEnd = [math]::Min($segment.Start + $segment.Limit, $segment.Start + $duration + 2)
        $subtitleEntries.Add([string]($index + 1))
        $subtitleEntries.Add((ConvertTo-SrtTime $segment.Start) + ' --> ' + (ConvertTo-SrtTime $cueEnd))
        $subtitleEntries.Add($segment.Chapter + "`r`n" + $segment.Caption)
        $subtitleEntries.Add('')
    }
    [System.IO.File]::WriteAllText($subtitlePath, ($subtitleEntries -join "`r`n"), [System.Text.UTF8Encoding]::new($false))
}
finally { $synth.Dispose() }

$overlongNarration = @()
for ($index = 0; $index -lt $segments.Count; $index++) {
    if ($speechDurations[$index] -gt ($segments[$index].Limit - 1.5)) {
        $overlongNarration += "chapter $($index + 1): $([math]::Round($speechDurations[$index], 2)) s / $($segments[$index].Limit - 1.5) s allowed"
    }
}
if ($overlongNarration.Count -gt 0) { throw ('Narration scene budgets exceeded: ' + ($overlongNarration -join '; ')) }

$audioInputs = [System.Collections.Generic.List[string]]::new()
$audioFilters = [System.Collections.Generic.List[string]]::new()
for ($index = 0; $index -lt $segments.Count; $index++) {
    $audioInput = $index + 1
    $delayMilliseconds = [int]($segments[$index].Start * 1000)
    $audioInputs.Add('-i')
    $audioInputs.Add((Join-Path $voiceDirectory ("narration-{0}.wav" -f ($index + 1))))
    $audioFilters.Add("[$audioInput`:a]adelay=delays=$delayMilliseconds`:all=1[a$index]")
}
$audioLabels = for ($index = 0; $index -lt $segments.Count; $index++) { "[a$index]" }
$audioFilters.Add(($audioLabels -join '') + 'amix=inputs=6:duration=longest:normalize=0,apad[aout]')
$filterComplex = $audioFilters -join ';'
$muxArguments = @('-hide_banner', '-loglevel', 'warning', '-i', $rawPath) + $audioInputs.ToArray() + @(
    '-i', $subtitlePath, '-filter_complex', $filterComplex, '-map', '0:v:0', '-map', '[aout]', '-map', '7:0',
    '-c:v', 'copy', '-c:a', 'aac', '-b:a', '192k', '-c:s', 'mov_text', '-t', '180', '-movflags', '+faststart', $outputPartialPath)
& $script:ffmpeg @muxArguments
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $outputPartialPath -PathType Leaf)) { throw 'Could not mux English narration and captions into the final MP4.' }

$finalProbe = Get-Probe $outputPartialPath 'format=duration,size:stream=codec_type,codec_name,width,height,avg_frame_rate'
$video = $finalProbe.streams | Where-Object { $_.codec_type -eq 'video' } | Select-Object -First 1
$audio = $finalProbe.streams | Where-Object { $_.codec_type -eq 'audio' } | Select-Object -First 1
$subtitles = $finalProbe.streams | Where-Object { $_.codec_type -eq 'subtitle' } | Select-Object -First 1
$finalDuration = [double]$finalProbe.format.duration
if ($null -eq $video -or $null -eq $audio -or $null -eq $subtitles -or $video.width -ne 1920 -or $video.height -ne 1080 -or $video.avg_frame_rate -ne '30/1' -or $finalDuration -lt 179.5 -or $finalDuration -gt 180.5) {
    throw "Final video validation failed: $finalDuration s; video $($video.width)x$($video.height)@$($video.avg_frame_rate); audio=$($null -ne $audio); subtitles=$($null -ne $subtitles)."
}

$blackLog = & $script:ffmpeg -hide_banner -i $outputPartialPath -vf 'blackdetect=d=0.05:pix_th=0.015' -an -f null NUL 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) { throw 'Could not decode the final video during the black-frame check.' }
$blackIntervalCount = ([regex]::Matches($blackLog, 'black_start:')).Count
if ($blackIntervalCount -gt 0) { throw "Final video contains $blackIntervalCount black interval(s) lasting 50 ms or longer." }

$frameTimes = @(5, 25, 57, 96, 135, 164)
for ($index = 0; $index -lt $frameTimes.Count; $index++) {
    $framePath = Join-Path $framesDirectory ("scene-{0}.png" -f ($index + 1))
    & $script:ffmpeg -hide_banner -loglevel error -y -ss $frameTimes[$index] -i $outputPartialPath -frames:v 1 -update 1 $framePath
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $framePath)) { throw "Could not extract verification frame for scene $($index + 1)." }
}

Move-Item -LiteralPath $outputPartialPath -Destination $outputPathFull
$verification = [ordered]@{
    CreatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    Output = $outputPathFull
    CalibrationSource = $calibrationPath
    LiveCameraUnitySource = $livePath
    LiveUnityDurationSeconds = [math]::Round($liveDuration, 3)
    LivePlaybackFactor = [math]::Round($slowFactor, 6)
    DurationSeconds = [math]::Round($finalDuration, 3)
    Resolution = '1920x1080'
    FrameRate = '30/1 CFR'
    VideoCodec = $video.codec_name
    AudioCodec = $audio.codec_name
    SubtitleCodec = $subtitles.codec_name
    NarrationVoice = $selectedVoice.VoiceInfo.Name
    NarrationDurationsSeconds = @($speechDurations.ToArray())
    BlackIntervalsAt50msThreshold = $blackIntervalCount
    PresenterSpeechRequired = $false
    VerificationFrames = @($frameTimes | ForEach-Object { Join-Path $framesDirectory ("scene-{0}.png" -f ([array]::IndexOf($frameTimes, $_) + 1)) })
}
[System.IO.File]::WriteAllText($verificationPath, ($verification | ConvertTo-Json -Depth 5), [System.Text.UTF8Encoding]::new($false))
Write-Host "FINAL VIDEO: $outputPathFull"
Write-Host "Duration $([math]::Round($finalDuration, 1)) s · 1920x1080 · 30 fps · English $($selectedVoice.VoiceInfo.Name) narration · captions · $blackIntervalCount black intervals"
Write-Host "Verification report: $verificationPath"
