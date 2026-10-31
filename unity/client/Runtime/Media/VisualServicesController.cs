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
        private string _semanticStatus = "Waiting for a confirmed object", _sceneryStatus = "Default tactical surface";
        private byte[] _pendingTexture;
        private bool _restoreRequested;
        private long _generationVersion;
        private Material _defaultMaterial, _generatedMaterial;
        private Texture2D _texture;
        /// <summary>Gets immutable session-scoped remembered tags.</summary>
        public SemanticSceneState Semantics { get { lock (_gate) return _semantics; } }
        /// <summary>Gets safe classifier/operator status.</summary>
        public string SemanticStatus { get { lock (_gate) return _semanticStatus; } }
        /// <summary>Gets safe generation status.</summary>
        public string SceneryStatus { get { lock (_gate) return _sceneryStatus; } }
        /// <summary>Gets whether an explicit generation is pending.</summary>
        public bool IsGenerating => _generation != null && !_generation.IsCompleted;
        /// <summary>Gets whether an operator edit is pending.</summary>
        public bool IsEditing => _edit != null && !_edit.IsCompleted;
        /// <summary>Gets whether the authored default surface is active.</summary>
        public bool IsDefaultSurface => _generatedMaterial == null;

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
                await Task.Delay(1000, token).ConfigureAwait(false);
            }
        }
        /// <summary>Submits a bounded manual simulation tag; null clears the override and reveals remembered model output.</summary>
        public void ApplyOverride(int trackId, ObjectSemantics classification)
        {
            var state = Semantics;
            if (!isActiveAndEnabled || IsEditing || state == null || !state.SessionId.HasValue) return;
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
        }
        /// <summary>Starts one explicit paid API request; a late reply cannot replace a reset or newer surface.</summary>
        public void GenerateSurface(string prompt)
        {
            if (!isActiveAndEnabled || IsGenerating) return;
            if (string.IsNullOrWhiteSpace(prompt) || prompt.Length > 1000) throw new ArgumentException("Enter a surface prompt of 1–1000 characters.");
            foreach (var character in prompt) if (char.IsControl(character)) throw new ArgumentException("Use a single-line surface prompt.");
            _generationStop?.Dispose(); _generationStop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token); _generationStop.CancelAfter(TimeSpan.FromSeconds(160));
            var token = _generationStop.Token; var client = _client; long version;
            lock (_gate) { version = ++_generationVersion; _pendingTexture = null; _restoreRequested = false; _sceneryStatus = "Generating with Muse Image · default remains available"; }
            _generation = Task.Run(async () =>
            {
                try
                {
                    var id = Guid.NewGuid(); var state = await client.GenerateAsync(_origin, id, prompt, token).ConfigureAwait(false);
                    while (state.Status == "generating") { await Task.Delay(750, token).ConfigureAwait(false); state = await client.ReadSceneryAsync(_origin, id, token).ConfigureAwait(false); }
                    if (state.Status != "ready") throw new MediaTransportException(state.FailureCategory ?? "scenery_generation_failed");
                    var image = await client.ReadSceneryImageAsync(_origin, id, token).ConfigureAwait(false);
                    lock (_gate) { token.ThrowIfCancellationRequested(); if (version != _generationVersion) return; _pendingTexture = image; _sceneryStatus = "Generated texture validated · applying"; }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { Fail(version, "Scenery wait cancelled / timed out · default restored"); }
                catch (Exception error) when (error is MediaTransportException || error is WorldStreamProtocolException || error is InvalidDataException)
                { Fail(version, error is MediaTransportException media ? media.Category + " · default restored" : "invalid_generated_surface · default restored"); }
            });
        }
        private void Fail(long version, string status) { lock (_gate) { if (version != _generationVersion) return; _pendingTexture = null; _restoreRequested = true; _sceneryStatus = status; } }
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
            { RestoreResources(); lock (_gate) _sceneryStatus = "surface_decode_failed · default restored"; }
        }
        /// <summary>Immediately restores the authored URP material and cancels local waiting. An accepted backend job may finish.</summary>
        public void RestoreDefault()
        {
            _generationStop?.Cancel();
            lock (_gate) { _generationVersion++; _pendingTexture = null; _restoreRequested = false; _sceneryStatus = "Default tactical surface"; }
            RestoreResources();
        }
        private void RestoreResources()
        {
            if (workspaceSurface != null && _defaultMaterial != null) workspaceSurface.sharedMaterial = _defaultMaterial;
            if (_generatedMaterial != null) Destroy(_generatedMaterial); _generatedMaterial = null;
            if (_texture != null) Destroy(_texture); _texture = null;
        }
        private void OnDisable()
        {
            RestoreDefault(); _stop?.Cancel();
            var client = _client; var stop = _stop; var generationStop = _generationStop;
            _ = Task.WhenAll(_poll ?? Task.CompletedTask, _generation ?? Task.CompletedTask, _edit ?? Task.CompletedTask)
                .ContinueWith(done => { var error = done.Exception; client?.Dispose(); generationStop?.Dispose(); stop?.Dispose(); }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            _client = null; _stop = null; _generationStop = null;
        }
    }
}
