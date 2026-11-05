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
using OmniTwin.Policies;
using OmniTwin.Simulation.World;

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
        private Label _policy, _reason, _brain, _model, _compiler;
        private VisualElement _unitCardsRoot;
        private readonly List<UnitCardView> _unitCards = new List<UnitCardView>(5);
        private Button _patrol, _cordon, _search, _hold, _generate;
        private TextField _instruction;
        private SwarmMissionKind _selectedMission = SwarmMissionKind.HazardQuarantine;
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
        private VisualElement _demoPresenter;
        private Label _demoChapter, _demoSubtitle, _demoSource;
        private VisualElement _demoCursor;
        private VisualElement _operationStrip;
        private Label _operationText, _lastActionText;
        private ProgressBar _operationProgress;
        private Button _selectTrack1, _selectTrack2, _selectTrack3, _steelSurface, _marsSurface, _blueprintSurface;
        private TacticalScenePresentation _scenePresentation;
        private float _sessionStartedAt, _operationClearAt, _voiceRecordingStartedAt;
        private float _operationProgressTarget;
        private string _operationName = "", _semanticChoiceSession = "";
        private bool _operationFailed;

        /// <summary>Gets the actual local policy name for conditional presentation choices.</summary>
        public string ActivePolicyNameForDemo => executor == null ? string.Empty : executor.ActivePolicyName;

        private void OnEnable()
        {
            var root = GetComponent<UIDocument>().rootVisualElement;
            if (stream == null || executor == null || media == null || visuals == null || root == null)
                throw new InvalidOperationException("Tactical HUD needs its stream and an initialized UIDocument.");
            root.style.flexGrow = 1;
            _screen = Required<VisualElement>(root, "screen");
            _sessionStartedAt = Time.unscaledTime;
            EnsureOperationStatus(root);
            _scenePresentation = UnityEngine.Object.FindAnyObjectByType<TacticalScenePresentation>();
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
            _policy = Required<Label>(root, "activePolicy"); _unitCardsRoot = Required<VisualElement>(root, "agentCards");
            BuildUnitCards();
            _reason = Required<Label>(root, "controlReason"); _brain = Required<Label>(root, "brainState");
            _model = Required<Label>(root, "modelIdentity"); _compiler = Required<Label>(root, "compilerDiagnostics");
            foreach (var label in new[] { _policy, _reason, _brain, _model, _compiler }) label.enableRichText = false;
            _patrol = Required<Button>(root, "patrolButton"); _cordon = Required<Button>(root, "cordonButton");
            _search = Required<Button>(root, "searchButton"); _hold = Required<Button>(root, "holdButton"); _generate = Required<Button>(root, "generateButton");
            _instruction = Required<TextField>(root, "missionInstruction");
            if (string.IsNullOrWhiteSpace(_instruction.value))
                _instruction.value = "Establish a 2.8m safety cordon around hazard object #3; use ground AGVs at the cordon and patrol the outer perimeter with aerial drones.";
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
            _selectTrack1 = Required<Button>(root, "selectTrack1Button"); _selectTrack2 = Required<Button>(root, "selectTrack2Button"); _selectTrack3 = Required<Button>(root, "selectTrack3Button");
            _selectTrack1.clicked += SelectTrack1; _selectTrack2.clicked += SelectTrack2; _selectTrack3.clicked += SelectTrack3;
            _semanticRole.choices = new List<string> { "obstacle", "hazard", "rescue_target", "unknown" }; _semanticRole.value = "unknown";
            _semanticHazard.choices = new List<string> { "none", "caution", "high", "unknown" }; _semanticHazard.value = "unknown";
            _semanticLabel = Required<TextField>(root, "semanticLabel"); _sceneryPrompt = Required<TextField>(root, "sceneryPrompt");
            _loadSemantic = Required<Button>(root, "loadSemanticButton"); _applySemantic = Required<Button>(root, "applySemanticButton"); _clearSemantic = Required<Button>(root, "clearSemanticButton");
            _generateScenery = Required<Button>(root, "generateSceneryButton"); _resetScenery = Required<Button>(root, "resetSceneryButton");
            _steelSurface = Required<Button>(root, "steelSurfaceButton"); _marsSurface = Required<Button>(root, "marsSurfaceButton"); _blueprintSurface = Required<Button>(root, "blueprintSurfaceButton");
            _loadSemantic.clicked += LoadTag; _applySemantic.clicked += ApplyTag; _clearSemantic.clicked += ClearTag; _generateScenery.clicked += GenerateScenery; _resetScenery.clicked += ResetScenery;
            _steelSurface.clicked += ApplySteelSurface; _marsSurface.clicked += ApplyMarsSurface; _blueprintSurface.clicked += ApplyBlueprintSurface;
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
            if (DemoDirector.IsRequested && GetComponent<DemoDirector>() == null)
                gameObject.AddComponent<DemoDirector>();
        }

        private void EnsureOperationStatus(VisualElement root)
        {
            if (_operationStrip != null) return;
            var column = root.Q<VisualElement>(className: "world-column");
            if (column == null) throw new InvalidOperationException("Tactical HUD world column is missing.");
            _operationStrip = new VisualElement { name = "operationStatus" };
            _operationStrip.AddToClassList("operation-strip");
            _operationText = new Label { name = "operationText" };
            _operationText.AddToClassList("operation-message");
            _operationProgress = new ProgressBar { name = "operationProgress", lowValue = 0, highValue = 100, value = 0 };
            _operationProgress.AddToClassList("operation-progress");
            _lastActionText = new Label { name = "lastActionText", text = "Last action: none" };
            _lastActionText.AddToClassList("last-action");
            _operationStrip.Add(_operationText);
            _operationStrip.Add(_operationProgress);
            _operationStrip.Add(_lastActionText);
            column.Insert(Mathf.Max(0, column.childCount - 1), _operationStrip);
            SetReadyStatus();
        }

        private void BeginOperation(string action, string message, float progress = 12)
        {
            _operationName = action ?? "Action";
            _operationFailed = false;
            _operationClearAt = 0;
            _operationProgressTarget = Mathf.Clamp(progress, 0, 100);
            _operationText.text = message ?? "Working";
            _operationProgress.value = _operationProgressTarget;
            _operationStrip.EnableInClassList("operation-working", true);
            _operationStrip.EnableInClassList("operation-complete", false);
            _operationStrip.EnableInClassList("operation-failed", false);
            var elapsed = Mathf.Max(0, Time.unscaledTime - _sessionStartedAt);
            _lastActionText.text = "Last action: " + _operationName + " · " + DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) +
                " · +" + elapsed.ToString("F1", CultureInfo.InvariantCulture) + "s";
        }

        private void UpdateOperation(string message, float progress)
        {
            _operationText.text = message ?? string.Empty;
            _operationProgressTarget = Mathf.Clamp(progress, 0, 100);
            _operationProgress.value = _operationProgressTarget;
        }

        private void CompleteOperation(string message, bool success = true)
        {
            _operationFailed = !success;
            _operationText.text = message ?? string.Empty;
            _operationProgressTarget = 100;
            _operationProgress.value = 100;
            _operationStrip.EnableInClassList("operation-working", false);
            _operationStrip.EnableInClassList("operation-complete", success);
            _operationStrip.EnableInClassList("operation-failed", !success);
            _operationClearAt = Time.unscaledTime + 12f;
        }

        private void SetReadyStatus()
        {
            if (_operationText == null) return;
            _operationName = string.Empty;
            _operationText.text = "SYSTEM READY · Local controls respond immediately; model calls report their real result";
            _operationProgressTarget = 0;
            _operationProgress.value = 0;
            _operationStrip.EnableInClassList("operation-working", false);
            _operationStrip.EnableInClassList("operation-complete", false);
            _operationStrip.EnableInClassList("operation-failed", false);
        }

        private void RefreshOperationStatus()
        {
            if (_operationClearAt > 0 && Time.unscaledTime >= _operationClearAt) SetReadyStatus();
            if (string.IsNullOrEmpty(_operationName)) return;
            if (_operationName == "BRAIN SYNTHESIS")
            {
                var control = executor.Control;
                var mission = executor.CurrentMission;
                if (executor.IsVerifiedBrainPolicyActive && control != null && mission != null && control.TimestampUnixMs >= mission.IssuedAtUnixMs && control.BrainStatus == "active")
                    CompleteOperation("POLICY VERIFIED AND ACTIVE · " + control.PolicyName + " · Roslyn/AppContainer response received", true);
                else if (!executor.IsLocalMissionSelected && control != null && mission != null && control.TimestampUnixMs >= mission.IssuedAtUnixMs && control.BrainStatus == "fallback")
                    CompleteOperation("BRAIN FALLBACK · " + (control.BrainDiagnostic ?? "generation_or_verification_failed") + " · " + executor.LocalExecutionStatus, false);
                else if (executor.IsLocalMissionSelected)
                    UpdateOperation("LOCAL BASELINE · " + executor.LocalExecutionStatus + " · " +
                        (executor.FailureCategory ?? "waiting for a verified model result"), 35);
            }
            else if (_operationName == "MUSE IMAGE")
            {
                if (visuals.IsGenerating) UpdateOperation("MUSE IMAGE REQUEST RUNNING · local surface remains visible", 55);
                else if (visuals.SceneryStatus.IndexOf("surface active", StringComparison.OrdinalIgnoreCase) >= 0)
                    CompleteOperation("MUSE IMAGE SURFACE APPLIED · validated image is active", true);
                else if (visuals.SceneryStatus.IndexOf("Muse Image failed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    visuals.SceneryStatus.IndexOf("decode_failed", StringComparison.OrdinalIgnoreCase) >= 0)
                    CompleteOperation(visuals.SceneryStatus, false);
            }
            else if (_operationName == "VOICE RECORDING")
            {
                if (media.IsRecording)
                {
                    var elapsed = Mathf.Max(0, Time.unscaledTime - _voiceRecordingStartedAt);
                    UpdateOperation("RECORDING VOICE · " + Mathf.Max(0, 5f - elapsed).ToString("F1", CultureInfo.InvariantCulture) + "s remaining · max 5 s", Mathf.Clamp01(elapsed / 5f) * 90f);
                }
                else if (!string.IsNullOrWhiteSpace(media.Transcript?.Transcript))
                    CompleteOperation("TRANSCRIPT READY · review it, then click USE TRANSCRIPT", true);
                else if (!media.IsTranscribing && !string.IsNullOrWhiteSpace(media.VoiceStatus) &&
                    media.VoiceStatus.IndexOf("failed", StringComparison.OrdinalIgnoreCase) >= 0)
                    CompleteOperation("VOICE FAILED · " + media.VoiceStatus, false);
            }
            else if (executor.CurrentMission != null &&
                string.Equals(_operationName, executor.CurrentMission.Kind.ToString().ToUpperInvariant(), StringComparison.Ordinal))
            {
                var mission = executor.CurrentMission;
                if (mission.Kind == SwarmMissionKind.HoldPosition && executor.ActivePolicyName == nameof(EmergencyHoldPolicy))
                {
                    CompleteOperation("HOLD ACTIVE · every agent stop was applied immediately", true);
                    return;
                }

                if (executor.IsLocalMissionMoving)
                {
                    CompleteOperation("LOCAL POLICY ACTIVE · " + executor.LocalExecutionStatus, true);
                    return;
                }

                var control = executor.Control;
                if (!executor.IsLocalMissionSelected && control != null && control.IsSourceCurrent &&
                    control.TimestampUnixMs >= mission.IssuedAtUnixMs && control.PolicyName == executor.ActivePolicyName)
                {
                    CompleteOperation("BACKEND POLICY ACTIVE · " + control.PolicyName + " · current stream validated", true);
                    return;
                }

                if (executor.IsLocalMissionSelected &&
                    executor.LocalExecutionStatus.StartsWith("LOCAL HOLD", StringComparison.Ordinal))
                {
                    CompleteOperation(executor.LocalExecutionStatus, false);
                    return;
                }

                if (!string.IsNullOrWhiteSpace(executor.FailureCategory))
                    UpdateOperation("LOCAL FALLBACK · " + executor.LocalExecutionStatus + " · " + executor.FailureCategory, 68);
                else
                    UpdateOperation("MISSION ACCEPTED · waiting for a fresh observation or validated policy reply", 54);
            }
        }

        /// <summary>Sets the recording's visible chapter and subtitle without implying any model response.</summary>
        public void SetDemoPresenter(string chapter, string subtitle, bool visible = true)
        {
            if (_screen == null) return;
            EnsureDemoPresenter();
            _demoPresenter.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
            _demoChapter.text = chapter ?? string.Empty;
            _demoSubtitle.text = subtitle ?? string.Empty;
        }

        /// <summary>Highlights the HUD control exercised by the current scripted presentation step.</summary>
        public void SetDemoHighlight(DemoHudTarget target)
        {
            SetHighlight(_view1, target == DemoHudTarget.ViewIso);
            SetHighlight(_view2, target == DemoHudTarget.ViewMap);
            SetHighlight(_view3, target == DemoHudTarget.ViewChase);
            SetHighlight(_patrol, target == DemoHudTarget.Patrol);
            SetHighlight(_cordon, target == DemoHudTarget.Cordon);
            SetHighlight(_search, target == DemoHudTarget.Search);
            SetHighlight(_hold, target == DemoHudTarget.Hold);
            SetHighlight(_brainTab, target == DemoHudTarget.BrainTab);
            SetHighlight(_semanticsTab, target == DemoHudTarget.SemanticsTab);
            SetHighlight(_sceneryTab, target == DemoHudTarget.SceneryTab);
            SetHighlight(_generate, target == DemoHudTarget.Synthesize);
            SetHighlight(_generateScenery, target == DemoHudTarget.GenerateSurface);
            SetHighlight(_recordVoice, target == DemoHudTarget.RecordVoice);
            SetHighlight(_useTranscript, target == DemoHudTarget.UseTranscript);
            SetHighlight(_applySemantic, target == DemoHudTarget.ApplyOperatorTag);
            ShowDemoPointer(target);
        }

        /// <summary>Switches the visible command-dock page for the presentation.</summary>
        public void SelectDemoDock(int page)
        {
            switch (page)
            {
                case 0: ShowBrainTab(); break;
                case 1: ShowSemanticsTab(); break;
                case 2: ShowSceneryTab(); break;
                default: throw new ArgumentOutOfRangeException(nameof(page));
            }
            ShowDemoPointer(page == 0 ? DemoHudTarget.BrainTab : page == 1 ? DemoHudTarget.SemanticsTab : DemoHudTarget.SceneryTab);
        }

        /// <summary>Moves the tactical camera to an authored preset and selected agent.</summary>
        public void SetDemoCamera(int preset, int agentId = 1)
        {
            var agent = preset == 3 ? FindAgent(agentId) : null;
            if (preset == 3 && agent == null) throw new InvalidOperationException("The requested demo chase agent is unavailable.");
            if (preset < 1 || preset > 3) throw new ArgumentOutOfRangeException(nameof(preset));
            if (preset == 1) ViewIsometric();
            else if (preset == 2) ViewMap();
            else { _cameraAgent.SetValueWithoutNotify(agentId.ToString(CultureInfo.InvariantCulture)); _cameraController.SetPreset(3, agent); }
            ShowDemoPointer(preset == 1 ? DemoHudTarget.ViewIso : preset == 2 ? DemoHudTarget.ViewMap : DemoHudTarget.ViewChase);
        }

        /// <summary>Applies a documented built-in mission through the same validated control path as the operator HUD.</summary>
        public void RunDemoMission(SwarmMissionKind kind, string instruction, float radiusMeters = 3f)
        {
            var target = kind switch
            {
                SwarmMissionKind.PatrolPerimeter => DemoHudTarget.Patrol,
                SwarmMissionKind.HazardQuarantine => DemoHudTarget.Cordon,
                SwarmMissionKind.GridSearchAndRescue => DemoHudTarget.Search,
                _ => DemoHudTarget.Hold
            };
            SetDemoHighlight(target);
            ShowDemoPointer(target);
            Mission(kind, instruction, false, radiusMeters);
        }

        /// <summary>Applies an explicit operator hazard tag to the first current live track for the recorded cordon.</summary>
        public void ApplyDemoOperatorHazardTag()
        {
            SelectDemoDock(1);
            if (_semanticTrack == null || _semanticTrack.choices.Count == 0)
            {
                CompleteOperation("OPERATOR HAZARD TAG NOT APPLIED · no current live track", false);
                return;
            }
            SelectTrackButton(0);
            _semanticLabel.value = "Operator-tagged hazard marker";
            _semanticRole.value = "hazard";
            _semanticHazard.value = "high";
            SetDemoHighlight(DemoHudTarget.ApplyOperatorTag);
            ApplyTag();
        }

        /// <summary>Queues a real backend policy request; provider, Roslyn, and worker outcomes remain backend-owned.</summary>
        public void RunDemoBrainSynthesis(string instruction)
        {
            SelectDemoDock(0);
            _instruction.value = instruction ?? string.Empty;
            SetDemoHighlight(DemoHudTarget.Synthesize);
            Mission(SwarmMissionKind.HazardQuarantine, _instruction.value, true, 2.8f);
        }

        /// <summary>Records a real short microphone command, uses a fresh Muse Voice transcript, then requests Brain synthesis.</summary>
        public void BeginDemoVoiceCommand(string typedFallback)
        {
            SelectDemoDock(0);
            var previousTranscript = media.Transcript;
            SetDemoHighlight(DemoHudTarget.RecordVoice);
            RecordVoice();
            StartCoroutine(FinishDemoVoiceCommand(typedFallback ?? string.Empty, previousTranscript));
        }

        private System.Collections.IEnumerator FinishDemoVoiceCommand(string typedFallback, VoiceTranscription previousTranscript)
        {
            var deadline = Time.realtimeSinceStartup + 10f;
            while (Time.realtimeSinceStartup < deadline && (media.IsRecording || media.IsTranscribing)) yield return null;
            var transcript = media.Transcript;
            if (transcript != null && !ReferenceEquals(transcript, previousTranscript) && !string.IsNullOrWhiteSpace(transcript.Transcript))
            {
                SetDemoHighlight(DemoHudTarget.UseTranscript);
                UseTranscript();
            }
            else
            {
                _instruction.value = typedFallback;
                CompleteOperation("NO FRESH MUSE VOICE TRANSCRIPT · visible typed fallback selected", true);
            }
            RunDemoBrainSynthesis(_instruction.value);
        }

        /// <summary>Enables the received preview and paired mask overlay.</summary>
        public void EnableDemoPerceptionOverlay()
        {
            media.SetMasksEnabled(true);
            media.SetPreviewEnabled(true);
            SetLidarVisibility(true);
        }

        /// <summary>Returns the backend's observed readiness, if the asynchronous readiness query has completed.</summary>
        public BackendModelReadiness DemoReadiness => visuals == null ? null : visuals.Readiness;

        /// <summary>Submits one real Muse Image request through the existing bounded client.</summary>
        public bool RequestDemoSurface(string prompt)
        {
            if (visuals == null || visuals.Readiness?.MetaApiKeyConfigured != true) return false;
            SelectDemoDock(2);
            _sceneryPrompt.value = prompt ?? string.Empty;
            SetDemoHighlight(DemoHudTarget.GenerateSurface);
            GenerateScenery();
            return true;
        }

        private void ShowDemoPointer(DemoHudTarget target)
        {
            if (_screen == null || target == DemoHudTarget.None) return;
            var element = target switch
            {
                DemoHudTarget.ViewIso => _view1,
                DemoHudTarget.ViewMap => _view2,
                DemoHudTarget.ViewChase => _view3,
                DemoHudTarget.Patrol => _patrol,
                DemoHudTarget.Cordon => _cordon,
                DemoHudTarget.Search => _search,
                DemoHudTarget.Hold => _hold,
                DemoHudTarget.BrainTab => _brainTab,
                DemoHudTarget.SemanticsTab => _semanticsTab,
                DemoHudTarget.SceneryTab => _sceneryTab,
                DemoHudTarget.Synthesize => _generate,
                DemoHudTarget.GenerateSurface => _generateScenery,
                DemoHudTarget.RecordVoice => _recordVoice,
                DemoHudTarget.UseTranscript => _useTranscript,
                DemoHudTarget.ApplyOperatorTag => _applySemantic,
                _ => null
            };
            if (element == null || element.worldBound.width <= 0 || element.worldBound.height <= 0) return;
            EnsureDemoPresenter();
            var screenBounds = _screen.worldBound;
            var targetBounds = element.worldBound;
            _demoCursor.style.left = targetBounds.center.x - screenBounds.x - 12;
            _demoCursor.style.top = targetBounds.center.y - screenBounds.y - 12;
            _demoCursor.style.display = DisplayStyle.Flex;
            _demoCursor.EnableInClassList("demo-cursor-pulse", false);
            _demoCursor.schedule.Execute(() => _demoCursor.EnableInClassList("demo-cursor-pulse", true)).StartingIn(35);
            _demoCursor.schedule.Execute(() => _demoCursor.EnableInClassList("demo-cursor-pulse", false)).StartingIn(430);
            _demoCursor.BringToFront();
        }

        private void EnsureDemoPresenter()
        {
            if (_demoPresenter == null)
            {
                _demoPresenter = new VisualElement { name = "demoPresenter", pickingMode = PickingMode.Ignore };
                _demoPresenter.AddToClassList("demo-presenter");
                _demoChapter = new Label { name = "demoChapter", pickingMode = PickingMode.Ignore };
                _demoChapter.AddToClassList("demo-chapter");
                _demoSubtitle = new Label { name = "demoSubtitle", pickingMode = PickingMode.Ignore };
                _demoSubtitle.AddToClassList("demo-subtitle");
                _demoSource = new Label("SCRIPTED PRESENTATION · LIVE CAMERA · SAM/MODEL STATES SHOWN FROM BACKEND") { name = "demoSource", pickingMode = PickingMode.Ignore };
                _demoSource.AddToClassList("demo-source");
                _demoPresenter.Add(_demoChapter);
                _demoPresenter.Add(_demoSubtitle);
                _demoPresenter.Add(_demoSource);
                _screen.Add(_demoPresenter);
            }
            if (_demoCursor != null) return;
            _demoCursor = new VisualElement { name = "demoCursor", pickingMode = PickingMode.Ignore };
            _demoCursor.AddToClassList("demo-cursor");
            var ripple = new VisualElement { name = "demoClickRipple", pickingMode = PickingMode.Ignore };
            ripple.AddToClassList("demo-click-ripple");
            _demoCursor.Add(ripple);
            _screen.Add(_demoCursor);
        }

        private static void SetHighlight(VisualElement element, bool active)
        {
            if (element != null) element.EnableInClassList("demo-highlight", active);
        }

        private SwarmAgentController FindAgent(int id)
        {
            foreach (var agent in executor.Agents) if (agent.AgentId == id) return agent;
            return null;
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
            _policy.text = executor.IsLocalMissionSelected ? "LOCAL · " + executor.ActivePolicyName : executor.ActivePolicyName;
            RefreshUnitCards();
            var activePolicy = executor.ActivePolicyName;
            _patrol.EnableInClassList("policy-active", activePolicy == nameof(PerimeterPatrolPolicy));
            _cordon.EnableInClassList("policy-active", activePolicy == nameof(HazardQuarantinePolicy) || activePolicy == nameof(HybridCordonPatrolBaselinePolicy));
            _search.EnableInClassList("policy-active", activePolicy == nameof(GridSearchAndRescuePolicy));
            _hold.EnableInClassList("policy-active", activePolicy == nameof(EmergencyHoldPolicy));
            _reason.text = Time.unscaledTime < _feedbackUntil ? _feedback : executor.IsLocalMissionSelected
                ? executor.LocalExecutionStatus : executor.FailureCategory ?? "Validated controls and current observations";
            var control = executor.Control;
            _brain.text = "Brain / " + (control?.BrainStatus ?? "awaiting backend") + (control == null ? "" : " / attempt " + control.GenerationAttempts);
            _model.text = "Model / " + (control?.ModelIdUsed ?? "none") + (control?.UsedModelFallback == true ? " / 404 fallback" : "");
            var readiness = visuals.Readiness;
            _modelSam.text = readiness == null ? "SAM 3.1\nSTATUS OFFLINE" : "SAM 3.1\n" +
                (readiness.MetaApiKeyConfigured ? "META KEY SET" : "META KEY MISSING");
            _modelSparkOpen.text = readiness == null ? "MUSE SPARK OPEN\nSTATUS OFFLINE" : "MUSE SPARK OPEN\n" +
                (readiness.BasetenApiKeyConfigured ? "BASETEN KEY SET" : "BASETEN KEY MISSING") +
                (control?.ModelIdUsed == null ? "" : " · " + (control.UsedModelFallback ? "FALLBACK ID" : "MODEL ACTIVE"));
            var metaStatus = readiness == null ? "BACKEND OFFLINE" : readiness.MetaApiKeyConfigured ? "META KEY SET · NOT PROBED" : "META KEY MISSING";
            _modelSemantic.text = "MUSE SPARK 1.3\n" + metaStatus;
            _modelVoice.text = "MUSE VOICE\n" + metaStatus;
            _modelImage.text = "MUSE IMAGE\n" + metaStatus;
            var verifiedBrainActive = executor.IsVerifiedBrainPolicyActive;
            _modelSandbox.text = readiness == null ? "ROSLYN + APPCONTAINER\nSTATUS OFFLINE" : "ROSLYN + APPCONTAINER\n" +
                (verifiedBrainActive ? "ACTIVE · VERIFIED" : (readiness.PolicyWorkerPresent ? "WORKER READY" : "WORKER MISSING") + " · " +
                (readiness.WindowsAppContainerSupported ? "ISOLATION READY" : "ISOLATION N/A"));
            _modelSandbox.EnableInClassList("model-active", verifiedBrainActive);
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
            _useTranscript.SetEnabled(true);
            _useTranscript.tooltip = draft != null && draft.Transcript.Length > 0
                ? "Copy the latest Muse Voice transcript into the mission instruction."
                : "No transcript yet. Click to see the next step, or enter the instruction as text.";
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
            if (semantics != null && !ReferenceEquals(semantics, _lastSemantics)) _lastSemantics = semantics;
            RefreshSemanticTrackChoices(current ? state.Message.World.Obstacles : null);
            var tag = SelectedTag(); var selectedTrackId = SelectedTrackId();
            _semanticSelected.text = tag == null
                ? selectedTrackId.HasValue ? "#" + selectedTrackId.Value.ToString(CultureInfo.InvariantCulture) + " · live track · no model tag" : "No current track selected"
                : "#" + tag.TrackId + " / " + tag.Status + (tag.OperatorOverride ? " / operator" : " / model") +
                    (tag.Classification == null ? "" : "\n" + tag.Classification.Label + " · " + tag.Classification.Role + " · " + tag.Classification.HazardLevel) +
                    (tag.FailureCategory == null ? "" : "\n" + tag.FailureCategory);
            _semanticTrack.SetEnabled(_semanticTrack.choices.Count > 0);
            _loadSemantic.SetEnabled(selectedTrackId.HasValue);
            _applySemantic.SetEnabled(selectedTrackId.HasValue && !visuals.IsEditing);
            _clearSemantic.SetEnabled(selectedTrackId.HasValue && !visuals.IsEditing);
            _generateScenery.SetEnabled(true); _generateScenery.text = visuals.IsGenerating ? "REPLACE SURFACE JOB" : "GENERATE SURFACE (META API)"; _sceneryStatus.text = visuals.SceneryStatus;
            RefreshOperationStatus();
        }

        private void Update()
        {
            var delta = Mathf.Max(Time.unscaledDeltaTime, 0.0001f);
            _frameTimeEma += (delta - _frameTimeEma) * (1 - Mathf.Exp(-5 * delta));
            if (_operationProgress != null)
                _operationProgress.value = Mathf.MoveTowards(_operationProgress.value, _operationProgressTarget, delta * 180f);
        }

        private void VoiceStarted()
        {
            _instructionBeforeVoice = _instruction.value;
            _voiceRecordingStartedAt = Time.unscaledTime;
            BeginOperation("VOICE RECORDING", "RECORDING VOICE · max 5 s", 5);
        }
        private void RecordVoice()
        {
            BeginOperation("VOICE RECORDING", media.IsRecording ? "Stopping capture and submitting audio for transcription" : "Starting microphone capture · max 5 s", media.IsRecording ? 80 : 3);
            media.ToggleRecording();
            if (media.IsTranscribing) UpdateOperation("TRANSCRIBING AUDIO · waiting for the real provider response", 86);
            else if (!media.IsRecording && string.IsNullOrWhiteSpace(media.Transcript?.Transcript))
                CompleteOperation("VOICE NOT STARTED · " + media.VoiceStatus, false);
        }
        private void CancelVoice()
        {
            BeginOperation("VOICE CANCEL", "Cancelling microphone request", 40);
            media.CancelVoice();
            CompleteOperation("VOICE CAPTURE CANCELLED", true);
        }
        private void UseTranscript()
        {
            var value = media.Transcript;
            if (value == null || value.Transcript.Length == 0) { BeginOperation("USE TRANSCRIPT", "No transcript is ready", 0); CompleteOperation("NO TRANSCRIPT · record and transcribe a voice command first", false); return; }
            _instruction.value = value.Transcript;
            BeginOperation("USE TRANSCRIPT", "Transcript copied to the mission instruction", 100);
            CompleteOperation("TRANSCRIPT READY IN MISSION FIELD · click SYNTHESIZE POLICY to send it", true);
        }
        private void TogglePreview()
        {
            BeginOperation("CAMERA PREVIEW", "Updating the camera preview", 65);
            media.SetPreviewEnabled(!media.PreviewEnabled);
            CompleteOperation(media.PreviewStatus, true);
        }
        private void ToggleMasks()
        {
            BeginOperation("MASK OVERLAY", "Updating the synchronized mask layer", 65);
            media.SetMasksEnabled(!media.MasksEnabled);
            CompleteOperation(media.MaskStatus, true);
        }
        private void ToggleLidar()
        {
            BeginOperation("LIDAR", "Updating LiDAR visualization", 65);
            SetLidarVisibility(!_lidarVisible);
            CompleteOperation("LiDAR beams " + (_lidarVisible ? "visible" : "hidden") + " · sensing remains active", true);
        }
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
        private int? SelectedTrackId() => int.TryParse(_semanticTrack?.value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : (int?)null;
        private void RefreshSemanticTrackChoices(IReadOnlyList<TrackedObstacleState> observedTracks)
        {
            var ids = new List<string>(128);
            if (observedTracks != null)
                foreach (var item in observedTracks)
                    if (item != null && item.Obstacle != null)
                        ids.Add(item.Obstacle.Id.ToString(CultureInfo.InvariantCulture));
            var identity = string.Join(",", ids);
            var previous = _semanticTrack.value;
            var choicesMatch = _semanticTrack.choices.Count == ids.Count;
            if (choicesMatch)
                for (var i = 0; i < ids.Count; i++)
                    if (_semanticTrack.choices[i] != ids[i]) { choicesMatch = false; break; }
            var sessionChanged = identity != _semanticChoiceSession;
            if (sessionChanged || !choicesMatch)
            {
                _semanticChoiceSession = identity;
                _semanticTrack.choices = ids;
            }
            var selected = ids.Contains(previous) ? previous : ids.Count > 0 ? ids[0] : string.Empty;
            if (_semanticTrack.value != selected) _semanticTrack.SetValueWithoutNotify(selected);
            _selectTrack1.SetEnabled(ids.Count > 0); _selectTrack2.SetEnabled(ids.Count > 1); _selectTrack3.SetEnabled(ids.Count > 2);
            _selectTrack1.text = ids.Count > 0 ? "SELECT #" + ids[0] : "SELECT #1";
            _selectTrack2.text = ids.Count > 1 ? "SELECT #" + ids[1] : "SELECT #2";
            _selectTrack3.text = ids.Count > 2 ? "SELECT #" + ids[2] : "SELECT #3";
            _selectTrack1.EnableInClassList("track-selected", ids.Count > 0 && selected == ids[0]);
            _selectTrack2.EnableInClassList("track-selected", ids.Count > 1 && selected == ids[1]);
            _selectTrack3.EnableInClassList("track-selected", ids.Count > 2 && selected == ids[2]);
            if (sessionChanged || !choicesMatch || selected != previous) LoadTag();
        }
        private void SelectTrack1() => SelectTrackButton(0);
        private void SelectTrack2() => SelectTrackButton(1);
        private void SelectTrack3() => SelectTrackButton(2);
        private void SelectTrackButton(int index)
        {
            BeginOperation("SELECT TRACK", "Selecting a current obstacle track", 65);
            if (index < 0 || index >= _semanticTrack.choices.Count) { CompleteOperation("TRACK NOT AVAILABLE · waiting for a current observation", false); return; }
            _semanticTrack.SetValueWithoutNotify(_semanticTrack.choices[index]);
            LoadTag();
            BeginOperation("SELECT TRACK", "Selected live obstacle track #" + _semanticTrack.choices[index], 100);
            CompleteOperation("TRACK SELECTED · local semantic edits are available", true);
            _selectTrack1.EnableInClassList("track-selected", index == 0);
            _selectTrack2.EnableInClassList("track-selected", index == 1);
            _selectTrack3.EnableInClassList("track-selected", index == 2);
        }
        private void LoadTag()
        {
            var id = SelectedTrackId(); var model = SelectedTag()?.Classification;
            _semanticLabel.value = model?.Label ?? (id.HasValue ? "Obstacle #" + id.Value.ToString(CultureInfo.InvariantCulture) : "");
            _semanticRole.value = model?.Role ?? "obstacle";
            _semanticHazard.value = model?.HazardLevel ?? "none";
            if (id.HasValue)
            {
                BeginOperation("LOAD SEMANTIC TAG", model == null ? "No model tag for track #" + id.Value + " · showing editable local defaults" : "Loaded model tag for track #" + id.Value, 100);
                CompleteOperation(model == null ? "LOCAL EDITABLE DEFAULT LOADED · no model classification is claimed" : "MODEL TAG LOADED · " + model.Label + " / " + model.Role, true);
            }
            else { BeginOperation("LOAD SEMANTIC TAG", "No current track is selected", 0); CompleteOperation("NO TRACK SELECTED · choose a current observed track first", false); }
        }
        private void ApplyTag()
        {
            var id = SelectedTrackId(); if (!id.HasValue) { BeginOperation("APPLY OPERATOR TAG", "No current track is selected", 0); CompleteOperation("NO TRACK SELECTED · current track list is empty", false); return; }
            BeginOperation("APPLY OPERATOR TAG", "Applying local visualization and planning override to track #" + id.Value, 55);
            try
            {
                var classification = new ObjectSemantics(_semanticLabel.value, _semanticRole.value, _semanticHazard.value, 1);
                var localHazardApplied = ApplyLocalHazardOverride(id.Value, classification.IsHazardZone);
                var apiQueued = visuals.TryApplyOverride(id.Value, classification);
                _visualFeedback = apiQueued ? "Operator tag submitted to backend" : "Local operator tag only · semantic backend unavailable";
                _visualFeedbackUntil = Time.unscaledTime + 5;
                CompleteOperation((localHazardApplied ? "LOCAL TRACK #" : "TRACK #") + id.Value + " · " + (classification.IsHazardZone ? "HAZARD VISUAL" : "OBSTACLE TAG") +
                    (apiQueued ? " · backend update queued" : " · backend unavailable; local override only"), localHazardApplied);
            }
            catch (ArgumentException) { _visualFeedback = "Enter a label and valid simulation tags"; _visualFeedbackUntil = Time.unscaledTime + 4; CompleteOperation(_visualFeedback, false); }
        }
        private void ClearTag()
        {
            var id = SelectedTrackId(); if (!id.HasValue) { BeginOperation("USE MODEL TAG", "No current track is selected", 0); CompleteOperation("NO TRACK SELECTED", false); return; }
            BeginOperation("USE MODEL TAG", "Restoring the model classification for track #" + id.Value, 55);
            var model = SelectedTag()?.Classification;
            var localApplied = ApplyLocalHazardOverride(id.Value, model?.IsHazardZone == true);
            var apiQueued = visuals.TryApplyOverride(id.Value, null);
            CompleteOperation((localApplied ? "LOCAL TRACK #" : "TRACK #") + id.Value + " · " +
                (model == null ? "no model classification; hazard visual cleared" : "model tag restored · " + model.Role + " / " + model.HazardLevel) +
                (apiQueued ? " · backend update queued" : " · backend unavailable; local view updated where possible"), localApplied);
        }
        private bool ApplyLocalHazardOverride(int trackId, bool isHazard)
        {
            var planningApplied = executor != null && executor.SetLocalHazardOverride(trackId, isHazard);
            if (_scenePresentation == null) _scenePresentation = UnityEngine.Object.FindAnyObjectByType<TacticalScenePresentation>();
            if (_scenePresentation != null) return planningApplied && _scenePresentation.SetOperatorHazardOverride(trackId, isHazard);
            var spawner = UnityEngine.Object.FindAnyObjectByType<DynamicObstacleSpawner>();
            return planningApplied && spawner != null && spawner.SetOperatorHazardOverride(trackId, isHazard);
        }
        private void GenerateScenery()
        {
            BeginOperation("MUSE IMAGE", "Applying a visible local surface baseline", 15);
            try
            {
                var prompt = _sceneryPrompt.value;
                var preset = prompt.IndexOf("mars", StringComparison.OrdinalIgnoreCase) >= 0 ? "MARS CRATER" :
                    prompt.IndexOf("blueprint", StringComparison.OrdinalIgnoreCase) >= 0 || prompt.IndexOf("neon", StringComparison.OrdinalIgnoreCase) >= 0 ? "NEON BLUEPRINT" : "INDUSTRIAL STEEL";
                visuals.ApplyLocalSurfacePreset(preset);
                UpdateOperation("LOCAL " + preset + " preview applied · checking real Muse Image readiness", 25);
                if (visuals.Readiness?.MetaApiKeyConfigured != true)
                {
                    CompleteOperation("LOCAL STYLE ACTIVE · " + preset + " · Muse Image was not called (Meta key/backend unavailable)", true);
                    return;
                }
                visuals.GenerateSurface(prompt);
                UpdateOperation("MUSE IMAGE REQUEST SENT · local " + preset + " remains visible while it runs", 45);
            }
            catch (ArgumentException) { _visualFeedback = "Enter a single-line surface description (1–1000 characters)"; _visualFeedbackUntil = Time.unscaledTime + 4; CompleteOperation(_visualFeedback, false); }
            catch (InvalidOperationException error) { CompleteOperation("LOCAL SURFACE FAILED · " + error.Message, false); }
        }
        private void ResetScenery()
        {
            BeginOperation("RESTORE SURFACE", "Restoring the authored default URP surface", 60);
            visuals.RestoreDefault();
            CompleteOperation("DEFAULT TACTICAL SURFACE RESTORED", true);
        }
        private void ApplySteelSurface() => ApplySurfacePreset("INDUSTRIAL STEEL");
        private void ApplyMarsSurface() => ApplySurfacePreset("MARS CRATER");
        private void ApplyBlueprintSurface() => ApplySurfacePreset("NEON BLUEPRINT");
        private void ApplySurfacePreset(string preset)
        {
            BeginOperation("LOCAL SURFACE STYLE", "Applying " + preset, 55);
            try
            {
                visuals.ApplyLocalSurfacePreset(preset);
                CompleteOperation("LOCAL STYLE ACTIVE · " + preset + " · no Muse Image request sent", true);
            }
            catch (InvalidOperationException error) { CompleteOperation("SURFACE STYLE FAILED · " + error.Message, false); }
        }

        private void Mission(SwarmMissionKind kind, string instruction, bool brain = false, float radiusMeters = 3f)
        {
            _selectedMission = kind;
            var name = brain ? "BRAIN SYNTHESIS" : kind.ToString().ToUpperInvariant();
            BeginOperation(name, brain ? "[1/3] Sending mission request · local built-in baseline remains supervised" : "Selecting local " + kind + " policy", brain ? 15 : 30);
            try
            {
                executor.RequestMission(kind, instruction, brain, radiusMeters);
                _feedback = brain ? "Local baseline active while model verification is pending." : "Local policy selected: " + kind;
                if (brain) UpdateOperation("[1/3] Model request queued · local " + executor.ActivePolicyName + " fallback selected", 25);
                else CompleteOperation("COMMAND ACCEPTED · " + executor.LocalExecutionStatus, true);
            }
            catch (Exception error) when (error is ArgumentException || error is InvalidOperationException)
            {
                _feedback = "Mission rejected: " + error.GetType().Name;
                CompleteOperation("MISSION NOT ACCEPTED · " + error.Message, false);
            }
            _feedbackUntil = Time.unscaledTime + 4;
        }
        private void Patrol() => Mission(SwarmMissionKind.PatrolPerimeter, "Patrol the workspace perimeter with all available agents.");
        private void Cordon() => Mission(SwarmMissionKind.HazardQuarantine, "Establish a 2.8m safety cordon around the highest-priority hazard.", false, 2.8f);
        private void Search() => Mission(SwarmMissionKind.GridSearchAndRescue, "Systematically search the free workspace cells.");
        private void Hold() => Mission(SwarmMissionKind.HoldPosition, "Immediately hold every agent at its current measured position.");
        private void Generate()
        {
            var instruction = _instruction.value;
            var kind = ResolveBrainMissionKind(instruction, _selectedMission);
            var radius = kind == SwarmMissionKind.HazardQuarantine ? 2.8f : 3f;
            Mission(kind, instruction, true, radius);
        }

        private static SwarmMissionKind ResolveBrainMissionKind(string instruction, SwarmMissionKind selectedMode)
        {
            var text = instruction ?? string.Empty;
            if (ContainsAny(text, "hold", "stop", "freeze", "stay in place")) return SwarmMissionKind.HoldPosition;
            if (ContainsAny(text, "grid search", "search and rescue", "search for", "find target", "find the target"))
                return SwarmMissionKind.GridSearchAndRescue;
            if (ContainsAny(text, "cordon", "quarantine", "isolate", "hazard zone", "safety ring", "safety perimeter around"))
                return SwarmMissionKind.HazardQuarantine;
            if (ContainsAny(text, "patrol", "orbit", "outer perimeter")) return SwarmMissionKind.PatrolPerimeter;
            return selectedMode;
        }

        private static bool ContainsAny(string text, params string[] terms)
        {
            foreach (var term in terms)
                if (text.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private void BuildUnitCards()
        {
            _unitCards.Clear();
            _unitCardsRoot.Clear();
            foreach (var agent in executor.Agents)
            {
                var telemetry = agent.ReadTelemetry();
                var card = new VisualElement();
                card.AddToClassList("unit-card");
                if (telemetry.Role == AgentRole.AerialDrone) card.AddToClassList("aerial-card");
                var header = new VisualElement(); header.AddToClassList("unit-card-header");
                var title = new Label("UNIT #" + telemetry.AgentId.ToString("D2", CultureInfo.InvariantCulture) +
                    (telemetry.Role == AgentRole.GroundVehicle ? " · GROUND AGV" : " · AERIAL UAV"));
                title.AddToClassList("unit-card-title"); title.enableRichText = false;
                var status = new Label(); status.AddToClassList("unit-status"); status.enableRichText = false;
                var movement = new Label(); movement.AddToClassList("unit-card-detail"); movement.enableRichText = false;
                var lidar = new Label(); lidar.AddToClassList("unit-card-detail"); lidar.enableRichText = false;
                header.Add(title); header.Add(status); card.Add(header); card.Add(movement); card.Add(lidar); _unitCardsRoot.Add(card);
                _unitCards.Add(new UnitCardView(agent, status, movement, lidar));
            }
        }

        private void RefreshUnitCards()
        {
            for (var i = 0; i < _unitCards.Count; i++)
            {
                var item = _unitCards[i];
                var telemetry = item.Agent.ReadTelemetry();
                var speed = Mathf.Sqrt(telemetry.CurrentVelocity.X * telemetry.CurrentVelocity.X +
                    telemetry.CurrentVelocity.Z * telemetry.CurrentVelocity.Z);
                item.Movement.text = "Speed " + speed.ToString("F1", CultureInfo.InvariantCulture) + " m/s   ·   Battery " +
                    (telemetry.BatteryLevel * 100).ToString("F0", CultureInfo.InvariantCulture) + "%";
                var sensor = telemetry.Lidar;
                item.Lidar.text = sensor == null ? "LiDAR unavailable" : sensor.AgeMilliseconds > 250 ? "LiDAR stale · " +
                    sensor.AgeMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms" : sensor.MinimumDistanceMeters.HasValue ?
                    "LiDAR " + sensor.MinimumDistanceMeters.Value.ToString("F1", CultureInfo.InvariantCulture) + " m · " +
                    sensor.HitCount.ToString(CultureInfo.InvariantCulture) + " hits" : "LiDAR clear to " +
                    sensor.RangeMeters.ToString("F0", CultureInfo.InvariantCulture) + " m · 0 hits";
                var label = StatusLabel(telemetry.Status);
                item.Status.text = label;
                item.Status.EnableInClassList("status-navigating", telemetry.Status == AgentStatus.Navigating);
                item.Status.EnableInClassList("status-holding", telemetry.Status == AgentStatus.Holding);
                item.Status.EnableInClassList("status-idle", telemetry.Status == AgentStatus.Idle);
                item.Status.EnableInClassList("status-lowbattery", telemetry.Status == AgentStatus.LowBattery);
                item.Status.EnableInClassList("status-offline", telemetry.Status == AgentStatus.Offline);
            }
        }

        private static string StatusLabel(AgentStatus status) => status switch
        {
            AgentStatus.Navigating => "NAVIGATING",
            AgentStatus.Holding => "HOLDING",
            AgentStatus.Idle => "IDLE",
            AgentStatus.LowBattery => "LOW BATTERY",
            _ => "OFFLINE"
        };

        private sealed class UnitCardView
        {
            internal UnitCardView(SwarmAgentController agent, Label status, Label movement, Label lidar)
            { Agent = agent; Status = status; Movement = movement; Lidar = lidar; }
            internal readonly SwarmAgentController Agent;
            internal readonly Label Status, Movement, Lidar;
        }

        private void Reconnect()
        {
            BeginOperation("RECONNECT STREAM", "Reopening the local world stream", 15);
            stream.Reconnect();
            Refresh();
            CompleteOperation("RECONNECT REQUESTED · waiting for a current camera or replay observation", true);
        }
        private void ShowBrainTab() { ShowDockTab(_brainTab, _brainView); BeginOperation("BRAIN & VOICE TAB", "Mission and voice controls are ready", 100); CompleteOperation("BRAIN & VOICE CONTROLS READY", true); }
        private void ShowSemanticsTab() { ShowDockTab(_semanticsTab, _semanticsView); BeginOperation("OBJECT SEMANTICS TAB", "Select a current track to edit its local hazard visualization", 100); CompleteOperation("OBJECT SEMANTICS CONTROLS READY", true); }
        private void ShowSceneryTab() { ShowDockTab(_sceneryTab, _sceneryView); BeginOperation("MUSE IMAGE TAB", "Local scene styles are available without a provider request", 100); CompleteOperation("SCENERY CONTROLS READY", true); }
        private void ShowDockTab(Button selected, VisualElement activeView)
        {
            _brainTab?.EnableInClassList("selected", selected == _brainTab);
            _semanticsTab?.EnableInClassList("selected", selected == _semanticsTab);
            _sceneryTab?.EnableInClassList("selected", selected == _sceneryTab);
            _brainView?.EnableInClassList("active", activeView == _brainView);
            _semanticsView?.EnableInClassList("active", activeView == _semanticsView);
            _sceneryView?.EnableInClassList("active", activeView == _sceneryView);
        }
        private void ViewIsometric() { BeginOperation("CAMERA · ISO", "Framing the complete tactical workspace", 100); _cameraController.SetPreset(1, null); CompleteOperation("ISOMETRIC VIEW ACTIVE", true); }
        private void ViewMap() { BeginOperation("CAMERA · MAP", "Switching to the top-down workspace view", 100); _cameraController.SetPreset(2, null); CompleteOperation("MAP VIEW ACTIVE", true); }
        private void ViewChase()
        {
            BeginOperation("CAMERA · CHASE", "Following the selected swarm unit", 60);
            var selected = SelectedCameraAgent();
            if (selected == null) { _visualFeedback = "Select a valid swarm unit for chase view"; _visualFeedbackUntil = Time.unscaledTime + 4; CompleteOperation(_visualFeedback, false); return; }
            _cameraController.SetPreset(3, selected);
            CompleteOperation("CHASE VIEW ACTIVE · AGENT #" + selected.AgentId.ToString(CultureInfo.InvariantCulture), true);
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
            if (_selectTrack1 != null) _selectTrack1.clicked -= SelectTrack1; if (_selectTrack2 != null) _selectTrack2.clicked -= SelectTrack2; if (_selectTrack3 != null) _selectTrack3.clicked -= SelectTrack3;
            if (_steelSurface != null) _steelSurface.clicked -= ApplySteelSurface; if (_marsSurface != null) _marsSurface.clicked -= ApplyMarsSurface; if (_blueprintSurface != null) _blueprintSurface.clicked -= ApplyBlueprintSurface;
            if (_loadSemantic != null) _loadSemantic.clicked -= LoadTag; if (_applySemantic != null) _applySemantic.clicked -= ApplyTag; if (_clearSemantic != null) _clearSemantic.clicked -= ClearTag;
            if (_generateScenery != null) _generateScenery.clicked -= GenerateScenery; if (_resetScenery != null) _resetScenery.clicked -= ResetScenery;
            _semanticTrack?.UnregisterValueChangedCallback(SelectSemanticTrack);
            _screen?.UnregisterCallback<GeometryChangedEvent>(Resize);
        }
        private static T Required<T>(VisualElement root, string name) where T : VisualElement =>
            root.Q<T>(name) ?? throw new InvalidOperationException("Missing HUD element: " + name);
    }
}
