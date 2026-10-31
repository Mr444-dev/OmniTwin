using System;
using System.Globalization;
using OmniTwin.Simulation.Network;
using OmniTwin.Transport;
using UnityEngine;
using UnityEngine.UIElements;
using System.Text;
using OmniTwin.Contracts;
using OmniTwin.Simulation.Runtime;

namespace OmniTwin.Simulation.UI
{
    /// <summary>Shows actual stream state and observation freshness; absent observations are never shown as a clear scene.</summary>
    [RequireComponent(typeof(UIDocument)), DefaultExecutionOrder(100)]
    public sealed class TacticalHudController : MonoBehaviour
    {
        [SerializeField] private TwinWebSocketClient stream = null;
        [SerializeField] private PolicyExecutor executor = null;
        private VisualElement _screen;
        private Label _badge, _connection, _tracks, _hazards, _age, _publication, _calibration, _notice;
        private Button _reconnect;
        private IVisualElementScheduledItem _refresh;
        private Label _policy, _telemetry, _reason, _brain, _model, _compiler;
        private Button _patrol, _cordon, _search, _hold, _generate;
        private TextField _instruction;
        private SwarmMissionKind _selectedMission = SwarmMissionKind.PatrolPerimeter;
        private string _feedback;
        private float _feedbackUntil;

        private void OnEnable()
        {
            var root = GetComponent<UIDocument>().rootVisualElement;
            if (stream == null || executor == null || root == null)
                throw new InvalidOperationException("Tactical HUD needs its stream and an initialized UIDocument.");
            _screen = Required<VisualElement>(root, "screen");
            _badge = Required<Label>(root, "statusBadge");
            _connection = Required<Label>(root, "connectionState");
            _tracks = Required<Label>(root, "trackCount");
            _hazards = Required<Label>(root, "hazardCount");
            _age = Required<Label>(root, "sourceAge");
            _publication = Required<Label>(root, "publication");
            _calibration = Required<Label>(root, "calibration");
            _notice = Required<Label>(root, "sourceNotice");
            _reconnect = Required<Button>(root, "reconnectButton");
            _reconnect.clicked += Reconnect;
            _policy = Required<Label>(root, "activePolicy"); _telemetry = Required<Label>(root, "agentTelemetry");
            _reason = Required<Label>(root, "controlReason"); _brain = Required<Label>(root, "brainState");
            _model = Required<Label>(root, "modelIdentity"); _compiler = Required<Label>(root, "compilerDiagnostics");
            foreach (var label in new[] { _policy, _telemetry, _reason, _brain, _model, _compiler }) label.enableRichText = false;
            _patrol = Required<Button>(root, "patrolButton"); _cordon = Required<Button>(root, "cordonButton");
            _search = Required<Button>(root, "searchButton"); _hold = Required<Button>(root, "holdButton"); _generate = Required<Button>(root, "generateButton");
            _instruction = Required<TextField>(root, "missionInstruction");
            _patrol.clicked += Patrol; _cordon.clicked += Cordon; _search.clicked += Search; _hold.clicked += Hold; _generate.clicked += Generate;
            _screen.RegisterCallback<GeometryChangedEvent>(Resize);
            _refresh = root.schedule.Execute(Refresh).Every(100);
            Refresh();
        }

        private void Refresh()
        {
            var current = stream.TryGetCurrentView(out var view);
            var state = view.State;
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
            _notice.text = current ? "Geometry is scoped to this calibration and tracking session. Proxy height is an authored display setting."
                : state.FailureCategory != null ? "Stream status: " + state.FailureCategory + ". Configure calibrated perception, then reconnect."
                : "Waiting for a calibrated camera observation. An empty view does not mean the workspace is clear.";
            _policy.text = executor.ActivePolicyName;
            var measured = new StringBuilder();
            foreach (var agent in executor.Agents)
            {
                var telemetry = agent.ReadTelemetry();
                measured.Append(telemetry.AgentId).Append(telemetry.Role == AgentRole.GroundVehicle ? " AGV_GROUND  " : " DRONE_AERIAL  ")
                    .Append(telemetry.Status).Append("\n  (").Append(telemetry.CurrentPosition.X.ToString("F1", CultureInfo.InvariantCulture)).Append(", ")
                    .Append(telemetry.CurrentPosition.Z.ToString("F1", CultureInfo.InvariantCulture)).Append(")  ")
                    .Append((telemetry.BatteryLevel * 100).ToString("F0", CultureInfo.InvariantCulture)).Append("%\n");
            }
            _telemetry.text = measured.ToString();
            _reason.text = Time.unscaledTime < _feedbackUntil ? _feedback : executor.FailureCategory ?? "Validated controls and current observations";
            var control = executor.Control;
            _brain.text = "Brain / " + (control?.BrainStatus ?? "awaiting backend") + (control == null ? "" : " / attempt " + control.GenerationAttempts);
            _model.text = "Model / " + (control?.ModelIdUsed ?? "none") + (control?.UsedModelFallback == true ? " / 404 fallback" : "");
            var diagnostics = new StringBuilder(control?.BrainDiagnostic ?? "");
            if (control != null) foreach (var diagnostic in control.CompilationDiagnostics) diagnostics.Append("\n").Append(diagnostic);
            _compiler.text = diagnostics.ToString();
        }

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
        private void Resize(GeometryChangedEvent change) => _screen.EnableInClassList("compact", change.newRect.width < 1100);
        private void OnDisable()
        {
            _refresh?.Pause();
            if (_reconnect != null) _reconnect.clicked -= Reconnect;
            if (_patrol != null) _patrol.clicked -= Patrol; if (_cordon != null) _cordon.clicked -= Cordon;
            if (_search != null) _search.clicked -= Search; if (_hold != null) _hold.clicked -= Hold; if (_generate != null) _generate.clicked -= Generate;
            _screen?.UnregisterCallback<GeometryChangedEvent>(Resize);
        }
        private static T Required<T>(VisualElement root, string name) where T : VisualElement =>
            root.Q<T>(name) ?? throw new InvalidOperationException("Missing HUD element: " + name);
    }
}
