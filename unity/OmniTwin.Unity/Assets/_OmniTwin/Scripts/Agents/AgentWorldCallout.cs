using System.Globalization;
using OmniTwin.Contracts;
using UnityEngine;

namespace OmniTwin.Simulation.Agents
{
    /// <summary>Shows bounded agent identity and sampled telemetry above a rig at 10 Hz.</summary>
    [DisallowMultipleComponent]
    public sealed class AgentWorldCallout : MonoBehaviour
    {
        [SerializeField] private SwarmAgentController agent;
        [SerializeField] private float verticalOffset = 1.75f;
        private TextMesh _text;
        private Camera _camera;
        private float _nextUpdate;

        private void Awake()
        {
            if (agent == null) agent = GetComponentInParent<SwarmAgentController>();
            if (agent == null) throw new System.InvalidOperationException("An agent callout requires its owning controller.");
            _camera = Camera.main;
            var label = new GameObject("Agent tactical callout"); label.transform.SetParent(transform, false);
            var id = agent.ReadTelemetry().AgentId;
            var rowStagger = id % 2 == 0 ? 0.35f : 0f;
            label.transform.localPosition = Vector3.up * (verticalOffset + rowStagger);
            _text = label.AddComponent<TextMesh>(); _text.anchor = TextAnchor.LowerCenter; _text.alignment = TextAlignment.Center;
            _text.fontSize = 96; _text.characterSize = 0.075f; _text.richText = false;
            _text.color = agent.Role == AgentRole.GroundVehicle ? new Color(0.35f, 0.91f, 1f) : new Color(1f, 0.74f, 0.35f);
            _text.GetComponent<MeshRenderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        private void Update()
        {
            if (Time.unscaledTime >= _nextUpdate)
            {
                _nextUpdate = Time.unscaledTime + 0.1f;
                var telemetry = agent.ReadTelemetry();
                var speed = Mathf.Sqrt(telemetry.CurrentVelocity.X * telemetry.CurrentVelocity.X + telemetry.CurrentVelocity.Z * telemetry.CurrentVelocity.Z);
                _text.text = "#" + telemetry.AgentId.ToString("D2", CultureInfo.InvariantCulture) + " " +
                    (agent.Role == AgentRole.GroundVehicle ? "AGV" : "UAV") + " " + speed.ToString("F1", CultureInfo.InvariantCulture) + "m/s";
            }
            if (_camera == null) _camera = Camera.main;
            if (_camera != null) _text.transform.rotation = Quaternion.LookRotation(_text.transform.position - _camera.transform.position, _camera.transform.up);
        }
    }
}
