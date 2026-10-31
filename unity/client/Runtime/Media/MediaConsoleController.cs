using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using OmniTwin.Contracts;
using OmniTwin.Perception.Audio;
using OmniTwin.Transport;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace OmniTwin.Simulation.Media
{
    /// <summary>Owns opt-in camera preview and operator-initiated short voice recordings, without model I/O on Unity threads.</summary>
    [DisallowMultipleComponent]
    public sealed class MediaConsoleController : MonoBehaviour
    {
        [SerializeField] private string backendOrigin = "http://127.0.0.1:5108";
        private readonly object _gate = new object();
        private MediaClient _client; private CancellationTokenSource _stop, _voiceStop; private Task _previewTask, _voiceTask; private Uri _origin;
        private VisualServicesClient _visualClient;
        private AnnotatedCameraPreview _pending, _shown; private long _received; private int _previewEnabled, _masksEnabled = 1;
        private string _previewStatus = "Camera preview off", _voiceStatus = "Ready to record · 5 s maximum";
        private VoiceTranscription _transcript;
        private Texture2D _texture, _maskTexture; private UIDocument _hud;
        private AudioClip _recording; private string _device; private float _recordedAt; private bool _spaceRecording;
        /// <summary>Raised on the Unity thread after an operator successfully starts capture.</summary>
        public event Action RecordingStarted;

        /// <summary>Gets whether camera download is explicitly enabled.</summary>
        public bool PreviewEnabled => Volatile.Read(ref _previewEnabled) != 0;
        /// <summary>Gets a texture only while its actual source capture remains fresh.</summary>
        public Texture2D PreviewTexture => _shown != null && PreviewEnabled && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _shown.Image.CapturedAtUnixMs < MediaProtocolLimits.PreviewFreshnessMilliseconds ? _texture : null;
        /// <summary>Gets an aligned transparent mask layer only while the paired camera capture is current.</summary>
        public Texture2D MaskTexture => MasksEnabled && PreviewTexture != null ? _maskTexture : null;
        /// <summary>Gets whether operator mask rendering is enabled.</summary>
        public bool MasksEnabled => Volatile.Read(ref _masksEnabled) != 0;
        /// <summary>Toggles mask rendering independently of the camera and navigation.</summary>
        public void SetMasksEnabled(bool value) => Volatile.Write(ref _masksEnabled, value ? 1 : 0);
        /// <summary>Gets visible mask provenance; a dashed contour always denotes a translated earlier SAM mask.</summary>
        public string MaskStatus => !MasksEnabled ? "Mask layer off" : PreviewTexture == null ? "Masks unavailable · no current image" :
            _shown.Masks.Count + " masks · solid: same-frame SAM · dashed: local estimate / SAM age";
        /// <summary>Gets bounded preview status; an image never establishes SAM validity.</summary>
        public string PreviewStatus { get { lock (_gate) return _previewStatus; } }
        /// <summary>Gets whether the microphone is currently owned by this controller.</summary>
        public bool IsRecording => _recording != null;
        /// <summary>Gets whether encoding or transcription is active.</summary>
        public bool IsTranscribing => _voiceTask != null && !_voiceTask.IsCompleted;
        /// <summary>Gets capture/API status without transcript or provider-body logging.</summary>
        public string VoiceStatus { get { lock (_gate) return _voiceStatus; } }
        /// <summary>Gets the latest immutable draft; it never automatically submits a mission.</summary>
        public VoiceTranscription Transcript { get { lock (_gate) return _transcript; } }

        private void OnEnable()
        {
            if (!Uri.TryCreate(backendOrigin, UriKind.Absolute, out _origin) || _origin.Scheme != "http" || !(_origin.Host == "127.0.0.1" || _origin.Host == "localhost") || _origin.AbsolutePath != "/" || _origin.Query.Length != 0 || _origin.Fragment.Length != 0 || _origin.UserInfo.Length != 0)
                throw new InvalidOperationException("Assign the local backend HTTP origin.");
            _hud = UnityEngine.Object.FindAnyObjectByType<UIDocument>(); _client = new MediaClient(); _visualClient = new VisualServicesClient(); _stop = new CancellationTokenSource();
            var client = _visualClient; var stop = _stop; _previewTask = Task.Run(() => PreviewLoop(client, stop.Token));
        }
        /// <summary>Enables/disables preview without opening another device or changing perception trust.</summary>
        public void SetPreviewEnabled(bool value)
        {
            Volatile.Write(ref _previewEnabled, value ? 1 : 0);
            if (!value) { lock (_gate) { _pending = null; _previewStatus = "Camera preview off"; } _shown = null; }
        }
        private async Task PreviewLoop(VisualServicesClient client, CancellationToken token)
        {
            Guid session = Guid.Empty; long sequence = -1, revision = -1;
            while (true)
            {
                token.ThrowIfCancellationRequested(); var delay = 200;
                if (PreviewEnabled)
                {
                    try
                    {
                        var paired = await client.ReadPreviewAsync(_origin, token).ConfigureAwait(false); var frame = paired.Image;
                        token.ThrowIfCancellationRequested();
                        if (PreviewEnabled && (frame.SessionId != session || frame.SequenceNumber > sequence || (frame.SequenceNumber == sequence && paired.OverlayRevision > revision)))
                        {
                            var newCapture = frame.SessionId != session || frame.SequenceNumber != sequence;
                            session = frame.SessionId; sequence = frame.SequenceNumber; revision = paired.OverlayRevision;
                            lock (_gate) { token.ThrowIfCancellationRequested(); _pending = paired; if (newCapture) _received = Stopwatch.GetTimestamp(); _previewStatus = "Camera · " + frame.Width + " × " + frame.Height + " · capture " + frame.SequenceNumber; }
                        }
                    }
                    catch (MediaTransportException error) { lock (_gate) { _pending = null; _previewStatus = error.Category; } delay = 1000; }
                    catch (Exception error) when (error is WorldStreamProtocolException || error is InvalidDataException) { lock (_gate) { _pending = null; _previewStatus = "invalid_camera_mask_pair"; } delay = 1000; }
                }
                await Task.Delay(delay, token).ConfigureAwait(false);
            }
        }
        /// <summary>Toggles a recording. Called only by an explicit operator button, never automatically at startup.</summary>
        public void ToggleRecording() { if (IsRecording) FinishRecording(); else StartRecording(false); }
        private void StartRecording(bool heldSpace)
        {
            if (!isActiveAndEnabled || !Application.isFocused || IsRecording || IsTranscribing) return;
            try
            {
                var devices = Microphone.devices;
                if (devices.Length == 0) { SetVoice("microphone_unavailable · type the mission instead"); return; }
                _device = devices[0]; Microphone.GetDeviceCaps(_device, out var minimum, out var maximum);
                var rate = minimum == 0 && maximum == 0 ? PcmWaveCodec.SampleRate : Mathf.Clamp(PcmWaveCodec.SampleRate, minimum, maximum);
                if (rate < 8000 || rate > 96000) { SetVoice("microphone_format_unsupported · type the mission instead"); return; }
                _recording = Microphone.Start(_device, false, MediaProtocolLimits.AudioMaximumSeconds, rate);
                if (_recording == null) { SetVoice("microphone_start_failed · type the mission instead"); return; }
                _recordedAt = Time.realtimeSinceStartup; _spaceRecording = heldSpace; SetVoice("Recording · release Space or press Stop · maximum 5 s");
                RecordingStarted?.Invoke();
            }
            catch (Exception error) when (error is ArgumentException || error is InvalidOperationException || error is UnityException)
            { CancelRecording(); SetVoice("microphone_start_failed · type the mission instead"); }
        }
        private void Update()
        {
            var keyboard = Keyboard.current;
            if (Application.isFocused && keyboard != null && keyboard.spaceKey.wasPressedThisFrame && !HasUiFocus()) StartRecording(true);
            if (_recording != null)
            {
                try
                {
                    var elapsed = Time.realtimeSinceStartup - _recordedAt;
                    if ((_spaceRecording && keyboard != null && keyboard.spaceKey.wasReleasedThisFrame) || elapsed >= MediaProtocolLimits.AudioMaximumSeconds) FinishRecording();
                    else if (elapsed >= 1 && Microphone.GetPosition(_device) == 0) { CancelRecording(); SetVoice("microphone_no_samples · type the mission instead"); }
                }
                catch (Exception error) when (error is ArgumentException || error is InvalidOperationException || error is UnityException)
                { CancelRecording(); SetVoice("microphone_device_failed · type the mission instead"); }
            }
            AnnotatedCameraPreview pending; long received; lock (_gate) { pending = _pending; received = _received; }
            if (!PreviewEnabled || pending == null || DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - pending.Image.CapturedAtUnixMs >= MediaProtocolLimits.PreviewFreshnessMilliseconds ||
                (Stopwatch.GetTimestamp() - received) * 1000.0 / Stopwatch.Frequency >= MediaProtocolLimits.PreviewFreshnessMilliseconds)
            {
                _shown = null;
                if (PreviewEnabled && pending != null) { lock (_gate) _previewStatus = "camera_preview_stale"; }
            }
            else if (!ReferenceEquals(_shown, pending))
            {
                if (_texture == null) { _texture = new Texture2D(2, 2, TextureFormat.RGB24, false); _texture.name = "OmniTwin live camera preview"; }
                if (_maskTexture == null) { _maskTexture = new Texture2D(2, 2, TextureFormat.RGBA32, false); _maskTexture.name = "OmniTwin synchronized SAM mask layer"; }
                if (ImageConversion.LoadImage(_texture, pending.Image.CopyJpeg(), true) && _texture.width == pending.Image.Width && _texture.height == pending.Image.Height &&
                    ImageConversion.LoadImage(_maskTexture, pending.CopyMaskLayer(), true) && _maskTexture.width == pending.Image.Width && _maskTexture.height == pending.Image.Height) _shown = pending;
                else { _shown = null; lock (_gate) { _pending = null; _previewStatus = "camera_preview_decode_failed"; } }
            }
            if (_previewTask != null && _previewTask.IsFaulted) { _shown = null; lock (_gate) _previewStatus = "camera_preview_worker_failed"; }
            if (_voiceTask != null && _voiceTask.IsFaulted) SetVoice("voice_worker_failed · type the mission instead");
        }
        private bool HasUiFocus() => _hud != null && _hud.rootVisualElement?.focusController?.focusedElement != null;
        private void FinishRecording()
        {
            var clip = _recording; if (clip == null) return;
            try
            {
                var frames = Microphone.GetPosition(_device);
                if (frames == 0 && Time.realtimeSinceStartup - _recordedAt >= MediaProtocolLimits.AudioMaximumSeconds) frames = clip.samples;
                if (!EndMicrophone()) return;
                _recording = null; _spaceRecording = false;
                frames = Math.Min(frames, Math.Min(clip.samples, clip.frequency * MediaProtocolLimits.AudioMaximumSeconds));
                if (clip.channels < 1 || clip.channels > 2 || clip.frequency < 8000 || clip.frequency > 96000) { SetVoice("microphone_format_unsupported · type the mission instead"); return; }
                if ((long)frames * 5 < clip.frequency) { SetVoice("recording_too_short · hold for at least 0.2 s"); return; }
                var samples = new float[frames * clip.channels];
                if (!clip.GetData(samples, 0)) { Array.Clear(samples, 0, samples.Length); SetVoice("microphone_read_failed · type the mission instead"); return; }
                var rate = clip.frequency; var channels = clip.channels; var client = _client;
                _voiceStop?.Dispose(); _voiceStop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token); var token = _voiceStop.Token;
                SetVoice("Transcribing with Muse Voice · text input remains available");
                _voiceTask = Task.Run(async () =>
                {
                    byte[] wav = null;
                    try
                    {
                        wav = PcmWaveCodec.Encode(samples, rate, channels); token.ThrowIfCancellationRequested();
                        var result = await client.TranscribeAsync(_origin, Guid.NewGuid(), wav, token).ConfigureAwait(false);
                        lock (_gate) { token.ThrowIfCancellationRequested(); _transcript = result; _voiceStatus = result.Transcript.Length == 0 ? "No speech recognized · type or record again" : "Transcript ready · review the mission before synthesis"; }
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { SetVoice("Voice request cancelled · text input remains available"); }
                    catch (MediaTransportException error) { SetVoice(error.Category + " · type the mission instead"); }
                    catch (Exception error) when (error is ArgumentException || error is InvalidDataException) { SetVoice("invalid_recorded_audio · type the mission instead"); }
                    finally { Array.Clear(samples, 0, samples.Length); if (wav != null) Array.Clear(wav, 0, wav.Length); }
                });
            }
            catch (Exception error) when (error is ArgumentException || error is InvalidOperationException || error is UnityException)
            { SetVoice("microphone_read_failed · type the mission instead"); }
            finally { if (_recording != null) { EndMicrophone(); _recording = null; } Destroy(clip); }
        }
        /// <summary>Cancels local recording/upload waiting; an already accepted server transcription may still finish.</summary>
        public void CancelVoice() { CancelRecording(); _voiceStop?.Cancel(); SetVoice("Voice cancelled · text input remains available"); }
        private void SetVoice(string status) { lock (_gate) _voiceStatus = status; }
        private bool EndMicrophone()
        {
            try { Microphone.End(_device); return true; }
            catch (Exception error) when (error is ArgumentException || error is InvalidOperationException || error is UnityException)
            { SetVoice("microphone_stop_failed · text input remains available"); return false; }
        }
        private void CancelRecording() { if (_recording == null) return; EndMicrophone(); Destroy(_recording); _recording = null; _spaceRecording = false; }
        private void OnApplicationFocus(bool focused) { if (!focused && IsRecording) { CancelRecording(); SetVoice("Recording cancelled on focus loss"); } }
        private void OnApplicationPause(bool paused) { if (paused && IsRecording) { CancelRecording(); SetVoice("Recording cancelled while paused"); } }
        private void OnDisable()
        {
            CancelRecording(); _voiceStop?.Cancel(); _stop?.Cancel(); SetPreviewEnabled(false);
            if (_texture != null) Destroy(_texture); _texture = null;
            if (_maskTexture != null) Destroy(_maskTexture); _maskTexture = null;
            var stop = _stop; var voiceStop = _voiceStop; var client = _client; var visualClient = _visualClient;
            var tasks = _voiceTask == null ? new[] { _previewTask ?? Task.CompletedTask } : new[] { _previewTask ?? Task.CompletedTask, _voiceTask };
            _ = Task.WhenAll(tasks).ContinueWith(finished => { var failure = finished.Exception; client?.Dispose(); visualClient?.Dispose(); voiceStop?.Dispose(); stop?.Dispose(); }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            _stop = null; _voiceStop = null; _client = null; _visualClient = null;
        }
    }
}
