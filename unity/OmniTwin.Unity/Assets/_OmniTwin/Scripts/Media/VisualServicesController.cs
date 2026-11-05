using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using OmniTwin.Contracts;
using OmniTwin.Perception.Imaging;
using OmniTwin.Transport;
using UnityEngine;

namespace OmniTwin.Simulation.Media
{
    /// <summary>Asynchronous semantic memory/operator edits and explicit surface generation; Unity objects are touched only on the main thread.</summary>
    [DisallowMultipleComponent]
    public sealed class VisualServicesController : MonoBehaviour
    {
        [SerializeField] private string backendOrigin = "http://127.0.0.1:5108";
        [SerializeField] private Renderer workspaceSurface;
        private readonly object _gate = new object();
        private VisualServicesClient _client;
        private CancellationTokenSource _stop, _generationStop;
        private Task _poll, _generation, _edit;
        private Uri _origin;
        private SemanticSceneState _semantics;
        private BackendModelReadiness _readiness;
        private string _readinessStatus = "Backend readiness not checked";
        private string _semanticStatus = "Waiting for a confirmed object", _sceneryStatus = "Default tactical surface";
        private byte[] _pendingTexture;
        private bool _restoreRequested;
        private readonly CommandEpoch _generationEpoch = new CommandEpoch();
        private Material _defaultMaterial, _generatedMaterial, _localPresetMaterial;
        private Texture2D _texture;
        private string _localPresetName;
        /// <summary>Gets immutable session-scoped remembered tags.</summary>
        public SemanticSceneState Semantics { get { lock (_gate) return _semantics; } }
        /// <summary>Gets the latest secret-free model credential and sandbox prerequisite status.</summary>
        public BackendModelReadiness Readiness { get { lock (_gate) return _readiness; } }
        /// <summary>Gets a bounded local readiness polling status.</summary>
        public string ReadinessStatus { get { lock (_gate) return _readinessStatus; } }
        /// <summary>Gets safe classifier/operator status.</summary>
        public string SemanticStatus { get { lock (_gate) return _semanticStatus; } }
        /// <summary>Gets safe generation status.</summary>
        public string SceneryStatus { get { lock (_gate) return _sceneryStatus; } }
        /// <summary>Gets whether an explicit generation is pending.</summary>
        public bool IsGenerating => _generation != null && !_generation.IsCompleted;
        /// <summary>Gets whether an operator edit is pending.</summary>
        public bool IsEditing => _edit != null && !_edit.IsCompleted;
        /// <summary>Gets whether the authored default surface is active.</summary>
        public bool IsDefaultSurface => _generatedMaterial == null && _localPresetMaterial == null;

        /// <summary>Applies one explicit local material preset immediately; it does not call Muse Image.</summary>
        public void ApplyLocalSurfacePreset(string presetName)
        {
            if (!isActiveAndEnabled || workspaceSurface == null || _defaultMaterial == null)
                throw new InvalidOperationException("The workspace surface is not initialized.");
            var preset = (presetName ?? string.Empty).Trim().ToUpperInvariant();
            Color tint; float metallic; float smoothness;
            switch (preset)
            {
                case "INDUSTRIAL STEEL": tint = new Color(0.54f, 0.66f, 0.72f, 1f); metallic = 0.72f; smoothness = 0.58f; break;
                case "MARS CRATER": tint = new Color(0.78f, 0.30f, 0.13f, 1f); metallic = 0.12f; smoothness = 0.24f; break;
                case "NEON BLUEPRINT": tint = new Color(0.08f, 0.35f, 0.92f, 1f); metallic = 0.38f; smoothness = 0.75f; break;
                default: throw new ArgumentException("Choose INDUSTRIAL STEEL, MARS CRATER, or NEON BLUEPRINT.", nameof(presetName));
            }
            if (_generation != null && !_generation.IsCompleted) _generationStop?.Cancel();
            _generationEpoch.Advance();
            RestoreResources();
            _localPresetMaterial = new Material(_defaultMaterial) { name = "OmniTwin Local " + preset + " Surface" };
            SetSurfaceTint(_localPresetMaterial, tint);
            if (_localPresetMaterial.HasProperty("_Metallic")) _localPresetMaterial.SetFloat("_Metallic", metallic);
            if (_localPresetMaterial.HasProperty("_Smoothness")) _localPresetMaterial.SetFloat("_Smoothness", smoothness);
            workspaceSurface.sharedMaterial = _localPresetMaterial;
            lock (_gate) { _localPresetName = preset; _pendingTexture = null; _restoreRequested = false; _sceneryStatus = "LOCAL STYLE ACTIVE · " + preset + " · Muse Image not called"; }
        }

        private void OnEnable()
        {
            _origin = new Uri(backendOrigin); MediaOrigin();
            if (workspaceSurface == null || workspaceSurface.sharedMaterial == null) throw new InvalidOperationException("Assign the authored URP workspace renderer.");
            _defaultMaterial = workspaceSurface.sharedMaterial; _client = new VisualServicesClient(); _stop = new CancellationTokenSource();
            var client = _client; var token = _stop.Token; _poll = Task.Run(() => Poll(client, token));
        }
        private void MediaOrigin()
        {
            if (_origin.Scheme != "http" || !(_origin.Host == "127.0.0.1" || _origin.Host == "localhost") || _origin.AbsolutePath != "/" || _origin.UserInfo.Length != 0 || _origin.Query.Length != 0 || _origin.Fragment.Length != 0)
                throw new InvalidOperationException("Use the local backend origin.");
        }
        private async Task Poll(VisualServicesClient client, CancellationToken token)
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var state = await client.ReadSemanticsAsync(_origin, token).ConfigureAwait(false);
                    lock (_gate) { token.ThrowIfCancellationRequested(); _semantics = state; _semanticStatus = state.SessionId.HasValue ? state.Objects.Count + " remembered tracks · Muse Spark / operator" : "No active perception session"; }
                }
                catch (MediaTransportException error) { lock (_gate) _semanticStatus = error.Category; }
                catch (WorldStreamProtocolException) { lock (_gate) _semanticStatus = "invalid_semantic_state"; }
                try
                {
                    var readiness = await client.ReadBackendReadinessAsync(_origin, token).ConfigureAwait(false);
                    lock (_gate) { token.ThrowIfCancellationRequested(); _readiness = readiness; _readinessStatus = "Backend configuration observed · endpoint latency requires live-preflight"; }
                }
                catch (MediaTransportException error) { lock (_gate) _readinessStatus = error.Category; }
                catch (WorldStreamProtocolException) { lock (_gate) _readinessStatus = "invalid_backend_readiness"; }
                await Task.Delay(1000, token).ConfigureAwait(false);
            }
        }
        /// <summary>Submits a bounded manual simulation tag; null clears the override and reveals remembered model output.</summary>
        public void ApplyOverride(int trackId, ObjectSemantics classification)
        {
            TryApplyOverride(trackId, classification);
        }

        /// <summary>Queues a semantic edit only when the live backend session contains the selected track.</summary>
        public bool TryApplyOverride(int trackId, ObjectSemantics classification)
        {
            var state = Semantics;
            if (!isActiveAndEnabled || IsEditing || state == null || !state.SessionId.HasValue) return false;
            var knownTrack = false;
            foreach (var item in state.Objects) if (item.TrackId == trackId) { knownTrack = true; break; }
            if (!knownTrack) return false;
            var client = _client; var token = _stop.Token; var session = state.SessionId.Value;
            _edit = Task.Run(async () =>
            {
                try
                {
                    var result = await client.OverrideAsync(_origin, session, trackId, classification, token).ConfigureAwait(false);
                    lock (_gate) { token.ThrowIfCancellationRequested(); _semantics = result; _semanticStatus = classification == null ? "Operator override cleared" : "Operator override saved"; }
                }
                catch (MediaTransportException error) { lock (_gate) _semanticStatus = error.Category; }
                catch (WorldStreamProtocolException) { lock (_gate) _semanticStatus = "invalid_semantic_edit_reply"; }
            });
            return true;
        }
        /// <summary>Starts one explicit paid API request; a late reply cannot replace a reset or newer surface.</summary>
        public void GenerateSurface(string prompt)
        {
            if (!isActiveAndEnabled) return;
            if (string.IsNullOrWhiteSpace(prompt) || prompt.Length > 1000) throw new ArgumentException("Enter a surface prompt of 1–1000 characters.");
            foreach (var character in prompt) if (char.IsControl(character)) throw new ArgumentException("Use a single-line surface prompt.");
            var previous = _generation; var previousStop = _generationStop;
            if (previous != null && !previous.IsCompleted) previousStop?.Cancel();
            var generationStop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token); generationStop.CancelAfter(TimeSpan.FromSeconds(160));
            _generationStop = generationStop;
            var token = generationStop.Token; var client = _client; var version = _generationEpoch.Advance();
            lock (_gate) { _pendingTexture = null; _restoreRequested = false; _sceneryStatus = "Muse Image request sent · current surface remains visible"; }
            _generation = Task.Run(async () =>
            {
                try
                {
                    if (previous != null) await previous.ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    var id = Guid.NewGuid(); var state = await client.GenerateAsync(_origin, id, prompt, token).ConfigureAwait(false);
                    while (state.Status == "generating") { await Task.Delay(750, token).ConfigureAwait(false); state = await client.ReadSceneryAsync(_origin, id, token).ConfigureAwait(false); }
                    if (state.Status != "ready") throw new MediaTransportException(state.FailureCategory ?? "scenery_generation_failed");
                    var image = await client.ReadSceneryImageAsync(_origin, id, token).ConfigureAwait(false);
                    lock (_gate) { token.ThrowIfCancellationRequested(); if (!_generationEpoch.IsCurrent(version)) return; _pendingTexture = image; _sceneryStatus = "Generated texture validated · applying"; }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { Fail(version, "scenery_cancelled_or_timed_out"); }
                catch (Exception error) when (error is MediaTransportException || error is WorldStreamProtocolException || error is InvalidDataException)
                { Fail(version, error is MediaTransportException media ? media.Category : "invalid_generated_surface"); }
                catch (Exception) { Fail(version, "scenery_worker_failed"); }
                finally { generationStop.Dispose(); }
            });
        }
        private void Fail(long version, string status)
        {
            lock (_gate)
            {
                if (!_generationEpoch.IsCurrent(version)) return;
                _pendingTexture = null; _restoreRequested = false;
                _sceneryStatus = "Muse Image failed · " + status + (_localPresetName == null ? " · authored surface remains" : " · LOCAL " + _localPresetName + " remains");
            }
        }
        private void Update()
        {
            byte[] image; bool restore;
            lock (_gate) { image = _pendingTexture; _pendingTexture = null; restore = _restoreRequested; _restoreRequested = false; }
            if (restore) RestoreResources();
            if (image == null) return;
            try
            {
                var info = RasterContainer.Inspect(image, 1024, 524288);
                var texture = new Texture2D(2, 2, TextureFormat.RGB24, false) { name = "Muse Image tactical surface", wrapMode = TextureWrapMode.Repeat };
                if (!ImageConversion.LoadImage(texture, image, true) || texture.width != info.Width || texture.height != info.Height) { Destroy(texture); throw new InvalidDataException(); }
                RestoreResources(); _texture = texture; _generatedMaterial = new Material(_defaultMaterial) { name = "Muse Image surface instance" };
                _generatedMaterial.SetTexture("_BaseMap", _texture); _generatedMaterial.SetColor("_BaseColor", Color.white); workspaceSurface.sharedMaterial = _generatedMaterial;
                lock (_gate) _sceneryStatus = "Muse Image surface active · tactical grid preserved";
            }
            catch (Exception error) when (error is InvalidDataException || error is UnityException || error is ArgumentException)
            { lock (_gate) _sceneryStatus = "surface_decode_failed · current local/authored surface retained"; }
        }
        /// <summary>Immediately restores the authored URP material and cancels local waiting. An accepted backend job may finish.</summary>
        public void RestoreDefault()
        {
            if (_generation != null && !_generation.IsCompleted) _generationStop?.Cancel();
            _generationEpoch.Advance();
            lock (_gate) { _pendingTexture = null; _restoreRequested = false; _sceneryStatus = "Default tactical surface"; }
            RestoreResources();
        }
        private void RestoreResources()
        {
            if (workspaceSurface != null && _defaultMaterial != null) workspaceSurface.sharedMaterial = _defaultMaterial;
            DestroySurfaceInstances(); lock (_gate) _localPresetName = null;
        }
        private void DestroySurfaceInstances()
        {
            if (_generatedMaterial != null) Destroy(_generatedMaterial); _generatedMaterial = null;
            if (_localPresetMaterial != null) Destroy(_localPresetMaterial); _localPresetMaterial = null;
            if (_texture != null) Destroy(_texture); _texture = null;
        }
        private static void SetSurfaceTint(Material material, Color tint)
        {
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", tint);
            else if (material.HasProperty("_Color")) material.SetColor("_Color", tint);
        }
        private void OnDisable()
        {
            RestoreDefault(); _stop?.Cancel();
            var client = _client; var stop = _stop;
            _ = Task.WhenAll(_poll ?? Task.CompletedTask, _generation ?? Task.CompletedTask, _edit ?? Task.CompletedTask)
                .ContinueWith(done => { var error = done.Exception; client?.Dispose(); stop?.Dispose(); }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            _client = null; _stop = null; _generationStop = null;
        }
    }
}
