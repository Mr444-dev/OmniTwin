using System;
using System.Globalization;
using OmniTwin.Simulation.Network;
using OmniTwin.Transport;
using UnityEngine;
using UnityEngine.UIElements;
using System.Text;
using System.Collections.Generic;
using OmniTwin.Contracts;
using OmniTwin.Simulation.Runtime;
using OmniTwin.Simulation.Media;
using OmniTwin.Simulation.CameraControl;
using OmniTwin.Simulation.Agents;
using OmniTwin.Simulation.Diagnostics;

namespace OmniTwin.Simulation.UI
{
    /// <summary>Shows actual stream state and observation freshness; absent observations are never shown as a clear scene.</summary>
    [RequireComponent(typeof(UIDocument)), DefaultExecutionOrder(100)]
    public sealed class TacticalHudController : MonoBehaviour
    {
        [SerializeField] private TwinWebSocketClient stream = null;
        [SerializeField] private PolicyExecutor executor = null;
        [SerializeField] private MediaConsoleController media = null;
        [SerializeField] private VisualServicesController visuals = null;
        private VisualElement _screen;
        private Label _badge, _connection, _tracks, _hazards, _age, _publication, _calibration, _notice;
        private Label _sourceMode, _performance;
        private Label _modelSam, _modelSparkOpen, _modelSemantic, _modelVoice, _modelImage, _modelSandbox;
        private VisualElement _modelMatrix, _brainView, _semanticsView, _sceneryView;
        private Button _brainTab, _semanticsTab, _sceneryTab;
        private Button _reconnect;
        private IVisualElementScheduledItem _refresh;
        private Label _policy, _telemetry, _reason, _brain, _model, _compiler;
        private Button _patrol, _cordon, _search, _hold, _generate;
        private TextField _instruction;
        private SwarmMissionKind _selectedMission = SwarmMissionKind.PatrolPerimeter;
        private string _feedback;
        private float _feedbackUntil;
        private Button _recordVoice, _cancelVoice, _useTranscript, _previewToggle;
        private Label _voiceStatus, _voiceDraft, _previewStatus;
        private Image _previewImage;
        private VoiceTranscription _lastTranscript;
        private string _instructionBeforeVoice;
        private Button _maskToggle, _lidarToggle, _loadSemantic, _applySemantic, _clearSemantic, _generateScenery, _resetScenery;
        private Label _maskStatus, _semanticStatus, _semanticSelected, _sceneryStatus;
        private Image _maskImage;
        private VisualElement _previewFrame;
        private Label _previewReplayBadge;
        private DropdownField _semanticTrack, _semanticRole, _semanticHazard;
        private TextField _semanticLabel, _sceneryPrompt;
        private SemanticSceneState _lastSemantics;
        private bool _lidarVisible = true;
        private string _visualFeedback;
        private float _visualFeedbackUntil;
        private Button _view1, _view2, _view3;
        private DropdownField _cameraAgent;
        private TacticalCameraController _cameraController;
        private float _frameTimeEma = 1f / 60f;

        private void OnEnable()
        {
            var root = GetComponent<UIDocument>().rootVisualElement;
            if (stream == null || executor == null || media == null || visuals == null || root == null)
                throw new InvalidOperationException("Tactical HUD needs its stream and an initialized UIDocument.");
            root.style.flexGrow = 1;
            _screen = Required<VisualElement>(root, "screen");
            _badge = Required<Label>(root, "statusBadge");
            _connection = Required<Label>(root, "connectionState");
            _tracks = Required<Label>(root, "trackCount");
            _hazards = Required<Label>(root, "hazardCount");
            _age = Required<Label>(root, "sourceAge");
            _publication = Required<Label>(root, "publication");
            _calibration = Required<Label>(root, "calibration");
            _notice = Required<Label>(root, "sourceNotice");
            _sourceMode = Required<Label>(root, "sourceModeBanner");
            _modelMatrix = Required<VisualElement>(root, "modelMatrix");
            _modelMatrix.pickingMode = PickingMode.Ignore;
            _modelSam = Required<Label>(root, "modelSam"); _modelSparkOpen = Required<Label>(root, "modelSparkOpen");
            _modelSemantic = Required<Label>(root, "modelSemantic"); _modelVoice = Required<Label>(root, "modelVoice");
            _modelImage = Required<Label>(root, "modelImage"); _modelSandbox = Required<Label>(root, "modelSandbox");
            _brainTab = Required<Button>(root, "brainTab"); _semanticsTab = Required<Button>(root, "semanticsTab"); _sceneryTab = Required<Button>(root, "sceneryTab");
            _brainView = Required<VisualElement>(root, "brainView"); _semanticsView = Required<VisualElement>(root, "semanticsView");
            _sceneryView = Required<VisualElement>(root, "sceneryView");
            _brainTab.clicked += ShowBrainTab; _semanticsTab.clicked += ShowSemanticsTab; _sceneryTab.clicked += ShowSceneryTab;
            ShowDockTab(_brainTab, _brainView);
            _performance = Required<Label>(root, "performanceMetrics");
            _sourceMode.enableRichText = false; _performance.enableRichText = false;
            foreach (var label in new[] { _modelSam, _modelSparkOpen, _modelSemantic, _modelVoice, _modelImage, _modelSandbox }) label.enableRichText = false;
            _reconnect = Required<Button>(root, "reconnectButton");
            _reconnect.clicked += Reconnect;
            _policy = Required<Label>(root, "activePolicy"); _telemetry = Required<Label>(root, "agentTelemetry");
            _reason = Required<Label>(root, "controlReason"); _brain = Required<Label>(root, "brainState");
            _model = Required<Label>(root, "modelIdentity"); _compiler = Required<Label>(root, "compilerDiagnostics");
            foreach (var label in new[] { _policy, _telemetry, _reason, _brain, _model, _compiler }) label.enableRichText = false;
            _patrol = Required<Button>(root, "patrolButton"); _cordon = Required<Button>(root, "cordonButton");
            _search = Required<Button>(root, "searchButton"); _hold = Required<Button>(root, "holdButton"); _generate = Required<Button>(root, "generateButton");
            _instruction = Required<TextField>(root, "missionInstruction");
            _view1 = Required<Button>(root, "view1Button"); _view2 = Required<Button>(root, "view2Button"); _view3 = Required<Button>(root, "view3Button");
            _cameraAgent = Required<DropdownField>(root, "cameraAgent"); _cameraController = UnityEngine.Object.FindAnyObjectByType<TacticalCameraController>();
            if (_cameraController == null) throw new InvalidOperationException("Tactical camera controller is missing from the scene.");
            _cameraAgent.choices = new List<string>();
            foreach (var agent in executor.Agents) _cameraAgent.choices.Add(agent.AgentId.ToString(CultureInfo.InvariantCulture));
            if (_cameraAgent.choices.Count > 0) _cameraAgent.SetValueWithoutNotify(_cameraAgent.choices[0]);
            _view1.clicked += ViewIsometric; _view2.clicked += ViewMap; _view3.clicked += ViewChase;
            _recordVoice = Required<Button>(root, "recordVoiceButton"); _cancelVoice = Required<Button>(root, "cancelVoiceButton");
            _useTranscript = Required<Button>(root, "useTranscriptButton"); _previewToggle = Required<Button>(root, "previewToggleButton");
            _voiceStatus = Required<Label>(root, "voiceStatus"); _voiceDraft = Required<Label>(root, "voiceDraft"); _previewStatus = Required<Label>(root, "previewStatus");
            _previewImage = Required<Image>(root, "cameraPreview");
            _previewFrame = Required<VisualElement>(root, "previewFrame"); _maskImage = Required<Image>(root, "maskPreview"); _maskImage.scaleMode = ScaleMode.ScaleToFit;
            _previewReplayBadge = Required<Label>(root, "previewReplayBadge"); _previewReplayBadge.enableRichText = false;
            _maskToggle = Required<Button>(root, "maskToggleButton"); _maskStatus = Required<Label>(root, "maskStatus");
            _lidarToggle = Required<Button>(root, "lidarToggleButton"); _maskToggle.clicked += ToggleMasks; _lidarToggle.clicked += ToggleLidar;
            SetLidarVisibility(_lidarVisible);
            _semanticStatus = Required<Label>(root, "semanticStatus"); _semanticSelected = Required<Label>(root, "semanticSelected"); _sceneryStatus = Required<Label>(root, "sceneryStatus");
            _semanticTrack = Required<DropdownField>(root, "semanticTrack"); _semanticRole = Required<DropdownField>(root, "semanticRole"); _semanticHazard = Required<DropdownField>(root, "semanticHazard");
            _semanticTrack.RegisterValueChangedCallback(SelectSemanticTrack);
            _semanticRole.choices = new List<string> { "obstacle", "hazard", "rescue_target", "unknown" }; _semanticRole.value = "unknown";
            _semanticHazard.choices = new List<string> { "none", "caution", "high", "unknown" }; _semanticHazard.value = "unknown";
            _semanticLabel = Required<TextField>(root, "semanticLabel"); _sceneryPrompt = Required<TextField>(root, "sceneryPrompt");
            _loadSemantic = Required<Button>(root, "loadSemanticButton"); _applySemantic = Required<Button>(root, "applySemanticButton"); _clearSemantic = Required<Button>(root, "clearSemanticButton");
            _generateScenery = Required<Button>(root, "generateSceneryButton"); _resetScenery = Required<Button>(root, "resetSceneryButton");
            _loadSemantic.clicked += LoadTag; _applySemantic.clicked += ApplyTag; _clearSemantic.clicked += ClearTag; _generateScenery.clicked += GenerateScenery; _resetScenery.clicked += ResetScenery;
            foreach (var label in new[] { _maskStatus, _semanticStatus, _semanticSelected, _sceneryStatus }) label.enableRichText = false;
            _lastSemantics = null;
            _previewImage.scaleMode = ScaleMode.ScaleToFit;
            _voiceStatus.enableRichText = false; _voiceDraft.enableRichText = false; _previewStatus.enableRichText = false;
            _recordVoice.clicked += RecordVoice; _cancelVoice.clicked += CancelVoice; _useTranscript.clicked += UseTranscript; _previewToggle.clicked += TogglePreview;
            media.RecordingStarted += VoiceStarted;
            _patrol.clicked += Patrol; _cordon.clicked += Cordon; _search.clicked += Search; _hold.clicked += Hold; _generate.clicked += Generate;
            if (SustainedBenchmarkRecorder.IsRequested)
            {
                media.SetMasksEnabled(true);
                media.SetPreviewEnabled(true);
                SetLidarVisibility(true);
            }
            _screen.RegisterCallback<GeometryChangedEvent>(Resize);
            _refresh = root.schedule.Execute(Refresh).Every(100);
            Refresh();
        }

        private void Refresh()
        {
            var current = stream.TryGetCurrentView(out var view);
            var state = view.State;
            var sourceMode = state.Message == null ? (PerceptionSourceMode?)null : state.Message.SourceMode;
            var synthetic = sourceMode == PerceptionSourceMode.SyntheticTabletopReplay;
            _sourceMode.text = synthetic ? "SOURCE: SYNTHETIC TABLETOP REPLAY (NON-LIVE)" : current ? "SOURCE: LIVE CAMERA" : "SOURCE: WAITING FOR BACKEND";
            _screen.EnableInClassList("synthetic-source", synthetic);
            _screen.EnableInClassList("live", current);
            _connection.text = "STREAM / " + state.Phase.ToString().ToUpperInvariant();
            _badge.text = current ? "OBSERVATION CURRENT" : state.Phase == WorldStreamClientPhase.Connected ? "AWAITING CURRENT OBSERVATION" : "NO CURRENT OBSERVATION";
            var tracks = 0;
            var hazards = 0;
            if (current)
                foreach (var track in state.Message.World.Obstacles)
                    if (view.IsTrackCurrent(track)) { tracks++; if (track.Obstacle.IsHazardZone) hazards++; }
            _tracks.text = current ? tracks.ToString(CultureInfo.InvariantCulture) : "--";
            _hazards.text = current ? hazards.ToString(CultureInfo.InvariantCulture) : "--";
            _publication.text = state.Message == null ? "--" : state.Message.SequenceNumber.ToString(CultureInfo.InvariantCulture);
            _calibration.text = state.Message == null ? "--" : state.Message.World.CalibrationId.ToString("N").Substring(0, 8);
            _age.text = current && state.Message.World.SourceAgeMilliseconds.HasValue
                ? (state.Message.World.SourceAgeMilliseconds.Value + view.AdditionalAgeMilliseconds).ToString(CultureInfo.InvariantCulture) + " ms" : "--";
            _notice.text = synthetic ? "Synthetic local CV replay. No camera, SAM response, or cloud inference is represented by this source."
                : current ? "Geometry is scoped to this calibration and tracking session. Proxy height is an authored display setting."
                : state.FailureCategory != null ? "Stream status: " + state.FailureCategory + ". Configure calibrated perception, then reconnect."
                : "Waiting for a calibrated camera observation. An empty view does not mean the workspace is clear.";
            _policy.text = executor.ActivePolicyName;
            var measured = new StringBuilder();
            foreach (var agent in executor.Agents)
            {
                var telemetry = agent.ReadTelemetry();
                measured.Append('#').Append(telemetry.AgentId)
                    .Append(telemetry.Role == AgentRole.GroundVehicle ? " AGV  " : " DRONE  ")
                    .Append(telemetry.Status).Append("  ")
                    .Append((telemetry.BatteryLevel * 100).ToString("F0", CultureInfo.InvariantCulture)).Append("%\n  ")
                    .Append(telemetry.CurrentPosition.X.ToString("F1", CultureInfo.InvariantCulture)).Append(',')
                    .Append(telemetry.CurrentPosition.Z.ToString("F1", CultureInfo.InvariantCulture)).Append("  ·  ");
                var lidar = telemetry.Lidar;
                measured.Append("LiDAR ").Append(lidar == null ? "unavailable" : lidar.AgeMilliseconds > 250 ? "stale" :
                    (lidar.MinimumDistanceMeters.HasValue ? lidar.MinimumDistanceMeters.Value.ToString("F2", CultureInfo.InvariantCulture) + "m" : "clear " + lidar.RangeMeters.ToString("F1", CultureInfo.InvariantCulture) + "m") + "/" + lidar.HitCount).Append("\n");
            }
            _telemetry.text = measured.ToString();
            _reason.text = Time.unscaledTime < _feedbackUntil ? _feedback : executor.FailureCategory ?? "Validated controls and current observations";
            var control = executor.Control;
            _brain.text = "Brain / " + (control?.BrainStatus ?? "awaiting backend") + (control == null ? "" : " / attempt " + control.GenerationAttempts);
            _model.text = "Model / " + (control?.ModelIdUsed ?? "none") + (control?.UsedModelFallback == true ? " / 404 fallback" : "");
            var readiness = visuals.Readiness;
            _modelSam.text = readiness == null ? "SAM 3.1\nSTATUS OFFLINE" : "SAM 3.1\n" +
                (readiness.MetaApiKeyConfigured ? "META KEY SET" : "META KEY MISSING");
            _modelSparkOpen.text = readiness == null ? "MUSE SPARK OPEN\nSTATUS OFFLINE" : "MUSE SPARK OPEN\n" +
                (readiness.BasetenApiKeyConfigured ? "BASETEN KEY SET" : "BASETEN KEY MISSING") +
                (control?.ModelIdUsed == null ? "" : " · " + (control.UsedModelFallback ? "FALLBACK ID" : "MODEL ACTIVE"));
            _modelSemantic.text = readiness == null ? "MUSE SPARK 1.3\nSTATUS OFFLINE" : "MUSE SPARK 1.3\nMODEL CONFIGURED";
            _modelVoice.text = readiness == null ? "MUSE VOICE\nSTATUS OFFLINE" : "MUSE VOICE\nMODEL CONFIGURED";
            _modelImage.text = readiness == null ? "MUSE IMAGE\nSTATUS OFFLINE" : "MUSE IMAGE\nMODEL CONFIGURED";
            _modelSandbox.text = readiness == null ? "ROSLYN + APPCONTAINER\nSTATUS OFFLINE" : "ROSLYN + APPCONTAINER\n" +
                (readiness.PolicyWorkerPresent ? "WORKER READY" : "WORKER MISSING") + " · " +
                (readiness.WindowsAppContainerSupported ? "ISOLATION READY" : "ISOLATION N/A");
            var fps = 1f / Mathf.Max(_frameTimeEma, 0.0001f);
            var evaluation = control == null ? "--" : control.EvaluationDurationMilliseconds.ToString("F1", CultureInfo.InvariantCulture);
            var observedAge = current && state.Message.World.SourceAgeMilliseconds.HasValue
                ? (state.Message.World.SourceAgeMilliseconds.Value + view.AdditionalAgeMilliseconds).ToString(CultureInfo.InvariantCulture) : "--";
            _performance.text = "FPS " + fps.ToString("F0", CultureInfo.InvariantCulture) + "  /  FRAME " + (_frameTimeEma * 1000).ToString("F1", CultureInfo.InvariantCulture) +
                " ms  /  SOURCE " + observedAge + " ms  /  SANDBOX " + evaluation + " ms";
            var diagnostics = new StringBuilder(control?.BrainDiagnostic ?? "");
            if (control != null) foreach (var diagnostic in control.CompilationDiagnostics) diagnostics.Append("\n").Append(diagnostic);
            _compiler.text = diagnostics.ToString();
            _voiceStatus.text = media.VoiceStatus; _recordVoice.text = media.IsRecording ? "STOP & TRANSCRIBE (META API)" : "RECORD VOICE (META API)";
            _recordVoice.SetEnabled(!media.IsTranscribing); _cancelVoice.SetEnabled(media.IsRecording || media.IsTranscribing);
            var draft = media.Transcript;
            _useTranscript.SetEnabled(draft != null && draft.Transcript.Length > 0);
            if (draft != null && !ReferenceEquals(draft, _lastTranscript))
            {
                _lastTranscript = draft; _voiceDraft.text = draft.Transcript;
                if (draft.Transcript.Length > 0 && _instruction.value == _instructionBeforeVoice) _instruction.value = draft.Transcript;
            }
            _previewToggle.text = media.PreviewEnabled ? "HIDE CAMERA PREVIEW" : "SHOW CAMERA PREVIEW";
            var previewTexture = media.PreviewTexture;
            _previewStatus.text = media.PreviewStatus; _previewImage.image = previewTexture;
            _maskImage.image = media.MaskTexture; _previewFrame.EnableInClassList("preview-hidden", previewTexture == null);
            _previewReplayBadge.EnableInClassList("preview-hidden", !synthetic || previewTexture == null);
            _maskToggle.text = media.MasksEnabled ? "HIDE SAM MASKS" : "SHOW SAM MASKS"; _maskStatus.text = media.MaskStatus;
            _lidarToggle.text = _lidarVisible ? "HIDE LIDAR BEAMS" : "SHOW LIDAR BEAMS";
            _semanticStatus.text = Time.unscaledTime < _visualFeedbackUntil ? _visualFeedback : visuals.SemanticStatus;
            var semantics = visuals.Semantics;
            if (semantics != null && !ReferenceEquals(semantics, _lastSemantics))
            {
                var selected = _semanticTrack.value; var sameSession = _lastSemantics?.SessionId == semantics.SessionId;
                _lastSemantics = semantics; var choices = new List<string>(); foreach (var item in semantics.Objects) choices.Add(item.TrackId.ToString(CultureInfo.InvariantCulture));
                _semanticTrack.choices = choices; var next = sameSession && choices.Contains(selected) ? selected : choices.Count > 0 ? choices[0] : "";
                _semanticTrack.SetValueWithoutNotify(next); if (!sameSession || selected != next) LoadTag();
            }
            var tag = SelectedTag();
            _semanticSelected.text = tag == null ? "Select a confirmed object" : "#" + tag.TrackId + " / " + tag.Status + (tag.OperatorOverride ? " / operator" : " / model") +
                (tag.Classification == null ? "" : "\n" + tag.Classification.Label + " · " + tag.Classification.Role + " · " + tag.Classification.HazardLevel) + (tag.FailureCategory == null ? "" : "\n" + tag.FailureCategory);
            _semanticTrack.SetEnabled(semantics != null && semantics.Objects.Count > 0);
            _loadSemantic.SetEnabled(tag?.Classification != null); _applySemantic.SetEnabled(tag != null && !visuals.IsEditing); _clearSemantic.SetEnabled(tag != null && !visuals.IsEditing);
            _generateScenery.SetEnabled(true); _generateScenery.text = visuals.IsGenerating ? "REPLACE SURFACE JOB" : "GENERATE SURFACE (META API)"; _sceneryStatus.text = visuals.SceneryStatus;
        }

        private void Update()
        {
            var delta = Mathf.Max(Time.unscaledDeltaTime, 0.0001f);
            _frameTimeEma += (delta - _frameTimeEma) * (1 - Mathf.Exp(-5 * delta));
        }

        private void VoiceStarted() => _instructionBeforeVoice = _instruction.value;
        private void RecordVoice() => media.ToggleRecording();
        private void CancelVoice() => media.CancelVoice();
        private void UseTranscript() { var value = media.Transcript; if (value != null && value.Transcript.Length > 0) _instruction.value = value.Transcript; }
        private void TogglePreview() => media.SetPreviewEnabled(!media.PreviewEnabled);
        private void ToggleMasks() => media.SetMasksEnabled(!media.MasksEnabled);
        private void ToggleLidar() => SetLidarVisibility(!_lidarVisible);
        private void SetLidarVisibility(bool visible)
        {
            _lidarVisible = visible;
            foreach (var agent in executor.Agents) if (agent.Lidar != null) agent.Lidar.SetVisualizationEnabled(visible);
        }
        private SemanticObjectState SelectedTag()
        {
            var state = visuals.Semantics; if (state == null || !int.TryParse(_semanticTrack.value, NumberStyles.None, CultureInfo.InvariantCulture, out var id)) return null;
            foreach (var item in state.Objects) if (item.TrackId == id) return item; return null;
        }
        private void SelectSemanticTrack(ChangeEvent<string> change) => LoadTag();
        private void LoadTag() { var tag = SelectedTag()?.Classification; _semanticLabel.value = tag?.Label ?? ""; _semanticRole.value = tag?.Role ?? "unknown"; _semanticHazard.value = tag?.HazardLevel ?? "unknown"; }
        private void ApplyTag()
        {
            var tag = SelectedTag(); if (tag == null) return;
            try { visuals.ApplyOverride(tag.TrackId, new ObjectSemantics(_semanticLabel.value, _semanticRole.value, _semanticHazard.value, 1)); }
            catch (ArgumentException) { _visualFeedback = "Enter a label and valid simulation tags"; _visualFeedbackUntil = Time.unscaledTime + 4; }
        }
        private void ClearTag() { var tag = SelectedTag(); if (tag != null) visuals.ApplyOverride(tag.TrackId, null); }
        private void GenerateScenery()
        {
            try { visuals.GenerateSurface(_sceneryPrompt.value); }
            catch (ArgumentException) { _visualFeedback = "Enter a single-line surface description (1–1000 characters)"; _visualFeedbackUntil = Time.unscaledTime + 4; }
        }
        private void ResetScenery() => visuals.RestoreDefault();

        private void Mission(SwarmMissionKind kind, string instruction, bool brain = false)
        {
            _selectedMission = kind;
            try { executor.RequestMission(kind, instruction, brain); _feedback = brain ? "Synthesis requested; baseline remains supervised." : "Mission submitted: " + kind; }
            catch (Exception error) when (error is ArgumentException || error is InvalidOperationException) { _feedback = "Mission rejected: check instruction and active control."; }
            _feedbackUntil = Time.unscaledTime + 4;
        }
        private void Patrol() => Mission(SwarmMissionKind.PatrolPerimeter, "Patrol the workspace perimeter with all available agents.");
        private void Cordon() => Mission(SwarmMissionKind.HazardQuarantine, "Create a three-meter cordon around the largest hazard or obstacle.");
        private void Search() => Mission(SwarmMissionKind.GridSearchAndRescue, "Systematically search the free workspace cells.");
        private void Hold() => Mission(SwarmMissionKind.HoldPosition, "Immediately hold every agent at its current measured position.");
        private void Generate() => Mission(_selectedMission, _instruction.value, true);

        private void Reconnect() { stream.Reconnect(); Refresh(); }
        private void ShowBrainTab() => ShowDockTab(_brainTab, _brainView);
        private void ShowSemanticsTab() => ShowDockTab(_semanticsTab, _semanticsView);
        private void ShowSceneryTab() => ShowDockTab(_sceneryTab, _sceneryView);
        private void ShowDockTab(Button selected, VisualElement activeView)
        {
            _brainTab?.EnableInClassList("selected", selected == _brainTab);
            _semanticsTab?.EnableInClassList("selected", selected == _semanticsTab);
            _sceneryTab?.EnableInClassList("selected", selected == _sceneryTab);
            _brainView?.EnableInClassList("active", activeView == _brainView);
            _semanticsView?.EnableInClassList("active", activeView == _semanticsView);
            _sceneryView?.EnableInClassList("active", activeView == _sceneryView);
        }
        private void ViewIsometric() => _cameraController.SetPreset(1, null);
        private void ViewMap() => _cameraController.SetPreset(2, null);
        private void ViewChase()
        {
            var selected = SelectedCameraAgent();
            if (selected == null) { _visualFeedback = "Select a valid swarm unit for chase view"; _visualFeedbackUntil = Time.unscaledTime + 4; return; }
            _cameraController.SetPreset(3, selected);
        }
        private SwarmAgentController SelectedCameraAgent()
        {
            if (!int.TryParse(_cameraAgent.value, NumberStyles.None, CultureInfo.InvariantCulture, out var id)) return null;
            foreach (var agent in executor.Agents) if (agent.AgentId == id) return agent;
            return null;
        }
        private void Resize(GeometryChangedEvent change) => _screen.EnableInClassList("compact", change.newRect.width < 1100);
        private void OnDisable()
        {
            _refresh?.Pause();
            if (_reconnect != null) _reconnect.clicked -= Reconnect;
            if (_brainTab != null) _brainTab.clicked -= ShowBrainTab;
            if (_semanticsTab != null) _semanticsTab.clicked -= ShowSemanticsTab;
            if (_sceneryTab != null) _sceneryTab.clicked -= ShowSceneryTab;
            if (_patrol != null) _patrol.clicked -= Patrol; if (_cordon != null) _cordon.clicked -= Cordon;
            if (_search != null) _search.clicked -= Search; if (_hold != null) _hold.clicked -= Hold; if (_generate != null) _generate.clicked -= Generate;
            if (_recordVoice != null) _recordVoice.clicked -= RecordVoice; if (_cancelVoice != null) _cancelVoice.clicked -= CancelVoice;
            if (_useTranscript != null) _useTranscript.clicked -= UseTranscript; if (_previewToggle != null) _previewToggle.clicked -= TogglePreview;
            if (media != null) media.RecordingStarted -= VoiceStarted;
            if (_maskToggle != null) _maskToggle.clicked -= ToggleMasks; if (_lidarToggle != null) _lidarToggle.clicked -= ToggleLidar;
            if (_view1 != null) _view1.clicked -= ViewIsometric; if (_view2 != null) _view2.clicked -= ViewMap; if (_view3 != null) _view3.clicked -= ViewChase;
            if (_loadSemantic != null) _loadSemantic.clicked -= LoadTag; if (_applySemantic != null) _applySemantic.clicked -= ApplyTag; if (_clearSemantic != null) _clearSemantic.clicked -= ClearTag;
            if (_generateScenery != null) _generateScenery.clicked -= GenerateScenery; if (_resetScenery != null) _resetScenery.clicked -= ResetScenery;
            _semanticTrack?.UnregisterValueChangedCallback(SelectSemanticTrack);
            _screen?.UnregisterCallback<GeometryChangedEvent>(Resize);
        }
        private static T Required<T>(VisualElement root, string name) where T : VisualElement =>
            root.Q<T>(name) ?? throw new InvalidOperationException("Missing HUD element: " + name);
    }
}
