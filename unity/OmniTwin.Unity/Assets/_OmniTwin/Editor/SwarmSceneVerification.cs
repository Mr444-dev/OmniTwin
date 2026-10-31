using System;
using System.Collections.Generic;
using System.Diagnostics;
using OmniTwin.Contracts;
using OmniTwin.Policies;
using OmniTwin.Simulation.Agents;
using OmniTwin.Simulation.Runtime;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;

namespace OmniTwin.Simulation.Editor
{
    /// <summary>Exercises actual controllers, paths and carving using a clearly identified temporary navigation fixture.</summary>
    public static class SwarmSceneVerification
    {
        private static SwarmAgentController[] _agents;
        private static Vector3[] _starts;
        private static int[] _maximumPaths;
        private static PolicyExecutor _executor;
        private static GameObject _fixture;
        private static PerimeterPatrolPolicy _policy;
        private static Stopwatch _clock;
        private static bool _wasEnabled;
        private static long _sequence;
        private static int _firstFrame, _ticks;
        private static float _next;
        private static string _failure;
        private static string _completed;

        /// <summary>Starts a bounded navigation-only check. It never injects a camera observation into the live stream.</summary>
        public static void Begin()
        {
            if (!EditorApplication.isPlaying || _clock != null) throw new InvalidOperationException("Enter Play mode and complete the previous verification first.");
            var executors = UnityEngine.Object.FindObjectsByType<PolicyExecutor>();
            if (executors.Length != 1 || executors[0].Agents.Count != 5) throw new InvalidOperationException("The authored five-agent scene is required.");
            _executor = executors[0]; _wasEnabled = _executor.enabled; _executor.enabled = false;
            _agents = new SwarmAgentController[5]; _starts = new Vector3[5]; _maximumPaths = new int[5];
            for (var i = 0; i < 5; i++) { _agents[i] = _executor.Agents[i]; _starts[i] = _agents[i].transform.position; }
            _fixture = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            _fixture.name = "Navigation verification fixture - not a SAM observation";
            _fixture.transform.position = new Vector3(-8.15f, 0.5f, -4.275f); _fixture.transform.localScale = new Vector3(1.1f, 0.5f, 1.1f);
            var obstacle = _fixture.AddComponent<NavMeshObstacle>(); obstacle.shape = NavMeshObstacleShape.Capsule;
            obstacle.radius = 0.5f; obstacle.height = 2; obstacle.carving = true; obstacle.carveOnlyStationary = false;
            _policy = new PerimeterPatrolPolicy(WorkspaceBounds.TacticalDesk);
            _clock = Stopwatch.StartNew(); _firstFrame = Time.frameCount; _ticks = 0; _sequence = 0; _failure = null; _completed = null; _next = 0;
            EditorApplication.update += Tick;
        }

        private static void Tick()
        {
            if (_clock == null) return;
            if (!EditorApplication.isPlaying || _clock.Elapsed.TotalSeconds > 20) { _failure = "verification_lifetime_exceeded"; Restore(); return; }
            if (_clock.Elapsed.TotalSeconds >= 8) { try { _completed = Finish(); } catch (Exception error) { _failure = error.Message; Restore(); } return; }
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 0.1f; _ticks++;
            try
            {
                var telemetry = new List<AgentTelemetry>(); foreach (var agent in _agents) telemetry.Add(agent.ReadTelemetry());
                var position = _fixture.transform.position;
                var footprint = new SegmentedObstacle(1, "authored_navigation_fixture", new Vector2D(position.x, position.z), 0.55f, 1, false);
                var world = new WorldStateSnapshot(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), new[] { footprint }, telemetry, "navigation_verification", "", _sequence++);
                var directives = _policy.ComputeDirectives(world, 0);
                foreach (var directive in directives) foreach (var agent in _agents) if (agent.AgentId == directive.AgentId) agent.ApplyDirective(directive);
                for (var i = 0; i < _agents.Length; i++) _maximumPaths[i] = Math.Max(_maximumPaths[i], _agents[i].GetComponentInChildren<LineRenderer>().positionCount);
                foreach (var agent in _agents)
                    if (agent.PlanarPosition.DistanceTo(footprint.WorldPosition) < 0.88) throw new InvalidOperationException("Measured entity overlaps the expanded fixture.");
            }
            catch (Exception error) { _failure = error.GetType().Name + ": " + error.Message; Restore(); }
        }

        /// <summary>Checks actual frame progression and all five displacements, then restores live fail-safe control.</summary>
        public static string Finish()
        {
            if (_clock == null) return _completed ?? throw new InvalidOperationException(_failure ?? "No navigation verification is running.");
            var elapsed = _clock.Elapsed.TotalSeconds; var frames = Time.frameCount - _firstFrame;
            var moved = new double[5]; var paths = new int[5];
            for (var i = 0; i < 5; i++)
            { moved[i] = Vector3.Distance(_agents[i].transform.position, _starts[i]); paths[i] = _maximumPaths[i]; }
            Restore();
            if (_failure != null || elapsed < 1 || frames < 10 || _ticks < 5) throw new InvalidOperationException(_failure ?? "The simulation did not advance sufficiently.");
            foreach (var distance in moved) if (distance < 0.1) throw new InvalidOperationException("A configured simulation entity did not move.");
            foreach (var count in paths) if (count < 2) throw new InvalidOperationException("A moving entity did not render a path.");
            return "frames=" + frames + "; ticks=" + _ticks + "; moved=" + string.Join(",", moved) + "; maximumPathVertices=" + string.Join(",", paths);
        }
        private static void Restore()
        {
            EditorApplication.update -= Tick;
            if (_agents != null) foreach (var agent in _agents) if (agent != null) agent.Hold();
            if (_fixture != null) UnityEngine.Object.Destroy(_fixture);
            if (_executor != null) _executor.enabled = _wasEnabled;
            _clock = null;
        }
    }
}
