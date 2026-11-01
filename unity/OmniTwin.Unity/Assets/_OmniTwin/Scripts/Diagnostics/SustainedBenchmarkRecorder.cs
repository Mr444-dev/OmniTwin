using System;
using System.Globalization;
using System.IO;
using System.Threading;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.UIElements;
using OmniTwin.Contracts;
using OmniTwin.Simulation.Agents;
using OmniTwin.Simulation.Media;
using OmniTwin.Simulation.Network;
using OmniTwin.Simulation.Runtime;
using OmniTwin.Simulation.Sensors;
using OmniTwin.Transport;

namespace OmniTwin.Simulation.Diagnostics
{
    /// <summary>Runs a bounded standalone replay benchmark and writes measured frame, memory, and policy data.</summary>
    [DisallowMultipleComponent]
    public sealed class SustainedBenchmarkRecorder : MonoBehaviour
    {
        private const int TargetWidth = 1920;
        private const int TargetHeight = 1080;
        private const int WarmupSeconds = 5;
        private const int ReadinessTimeoutSeconds = 60;
        private const int MaximumDurationSeconds = 60;
        private const int MaximumFrames = 36000;
        private const int MaximumPolicySamples = 1024;

        private readonly float[] _frameTimes = new float[MaximumFrames];
        private readonly double[] _policyEvaluationTimes = new double[MaximumPolicySamples];
        private int _durationSeconds = 45;
        private string _reportPath;
        private string _screenshotPath;
        private double _startupTime;
        private double _warmupEnd;
        private double _measurementStart;
        private double _lastMemorySample;
        private int _frameCount;
        private int _policySampleCount;
        private long _lastPolicySequence = -1;
        private double _frameTimeSumMs;
        private long _managedStart, _managedEnd, _managedPeak;
        private long _unityAllocatedStart, _unityAllocatedEnd, _unityAllocatedPeak;
        private long _graphicsStart, _graphicsEnd, _graphicsPeak;
        private long _textureStart, _textureEnd, _texturePeak;
        private int _gen0Start, _gen1Start, _gen2Start;
        private int _gen0End, _gen1End, _gen2End;
        private PolicyExecutor _executor;
        private TwinWebSocketClient _stream;
        private MediaConsoleController _media;
        private int _finished;

        /// <summary>Gets whether the current process explicitly requested the isolated standalone benchmark.</summary>
        public static bool IsRequested
        {
            get
            {
                var arguments = Environment.GetCommandLineArgs();
                for (var i = 0; i < arguments.Length; i++) if (arguments[i] == "--omnitwin-benchmark") return true;
                return false;
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSplashScreen)]
        private static void ConfigureBenchmarkPlayer()
        {
            if (!IsRequested || Application.isEditor) return;
            Application.runInBackground = true;
            Application.targetFrameRate = -1;
            QualitySettings.vSyncCount = 0;
            Screen.SetResolution(TargetWidth, TargetHeight, FullScreenMode.Windowed, 60);
            var cameras = UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsSortMode.None);
            for (var i = 0; i < cameras.Length; i++) cameras[i].allowMSAA = true;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void InstallBenchmarkRecorder()
        {
            if (!IsRequested || Application.isEditor) return;
            var root = new GameObject("OmniTwin Sustained Benchmark");
            DontDestroyOnLoad(root);
            root.AddComponent<SustainedBenchmarkRecorder>();
        }

        private void Awake()
        {
            if (!IsRequested || Application.isEditor) { Destroy(gameObject); return; }
            if (!TryReadIntOption("--omnitwin-benchmark-seconds=", 45, 30, MaximumDurationSeconds, out _durationSeconds))
            { Fail("invalid_benchmark_duration"); return; }
            _reportPath = ReadPathOption("--omnitwin-benchmark-report=", Path.Combine(Application.persistentDataPath, "OmniTwin-Stage7-Benchmark.json"), ".json");
            _screenshotPath = ReadPathOption("--omnitwin-benchmark-screenshot=", Path.Combine(Application.persistentDataPath, "OmniTwin-Stage7-Screenshot.png"), ".png");
            if (_reportPath == null || _screenshotPath == null) { Fail("invalid_output_path"); return; }
            try
            {
                var reportDirectory = Path.GetDirectoryName(_reportPath);
                var screenshotDirectory = Path.GetDirectoryName(_screenshotPath);
                if (!string.IsNullOrEmpty(reportDirectory)) Directory.CreateDirectory(reportDirectory);
                if (!string.IsNullOrEmpty(screenshotDirectory)) Directory.CreateDirectory(screenshotDirectory);
            }
            catch (Exception error) { Fail("benchmark_output_directory_failed_" + error.GetType().Name); return; }

            Application.runInBackground = true;
            Application.targetFrameRate = -1;
            QualitySettings.vSyncCount = 0;
            Screen.SetResolution(TargetWidth, TargetHeight, FullScreenMode.Windowed, 60);
            var cameras = UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsSortMode.None);
            for (var i = 0; i < cameras.Length; i++) cameras[i].allowMSAA = true;
            _startupTime = Time.realtimeSinceStartupAsDouble;
            SetHudStatus("STANDALONE BENCHMARK · WAITING FOR CURRENT REPLAY AND PIP/MASK TEXTURES");
        }

        private void Update()
        {
            if (Volatile.Read(ref _finished) != 0) return;
            FindRuntimeSystems();
            if (_measurementStart <= 0)
            {
                EnableBenchmarkPresentation();
                if (!IsReady())
                {
                    if (Time.realtimeSinceStartupAsDouble - _startupTime > ReadinessTimeoutSeconds) Fail("benchmark_readiness_timeout");
                    return;
                }
                if (_warmupEnd <= 0)
                {
                    _warmupEnd = Time.realtimeSinceStartupAsDouble + WarmupSeconds;
                    SetHudStatus("STANDALONE BENCHMARK · WARMUP 5 s · 1920 × 1080 · PIP / MASKS / 5 AGENTS / 48-RAY LIDAR");
                    return;
                }
                if (Time.realtimeSinceStartupAsDouble < _warmupEnd)
                {
                    var remaining = Mathf.CeilToInt((float)(_warmupEnd - Time.realtimeSinceStartupAsDouble));
                    SetHudStatus("STANDALONE BENCHMARK · WARMUP " + remaining.ToString(CultureInfo.InvariantCulture) + " s · PIP / MASKS ACTIVE");
                    return;
                }
                BeginMeasurement();
            }

            var now = Time.realtimeSinceStartupAsDouble;
            var deltaMs = Time.unscaledDeltaTime * 1000f;
            if (_frameCount >= _frameTimes.Length) { Fail("benchmark_frame_capacity_exceeded"); return; }
            _frameTimes[_frameCount++] = deltaMs;
            _frameTimeSumMs += deltaMs;
            SamplePolicyEvaluation();
            if (now >= _lastMemorySample)
            {
                SampleMemory();
                _lastMemorySample = now + 0.5;
            }
            if (now - _measurementStart >= _durationSeconds)
            {
                if (_frameCount < 1800) { Fail("benchmark_below_1800_frames"); return; }
                StartCoroutine(CompleteBenchmark());
            }
            else if (_frameCount % 60 == 0)
                SetHudStatus("STANDALONE BENCHMARK · " + Mathf.FloorToInt((float)(now - _measurementStart)).ToString(CultureInfo.InvariantCulture) +
                    "/" + _durationSeconds.ToString(CultureInfo.InvariantCulture) + " s · " + _frameCount.ToString(CultureInfo.InvariantCulture) + " frames");
        }

        private void FindRuntimeSystems()
        {
            if (_executor == null) _executor = UnityEngine.Object.FindAnyObjectByType<PolicyExecutor>();
            if (_stream == null) _stream = UnityEngine.Object.FindAnyObjectByType<TwinWebSocketClient>();
            if (_media == null) _media = UnityEngine.Object.FindAnyObjectByType<MediaConsoleController>();
        }

        private void EnableBenchmarkPresentation()
        {
            if (_media != null)
            {
                _media.SetMasksEnabled(true);
                _media.SetPreviewEnabled(true);
            }
            if (_executor == null) return;
            var agents = _executor.Agents;
            for (var i = 0; i < agents.Count; i++)
            {
                var lidar = agents[i] == null ? null : agents[i].Lidar;
                if (lidar != null) lidar.SetVisualizationEnabled(true);
            }
        }

        private bool IsReady()
        {
            if (_executor == null || _stream == null || _media == null || Screen.width != TargetWidth || Screen.height != TargetHeight) return false;
            if (!_stream.TryGetCurrentView(out var view) || view.State.Message == null ||
                view.State.Message.SourceMode != PerceptionSourceMode.SyntheticTabletopReplay || view.State.Message.World.Obstacles.Count == 0) return false;
            if (_executor.Agents.Count != 5 || _media.PreviewTexture == null || _media.MaskTexture == null) return false;
            for (var i = 0; i < _executor.Agents.Count; i++)
            {
                var agent = _executor.Agents[i];
                if (agent == null || agent.Lidar == null || agent.Lidar.BeamCount != 48 || !agent.Lidar.VisualizationEnabled) return false;
                var lidar = agent.Lidar.ReadTelemetry();
                if (lidar == null || lidar.AgeMilliseconds > 250) return false;
            }
            return true;
        }

        private void BeginMeasurement()
        {
            _measurementStart = Time.realtimeSinceStartupAsDouble;
            _lastMemorySample = _measurementStart;
            _managedStart = _managedPeak = GC.GetTotalMemory(false);
            _unityAllocatedStart = _unityAllocatedPeak = Profiler.GetTotalAllocatedMemoryLong();
            _graphicsStart = _graphicsPeak = ToReportBytes(Profiler.GetAllocatedMemoryForGraphicsDriver());
            _textureStart = _texturePeak = ToReportBytes(Texture.currentTextureMemory);
            _gen0Start = GC.CollectionCount(0); _gen1Start = GC.CollectionCount(1); _gen2Start = GC.CollectionCount(2);
            SetHudStatus("STANDALONE BENCHMARK · CAPTURING " + _durationSeconds.ToString(CultureInfo.InvariantCulture) + " s");
        }

        private void SamplePolicyEvaluation()
        {
            var control = _executor == null ? null : _executor.Control;
            if (control == null || control.TelemetrySequenceNumber == _lastPolicySequence || _policySampleCount >= _policyEvaluationTimes.Length) return;
            _lastPolicySequence = control.TelemetrySequenceNumber;
            _policyEvaluationTimes[_policySampleCount++] = control.EvaluationDurationMilliseconds;
        }

        private void SampleMemory()
        {
            _managedEnd = GC.GetTotalMemory(false); if (_managedEnd > _managedPeak) _managedPeak = _managedEnd;
            _unityAllocatedEnd = Profiler.GetTotalAllocatedMemoryLong(); if (_unityAllocatedEnd > _unityAllocatedPeak) _unityAllocatedPeak = _unityAllocatedEnd;
            _graphicsEnd = ToReportBytes(Profiler.GetAllocatedMemoryForGraphicsDriver()); if (_graphicsEnd > _graphicsPeak) _graphicsPeak = _graphicsEnd;
            _textureEnd = ToReportBytes(Texture.currentTextureMemory); if (_textureEnd > _texturePeak) _texturePeak = _textureEnd;
            _gen0End = GC.CollectionCount(0); _gen1End = GC.CollectionCount(1); _gen2End = GC.CollectionCount(2);
        }

        private System.Collections.IEnumerator CompleteBenchmark()
        {
            if (Interlocked.Exchange(ref _finished, 1) != 0) yield break;
            SampleMemory();
            var measuredSeconds = Time.realtimeSinceStartupAsDouble - _measurementStart;
            var report = BuildReport(measuredSeconds, null);
            SetHudStatus("STANDALONE BENCHMARK · COMPLETE · " + report.AverageFps.ToString("F1", CultureInfo.InvariantCulture) + " FPS AVG · " + _frameCount + " frames");
            yield return new WaitForEndOfFrame();
            ScreenCapture.CaptureScreenshot(_screenshotPath);
            yield return new WaitForSecondsRealtime(2);
            report.ScreenshotWritten = File.Exists(_screenshotPath);
            report.Success = report.ScreenshotWritten;
            report.FailureCategory = report.Success ? string.Empty : "benchmark_output_write_failed";
            try
            {
                var directory = Path.GetDirectoryName(_reportPath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                File.WriteAllText(_reportPath, JsonUtility.ToJson(report, true));
                if (!File.Exists(_reportPath)) throw new IOException("Benchmark report was not created.");
            }
            catch (Exception error)
            {
                Debug.LogError("OmniTwin benchmark report write failed: " + error.GetType().Name);
                report.Success = false; report.FailureCategory = "benchmark_report_write_failed";
            }
            Debug.Log("OmniTwin standalone benchmark: " + JsonUtility.ToJson(report));
            Application.Quit(report.Success ? 0 : 2);
        }

        private BenchmarkReport BuildReport(double measuredSeconds, string failure)
        {
            Array.Sort(_frameTimes, 0, _frameCount);
            Array.Sort(_policyEvaluationTimes, 0, _policySampleCount);
            var meanFrameTimeMs = _frameCount == 0 ? 0 : _frameTimeSumMs / _frameCount;
            var p95 = Percentile(_frameTimes, _frameCount, 0.95);
            var p99 = Percentile(_frameTimes, _frameCount, 0.99);
            var slowCount = Math.Max(1, (int)Math.Ceiling(_frameCount * 0.01));
            var slowSum = 0.0;
            for (var i = _frameCount - slowCount; i < _frameCount; i++) if (i >= 0) slowSum += _frameTimes[i];
            var slowMean = slowCount == 0 ? 0 : slowSum / slowCount;
            var policyMean = 0.0;
            for (var i = 0; i < _policySampleCount; i++) policyMean += _policyEvaluationTimes[i];
            if (_policySampleCount > 0) policyMean /= _policySampleCount;
            var view = _stream != null ? _stream.View : default;
            var mode = view.State.Message == null ? "unavailable" : view.State.Message.SourceMode.ToString();
            return new BenchmarkReport
            {
                Success = string.IsNullOrEmpty(failure), FailureCategory = failure ?? string.Empty,
                CapturedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                UnityVersion = Application.unityVersion, BuildTarget = "StandaloneWindows64",
                Width = Screen.width, Height = Screen.height, WarmupSeconds = WarmupSeconds,
                RequestedDurationSeconds = _durationSeconds, MeasuredSeconds = measuredSeconds,
                FrameCount = _frameCount, AverageFps = measuredSeconds > 0 ? _frameCount / measuredSeconds : 0,
                MeanFrameTimeMs = meanFrameTimeMs, P95FrameTimeMs = p95, P99FrameTimeMs = p99,
                OnePercentLowFps = slowMean > 0 ? 1000.0 / slowMean : 0,
                ManagedHeapBytesStart = _managedStart, ManagedHeapBytesEnd = _managedEnd,
                ManagedHeapPeakBytes = _managedPeak, ManagedHeapDeltaBytes = _managedEnd - _managedStart,
                UnityAllocatedBytesStart = _unityAllocatedStart, UnityAllocatedBytesEnd = _unityAllocatedEnd,
                UnityAllocatedPeakBytes = _unityAllocatedPeak, UnityAllocatedDeltaBytes = _unityAllocatedEnd - _unityAllocatedStart,
                GraphicsDriverBytesStart = _graphicsStart, GraphicsDriverBytesEnd = _graphicsEnd,
                GraphicsDriverPeakBytes = _graphicsPeak, GraphicsDriverDeltaBytes = _graphicsEnd - _graphicsStart,
                TextureBytesStart = _textureStart, TextureBytesEnd = _textureEnd, TexturePeakBytes = _texturePeak,
                TextureDeltaBytes = _textureEnd - _textureStart,
                Gen0Collections = _gen0End - _gen0Start, Gen1Collections = _gen1End - _gen1Start, Gen2Collections = _gen2End - _gen2Start,
                SourceMode = mode, AgentCount = _executor == null ? 0 : _executor.Agents.Count,
                ObstacleTrackCount = view.State.Message == null ? 0 : view.State.Message.World.Obstacles.Count,
                LidarSensors = CountLidarSensors(), LidarBeamsPerAgent = 48, LidarRangeMeters = 6,
                CameraPreviewEnabled = _media != null && _media.PreviewEnabled,
                MaskOverlayEnabled = _media != null && _media.MasksEnabled && _media.MaskTexture != null,
                MaskStatus = _media == null ? "unavailable" : _media.MaskStatus,
                PolicyEvaluationBoundary = "Backend coordinator evaluation; this replay runs the compiled-in baseline, not a generated PolicyWorker policy.",
                PolicyEvaluationSamples = _policySampleCount, PolicyEvaluationMeanMs = policyMean,
                PolicyEvaluationP95Ms = Percentile(_policyEvaluationTimes, _policySampleCount, 0.95),
                PolicyEvaluationP99Ms = Percentile(_policyEvaluationTimes, _policySampleCount, 0.99),
                ReportPath = _reportPath, ScreenshotPath = _screenshotPath
            };
        }

        private int CountLidarSensors()
        {
            if (_executor == null) return 0;
            var count = 0;
            for (var i = 0; i < _executor.Agents.Count; i++) if (_executor.Agents[i] != null && _executor.Agents[i].Lidar != null) count++;
            return count;
        }

        private void Fail(string category)
        {
            if (Interlocked.Exchange(ref _finished, 1) != 0) return;
            var report = BuildReport(_measurementStart > 0 ? Time.realtimeSinceStartupAsDouble - _measurementStart : 0, category);
            try
            {
                var directory = Path.GetDirectoryName(_reportPath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                File.WriteAllText(_reportPath, JsonUtility.ToJson(report, true));
            }
            catch (Exception error) { Debug.LogError("OmniTwin benchmark failure report write failed: " + error.GetType().Name); }
            Debug.LogError("OmniTwin standalone benchmark failed: " + category);
            Application.Quit(2);
        }

        private void SetHudStatus(string status)
        {
            var document = UnityEngine.Object.FindAnyObjectByType<UIDocument>();
            var label = document == null ? null : document.rootVisualElement.Q<Label>("performanceMetrics");
            if (label != null) label.text = status;
        }

        private static double Percentile(float[] values, int count, double percentile)
        {
            if (count == 0) return 0;
            var index = Mathf.Clamp(Mathf.CeilToInt((float)(percentile * count)) - 1, 0, count - 1);
            return values[index];
        }

        private static double Percentile(double[] values, int count, double percentile)
        {
            if (count == 0) return 0;
            var index = Mathf.Clamp(Mathf.CeilToInt((float)(percentile * count)) - 1, 0, count - 1);
            return values[index];
        }

        private static long ToReportBytes(ulong bytes)
            => bytes > long.MaxValue ? long.MaxValue : (long)bytes;

        private static long ToReportBytes(long bytes) => Math.Max(0L, bytes);

        private static bool TryReadIntOption(string prefix, int fallback, int minimum, int maximum, out int value)
        {
            value = fallback;
            var arguments = Environment.GetCommandLineArgs();
            for (var i = 0; i < arguments.Length; i++)
                if (arguments[i].StartsWith(prefix, StringComparison.Ordinal))
                    return int.TryParse(arguments[i].Substring(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= minimum && value <= maximum;
            return true;
        }

        private static string ReadPathOption(string prefix, string fallback, string extension)
        {
            var arguments = Environment.GetCommandLineArgs();
            for (var i = 0; i < arguments.Length; i++)
            {
                if (!arguments[i].StartsWith(prefix, StringComparison.Ordinal)) continue;
                var value = arguments[i].Substring(prefix.Length).Trim('"');
                if (value.Length == 0 || value.Length > 2048 || !Path.IsPathFullyQualified(value) ||
                    !string.Equals(Path.GetExtension(value), extension, StringComparison.OrdinalIgnoreCase)) return null;
                return Path.GetFullPath(value);
            }
            return fallback;
        }

        [Serializable]
        private sealed class BenchmarkReport
        {
            public bool Success;
            public string FailureCategory;
            public string CapturedAtUtc;
            public string UnityVersion;
            public string BuildTarget;
            public int Width, Height, WarmupSeconds, RequestedDurationSeconds, FrameCount;
            public double MeasuredSeconds, AverageFps, MeanFrameTimeMs, P95FrameTimeMs, P99FrameTimeMs, OnePercentLowFps;
            public long ManagedHeapBytesStart, ManagedHeapBytesEnd, ManagedHeapPeakBytes, ManagedHeapDeltaBytes;
            public long UnityAllocatedBytesStart, UnityAllocatedBytesEnd, UnityAllocatedPeakBytes, UnityAllocatedDeltaBytes;
            public long GraphicsDriverBytesStart, GraphicsDriverBytesEnd, GraphicsDriverPeakBytes, GraphicsDriverDeltaBytes;
            public long TextureBytesStart, TextureBytesEnd, TexturePeakBytes, TextureDeltaBytes;
            public int Gen0Collections, Gen1Collections, Gen2Collections;
            public string SourceMode;
            public int AgentCount, ObstacleTrackCount, LidarSensors, LidarBeamsPerAgent;
            public float LidarRangeMeters;
            public bool CameraPreviewEnabled, MaskOverlayEnabled;
            public string MaskStatus, PolicyEvaluationBoundary;
            public int PolicyEvaluationSamples;
            public double PolicyEvaluationMeanMs, PolicyEvaluationP95Ms, PolicyEvaluationP99Ms;
            public string ReportPath, ScreenshotPath;
            public bool ScreenshotWritten;
        }
    }
}
