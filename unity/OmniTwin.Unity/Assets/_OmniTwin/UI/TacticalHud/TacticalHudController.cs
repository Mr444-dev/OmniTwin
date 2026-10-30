using System;
using System.Globalization;
using OmniTwin.Simulation.Network;
using OmniTwin.Transport;
using UnityEngine;
using UnityEngine.UIElements;

namespace OmniTwin.Simulation.UI
{
    /// <summary>Shows actual stream state and observation freshness; absent observations are never shown as a clear scene.</summary>
    [RequireComponent(typeof(UIDocument)), DefaultExecutionOrder(100)]
    public sealed class TacticalHudController : MonoBehaviour
    {
        [SerializeField] private TwinWebSocketClient stream = null;
        private VisualElement _screen;
        private Label _badge, _connection, _tracks, _hazards, _age, _publication, _calibration, _notice;
        private Button _reconnect;
        private IVisualElementScheduledItem _refresh;

        private void OnEnable()
        {
            var root = GetComponent<UIDocument>().rootVisualElement;
            if (stream == null || root == null)
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
        }

        private void Reconnect() { stream.Reconnect(); Refresh(); }
        private void Resize(GeometryChangedEvent change) => _screen.EnableInClassList("compact", change.newRect.width < 1100);
        private void OnDisable()
        {
            _refresh?.Pause();
            if (_reconnect != null) _reconnect.clicked -= Reconnect;
            _screen?.UnregisterCallback<GeometryChangedEvent>(Resize);
        }
        private static T Required<T>(VisualElement root, string name) where T : VisualElement =>
            root.Q<T>(name) ?? throw new InvalidOperationException("Missing HUD element: " + name);
    }
}
