[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$RawRecordingPath,
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$rawPath = (Resolve-Path -LiteralPath $RawRecordingPath).Path
$runRoot = Split-Path -Parent $rawPath
if ([string]::IsNullOrWhiteSpace($OutputPath)) { $OutputPath = Join-Path $repoRoot 'OmniTwin-Hackathon-Demo.mp4' }
$outputFullPath = [System.IO.Path]::GetFullPath($OutputPath)
$partialPath = $outputFullPath + '.partial.mp4'
$voiceRoot = Join-Path $runRoot 'voiceover'
$framesRoot = Join-Path $runRoot 'verification-frames'
$subtitlePath = Join-Path $voiceRoot 'subtitles.srt'
$verificationPath = Join-Path $runRoot 'final-video-verification.json'

function Resolve-Executable([string]$name) {
    $command = Get-Command $name -ErrorAction Stop
    return $command.Source
}

function Get-WaveDurationSeconds([string]$path) {
    $stream = [System.IO.File]::OpenRead($path)
    try {
        $reader = [System.IO.BinaryReader]::new($stream)
        try {
            if ([System.Text.Encoding]::ASCII.GetString($reader.ReadBytes(4)) -ne 'RIFF') { throw "Invalid WAV header: $path" }
            [void]$reader.ReadUInt32()
            if ([System.Text.Encoding]::ASCII.GetString($reader.ReadBytes(4)) -ne 'WAVE') { throw "Invalid WAV format: $path" }
            $bytesPerSecond = 0L; $dataBytes = 0L
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
                elseif ($chunkName -eq 'data') { $dataBytes = $chunkLength; $stream.Position += $chunkLength }
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

if (-not (Test-Path -LiteralPath $runRoot -PathType Container)) { throw 'The OBS recording directory is unavailable.' }
if (Test-Path -LiteralPath $outputFullPath) { throw "Output already exists; move it aside before producing a new final video: $outputFullPath" }
if (Test-Path -LiteralPath $partialPath) { throw "Partial output already exists; inspect it before retrying: $partialPath" }
New-Item -ItemType Directory -Force -Path $voiceRoot, $framesRoot, (Split-Path -Parent $outputFullPath) | Out-Null

$ffmpeg = Resolve-Executable 'ffmpeg'
$ffprobe = Resolve-Executable 'ffprobe'
$rawProbeText = & $ffprobe -v error -show_entries 'format=duration:stream=codec_type,codec_name,width,height,avg_frame_rate' -of json $rawPath 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) { throw 'ffprobe could not read the OBS recording.' }
$rawProbe = $rawProbeText | ConvertFrom-Json
$rawVideo = $rawProbe.streams | Where-Object { $_.codec_type -eq 'video' } | Select-Object -First 1
$rawDuration = [double]$rawProbe.format.duration
if ($null -eq $rawVideo -or $rawVideo.width -ne 1920 -or $rawVideo.height -ne 1080 -or $rawVideo.avg_frame_rate -ne '30/1') {
    throw "OBS recording must be a native 1920x1080 30fps window capture; got $($rawVideo.width)x$($rawVideo.height) at $($rawVideo.avg_frame_rate)."
}
if ($rawDuration -lt 150 -or $rawDuration -gt 168) { throw "Expected the 10-second calibration shot plus the 155-second live Unity timeline; OBS duration was $([math]::Round($rawDuration, 2)) seconds." }

$segments = @(
    [pscustomobject]@{ Start = 1; Limit = 34; Chapter = 'CHAPTER 1/6 · FOUR-POINT CALIBRATION & LIVE PERCEPTION'; Caption = '4-point homography · .NET 10 · 451 passing tests'; Text = 'OmniTwin turns a calibrated overhead camera into a spatial digital twin. Four tabletop corners define a projective mapping into a twenty by twelve point five metre world. The live camera feeds SAM three point one, and validated masks become measured obstacle tracks.' },
    [pscustomobject]@{ Start = 36; Limit = 24; Chapter = 'CHAPTER 2/6 · CAMERA, MASKS & OBJECT MOTION'; Caption = 'Live camera frames · SAM-verified masks · local motion between captures'; Text = 'Each accepted SAM mask carries its capture identity and confidence. A lightweight local motion estimator follows the object between segmentation updates, while the camera preview and world track remain synchronized to their actual observations.' },
    [pscustomobject]@{ Start = 61; Limit = 24; Chapter = 'CHAPTER 3/6 · CORDON & GRID SEARCH'; Caption = 'Operator hazard tag · validated swarm missions · visible LiDAR'; Text = 'For this run, the operator explicitly marks the duck as a hazard; this is a mission override, not model inference. The ground vehicles establish a safety cordon, then the swarm switches to systematic grid search with LiDAR obstacle checks.' },
    [pscustomobject]@{ Start = 86; Limit = 34; Chapter = 'CHAPTER 4/6 · MUSE VOICE · ROSLYN · APPCONTAINER'; Caption = 'Fresh speech transcript when available · verified policy · isolated worker'; Text = 'A spoken command can enter through Muse Voice, with a visible text fallback if transcription is unavailable. Muse Spark Open may propose a policy. Roslyn checks its symbols, then a separate Windows AppContainer worker evaluates bounded directives. The HUD reports the result of this run.' },
    [pscustomobject]@{ Start = 121; Limit = 24; Chapter = 'CHAPTER 5/6 · OBJECT SEMANTICS & MUSE IMAGE'; Caption = 'Model tags and operator overrides remain distinct · image result or local fallback'; Text = 'Object semantics keeps model classifications separate from operator overrides. Muse Image is requested through the backend. A validated image is applied when returned; otherwise the authored tactical surface remains visible and the status says why.' },
    [pscustomobject]@{ Start = 146; Limit = 18; Chapter = 'CHAPTER 6/6 · LIVE DIGITAL-TWIN PIPELINE'; Caption = 'Five agents · .NET 10 · Unity · isolated policy execution'; Text = 'OmniTwin combines live camera perception, five agents, LiDAR, and isolated policy execution in one workspace. The HUD reports measured status for this run.' }
)

Add-Type -AssemblyName System.Speech
$synth = [System.Speech.Synthesis.SpeechSynthesizer]::new()
$durations = [System.Collections.Generic.List[double]]::new()
$srt = [System.Collections.Generic.List[string]]::new()
try {
    $voice = $synth.GetInstalledVoices() | Where-Object { $_.VoiceInfo.Culture.Name -eq 'en-US' } | Select-Object -First 1
    if ($null -eq $voice) { throw 'No installed English (United States) System.Speech voice is available.' }
    $synth.SelectVoice($voice.VoiceInfo.Name); $synth.Rate = -1; $synth.Volume = 100
    for ($index = 0; $index -lt $segments.Count; $index++) {
        $segment = $segments[$index]
        $wavPath = Join-Path $voiceRoot ("narration-{0}.wav" -f ($index + 1))
        $synth.SetOutputToWaveFile($wavPath); $synth.Speak($segment.Text); $synth.SetOutputToNull()
        $duration = Get-WaveDurationSeconds $wavPath
        if ($duration -gt ($segment.Limit - 2)) { throw "Narration chapter $($index + 1) exceeds its scene budget ($([math]::Round($duration, 1)) s)." }
        $durations.Add($duration)
        $end = [math]::Min($segment.Start + $segment.Limit - 1, $segment.Start + $duration + 2)
        $srt.Add([string]($index + 1)); $srt.Add((ConvertTo-SrtTime $segment.Start) + ' --> ' + (ConvertTo-SrtTime $end))
        $srt.Add($segment.Chapter + "`r`n" + $segment.Caption); $srt.Add('')
    }
    [System.IO.File]::WriteAllText($subtitlePath, ($srt -join "`r`n"), [System.Text.UTF8Encoding]::new($false))
}
finally { $synth.Dispose() }

$audioInputs = [System.Collections.Generic.List[string]]::new()
$audioFilters = [System.Collections.Generic.List[string]]::new()
for ($index = 0; $index -lt $segments.Count; $index++) {
    $inputNumber = $index + 1; $delayMs = [int]($segments[$index].Start * 1000)
    $audioInputs.Add('-i'); $audioInputs.Add((Join-Path $voiceRoot ("narration-{0}.wav" -f ($index + 1))))
    $audioFilters.Add("[$inputNumber`:a]adelay=delays=$delayMs`:all=1[a$index]")
}
$labels = for ($index = 0; $index -lt $segments.Count; $index++) { "[a$index]" }
$audioFilters.Add(($labels -join '') + 'amix=inputs=6:duration=longest:normalize=0,apad[aout]')
$filterComplex = $audioFilters -join ';'
$muxArguments = @('-hide_banner', '-loglevel', 'warning', '-i', $rawPath) + $audioInputs.ToArray() + @(
    '-i', $subtitlePath, '-filter_complex', $filterComplex, '-map', '0:v:0', '-map', '[aout]', '-map', '7:0',
    '-c:v', 'libx264', '-preset', 'medium', '-crf', '18', '-pix_fmt', 'yuv420p', '-fps_mode', 'cfr', '-r', '30',
    '-c:a', 'aac', '-b:a', '192k', '-c:s', 'mov_text', '-t', '170', '-shortest', '-movflags', '+faststart', $partialPath)
& $ffmpeg @muxArguments
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $partialPath)) { throw 'H.264/AAC mux failed; the partial file has not been promoted.' }

$finalProbeText = & $ffprobe -v error -show_entries 'format=duration,size:stream=codec_type,codec_name,width,height,avg_frame_rate' -of json $partialPath 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) { throw 'ffprobe could not validate the muxed MP4.' }
$finalProbe = $finalProbeText | ConvertFrom-Json
$video = $finalProbe.streams | Where-Object { $_.codec_type -eq 'video' } | Select-Object -First 1
$audio = $finalProbe.streams | Where-Object { $_.codec_type -eq 'audio' } | Select-Object -First 1
$subtitles = $finalProbe.streams | Where-Object { $_.codec_type -eq 'subtitle' } | Select-Object -First 1
$durationSeconds = [double]$finalProbe.format.duration
if ($null -eq $video -or $null -eq $audio -or $null -eq $subtitles -or $video.width -ne 1920 -or $video.height -ne 1080 -or $video.avg_frame_rate -ne '30/1' -or $durationSeconds -lt 140 -or $durationSeconds -gt 170) {
    throw "Final MP4 validation failed: duration=$durationSeconds, video=$($video.width)x$($video.height)@$($video.avg_frame_rate), audio=$($null -ne $audio), subtitles=$($null -ne $subtitles)."
}
$blackLog = & $ffmpeg -hide_banner -i $partialPath -vf 'blackdetect=d=0.05:pix_th=0.015' -an -f null NUL 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) { throw 'Black-frame validation could not decode the MP4.' }
$blackIntervals = ([regex]::Matches($blackLog, 'black_start:')).Count
if ($blackIntervals -gt 0) { throw "Black-frame validation found $blackIntervals interval(s) lasting at least 50 ms." }

$frameTimes = @(5, 49, 73, 103, 133, 155)
$frameNames = @('scene-1-calibration', 'scene-2-live-tracking', 'scene-3-cordon-search', 'scene-4-voice-brain', 'scene-5-semantics-image', 'scene-6-outro')
for ($index = 0; $index -lt $frameTimes.Count; $index++) {
    $framePath = Join-Path $framesRoot ($frameNames[$index] + '.png')
    & $ffmpeg -hide_banner -loglevel error -ss $frameTimes[$index] -i $partialPath -frames:v 1 -update 1 $framePath
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $framePath)) { throw "Could not extract verification frame $($index + 1)." }
}

Move-Item -LiteralPath $partialPath -Destination $outputFullPath
$verification = [ordered]@{
    CreatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    Source = 'Windows screen capture of the completed calibration view and live-camera OmniTwin Standalone; generated voice-over replaces source audio.'
    SourceRecording = $rawPath
    Output = $outputFullPath
    DurationSeconds = [math]::Round($durationSeconds, 3)
    Resolution = '1920x1080'
    FrameRate = $video.avg_frame_rate + ' CFR'
    VideoCodec = $video.codec_name
    AudioCodec = $audio.codec_name
    SubtitleCodec = $subtitles.codec_name
    BlackIntervalsAt50msThreshold = $blackIntervals
    NarrationVoice = $voice.VoiceInfo.Name
    NarrationSeconds = @($durations.ToArray())
    VerificationFrames = @($frameNames | ForEach-Object { Join-Path $framesRoot ($_.ToString() + '.png') })
    CredentialsWrittenOrPrinted = $false
}
[System.IO.File]::WriteAllText($verificationPath, ($verification | ConvertTo-Json -Depth 5), [System.Text.UTF8Encoding]::new($false))
Write-Host "FINAL VIDEO: $outputFullPath"
Write-Host "Duration $([math]::Round($durationSeconds, 1)) s · 1920x1080 · 30 fps · H.264/AAC · $($subtitles.codec_name) subtitles"
Write-Host "Verification frames: $framesRoot"
