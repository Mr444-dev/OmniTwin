using OmniTwin.Contracts;
using UnityEngine;
using UnityEngine.Rendering;

namespace OmniTwin.Simulation.Agents
{
    /// <summary>Animates cached parts of a procedural AGV or quadcopter without per-frame allocations.</summary>
    [DisallowMultipleComponent]
    public sealed class SwarmRigAnimator : MonoBehaviour
    {
        private static readonly int BaseColor = Shader.PropertyToID("_BaseColor");
        private static readonly int EmissionColor = Shader.PropertyToID("_EmissionColor");
        [SerializeField] private SwarmAgentController agent;
        [SerializeField] private AgentRole role;
        [SerializeField] private Transform bodyPivot;
        [SerializeField] private Transform lidarTurret;
        [SerializeField] private Transform[] propellers = new Transform[0];
        [SerializeField] private Renderer[] modeLights = new Renderer[0];
        private MaterialPropertyBlock _lightBlock;
        private float _nextLightRefresh;

        /// <summary>Assigns authored rig parts once during scene creation.</summary>
        public void Configure(SwarmAgentController owner, AgentRole agentRole, Transform pivot, Transform turret,
            Transform[] rotors, Renderer[] leds)
        {
            agent = owner; role = agentRole; bodyPivot = pivot; lidarTurret = turret;
            if (_lightBlock == null) _lightBlock = new MaterialPropertyBlock();
            propellers = rotors ?? new Transform[0]; modeLights = leds ?? new Renderer[0];
        }

        private void Awake()
        {
            EnsureRuntimeState();
            if (agent == null) agent = GetComponent<SwarmAgentController>();
            if (agent == null || agent.Role != role || bodyPivot == null)
                throw new System.InvalidOperationException("A swarm rig requires its matching configured controller and pivot.");
            foreach (var rotor in propellers) if (rotor == null) throw new System.InvalidOperationException("A drone rotor reference is missing.");
            foreach (var light in modeLights) if (light == null) throw new System.InvalidOperationException("A mode LED reference is missing.");
        }

        private void OnEnable() => EnsureRuntimeState();

        private void EnsureRuntimeState()
        {
            if (_lightBlock == null) _lightBlock = new MaterialPropertyBlock();
            if (agent == null) agent = GetComponent<SwarmAgentController>();
        }

        private void Update()
        {
            // Unity can recreate native-backed per-instance state after script reloads without
            // reconstructing the component. Recover once here; this is not a per-frame allocation.
            if (_lightBlock == null) _lightBlock = new MaterialPropertyBlock();
            if (agent == null) agent = GetComponent<SwarmAgentController>();
            if (agent == null) { enabled = false; return; }
            if (role == AgentRole.GroundVehicle)
            {
                if (lidarTurret != null) lidarTurret.Rotate(0, 112 * Time.deltaTime, 0, Space.Self);
            }
            else
            {
                var speed = agent.MeasuredVelocity;
                var target = Quaternion.Euler(Mathf.Clamp(-speed.z * 5, -18, 18), 0, Mathf.Clamp(speed.x * 5, -18, 18));
                bodyPivot.localRotation = Quaternion.Slerp(bodyPivot.localRotation, target, 1 - Mathf.Exp(-5 * Time.deltaTime));
                for (var i = 0; i < propellers.Length; i++) propellers[i].Rotate(0, (i & 1) == 0 ? 1250 * Time.deltaTime : -1250 * Time.deltaTime, 0, Space.Self);
            }
            if (Time.unscaledTime < _nextLightRefresh) return;
            _nextLightRefresh = Time.unscaledTime + 0.1f;
            var color = ModeColor(agent.DirectiveStatusTag, agent.Status);
            _lightBlock.SetColor(BaseColor, color);
            _lightBlock.SetColor(EmissionColor, color * 2.4f);
            foreach (var light in modeLights) light.SetPropertyBlock(_lightBlock);
        }

        private static Color ModeColor(string tag, AgentStatus status)
        {
            if (status == AgentStatus.Holding || status == AgentStatus.LowBattery) return new Color(1f, 0.13f, 0.08f, 1);
            if (tag != null && (tag.IndexOf("cordon", System.StringComparison.OrdinalIgnoreCase) >= 0 || tag.IndexOf("hazard", System.StringComparison.OrdinalIgnoreCase) >= 0)) return new Color(1f, 0.57f, 0.12f, 1);
            if (tag != null && (tag.IndexOf("grid", System.StringComparison.OrdinalIgnoreCase) >= 0 || tag.IndexOf("search", System.StringComparison.OrdinalIgnoreCase) >= 0)) return new Color(0.15f, 1f, 0.49f, 1);
            return new Color(0.12f, 0.92f, 1f, 1);
        }
    }
}
