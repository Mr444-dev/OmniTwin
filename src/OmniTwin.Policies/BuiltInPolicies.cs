using System;
using System.Collections.Generic;
using OmniTwin.Contracts;

namespace OmniTwin.Policies;

/// <summary>Immediately issues zero-speed directives at each agent's measured current position.</summary>
public sealed class EmergencyHoldPolicy : ISwarmPolicy
{
    /// <inheritdoc />
    public string PolicyName => nameof(EmergencyHoldPolicy);
    /// <inheritdoc />
    public IReadOnlyList<AgentTargetDirective> ComputeDirectives(WorldStateSnapshot worldState, float timeSeconds)
    {
        var agents = PolicySupport.Agents(worldState, timeSeconds);
        var result = new List<AgentTargetDirective>(agents.Count);
        foreach (var agent in agents) result.Add(new AgentTargetDirective(agent.AgentId, agent.CurrentPosition, 0, "emergency_hold"));
        return result.AsReadOnly();
    }
}

/// <summary>Distributes ground and aerial units uniformly around an inset workspace perimeter.</summary>
public sealed class PerimeterPatrolPolicy : ISwarmPolicy
{
    private readonly WorkspaceBounds _bounds;
    private readonly float _lapSeconds, _speed;
    /// <summary>Configures a complete lap duration and maximum movement speed in world units.</summary>
    public PerimeterPatrolPolicy(WorkspaceBounds bounds, float lapSeconds = 60, float speed = 1.5f)
    { PolicySupport.Validate(bounds, speed); PolicySupport.Positive(lapSeconds, nameof(lapSeconds)); _bounds = bounds; _lapSeconds = lapSeconds; _speed = speed; }
    /// <inheritdoc />
    public string PolicyName => nameof(PerimeterPatrolPolicy);
    /// <inheritdoc />
    public IReadOnlyList<AgentTargetDirective> ComputeDirectives(WorldStateSnapshot worldState, float timeSeconds)
    {
        var agents = PolicySupport.Agents(worldState, timeSeconds); var result = new List<AgentTargetDirective>(agents.Count);
        var low = _bounds.Clamp(new Vector2D(_bounds.MinimumX, _bounds.MinimumZ), 0.7f);
        var high = _bounds.Clamp(new Vector2D(_bounds.MaximumX, _bounds.MaximumZ), 0.7f);
        var width = high.X - low.X; var depth = high.Z - low.Z; var perimeter = 2 * (width + depth);
        for (var i = 0; i < agents.Count; i++)
        {
            var distance = ((double)timeSeconds / _lapSeconds + (double)i / agents.Count) % 1 * perimeter;
            Vector2D goal;
            if (distance < width) goal = new Vector2D(low.X + (float)distance, low.Z);
            else if ((distance -= width) < depth) goal = new Vector2D(high.X, low.Z + (float)distance);
            else if ((distance -= depth) < width) goal = new Vector2D(high.X - (float)distance, high.Z);
            else goal = new Vector2D(low.X, high.Z - (float)(distance - width));
            result.Add(PolicySupport.Goal(agents[i], goal, _bounds, worldState.Obstacles, _speed, "perimeter_patrol"));
        }
        return SafeNavigation.ValidateDirectives(worldState, result, _bounds);
    }
}

/// <summary>Forms a clearance-preserving cordon around the largest designated hazard, or largest observed object.</summary>
public sealed class HazardQuarantinePolicy : ISwarmPolicy
{
    private readonly WorkspaceBounds _bounds;
    private readonly float _radius, _speed;
    /// <summary>Configures the requested cordon radius; obstacle and agent clearance can expand it.</summary>
    public HazardQuarantinePolicy(WorkspaceBounds bounds, float radiusMeters = 3, float speed = 1.5f)
    { PolicySupport.Validate(bounds, speed); PolicySupport.Positive(radiusMeters, nameof(radiusMeters)); _bounds = bounds; _radius = radiusMeters; _speed = speed; }
    /// <inheritdoc />
    public string PolicyName => nameof(HazardQuarantinePolicy);
    /// <inheritdoc />
    public IReadOnlyList<AgentTargetDirective> ComputeDirectives(WorldStateSnapshot worldState, float timeSeconds)
    {
        var agents = PolicySupport.Agents(worldState, timeSeconds); SegmentedObstacle? target = null;
        foreach (var obstacle in worldState.Obstacles)
            if (target is null || (obstacle.IsHazardZone && !target.IsHazardZone) ||
                (obstacle.IsHazardZone == target.IsHazardZone && (obstacle.RadiusMeters > target.RadiusMeters ||
                (obstacle.RadiusMeters == target.RadiusMeters && obstacle.Id < target.Id)))) target = obstacle;
        if (target is null) return new EmergencyHoldPolicy().ComputeDirectives(worldState, timeSeconds);
        var radius = Math.Max(_radius, target.RadiusMeters + SafeNavigation.ClearanceMeters + 0.1f);
        var result = new List<AgentTargetDirective>(agents.Count);
        for (var i = 0; i < agents.Count; i++)
        {
            var angle = 2 * Math.PI * i / agents.Count;
            var goal = new Vector2D(target.WorldPosition.X + radius * (float)Math.Cos(angle), target.WorldPosition.Z + radius * (float)Math.Sin(angle));
            result.Add(PolicySupport.Goal(agents[i], goal, _bounds, worldState.Obstacles, _speed, "hazard_cordon"));
        }
        return SafeNavigation.ValidateDirectives(worldState, result, _bounds);
    }
}

/// <summary>Partitions a serpentine search grid among stable agent IDs, advancing cells only after measured arrival.</summary>
public sealed class GridSearchAndRescuePolicy : ISwarmPolicy
{
    private readonly object _gate = new object();
    private readonly WorkspaceBounds _bounds;
    private readonly float _cellSize, _speed;
    private readonly Dictionary<int, SearchState> _progress = new Dictionary<int, SearchState>();
    /// <summary>Configures cell spacing and speed. Grid dimensions are bounded to 65 by 65.</summary>
    public GridSearchAndRescuePolicy(WorkspaceBounds bounds, float cellSizeMeters = 1.5f, float speed = 1.5f)
    { PolicySupport.Validate(bounds, speed); PolicySupport.Positive(cellSizeMeters, nameof(cellSizeMeters)); _bounds = bounds; _cellSize = cellSizeMeters; _speed = speed; }
    /// <inheritdoc />
    public string PolicyName => nameof(GridSearchAndRescuePolicy);
    /// <inheritdoc />
    public IReadOnlyList<AgentTargetDirective> ComputeDirectives(WorldStateSnapshot worldState, float timeSeconds)
    {
        var agents = PolicySupport.Agents(worldState, timeSeconds);
        lock (_gate)
        {
            var low = _bounds.Clamp(new Vector2D(_bounds.MinimumX, _bounds.MinimumZ), 0.7f);
            var high = _bounds.Clamp(new Vector2D(_bounds.MaximumX, _bounds.MaximumZ), 0.7f);
            var spacing = Math.Max(_cellSize, Math.Max(high.X - low.X, high.Z - low.Z) / 64);
            var columns = Math.Max(2, (int)Math.Floor((high.X - low.X) / spacing) + 1);
            var rows = Math.Max(2, (int)Math.Floor((high.Z - low.Z) / spacing) + 1);
            var cells = new List<Vector2D>(columns * rows);
            for (var row = 0; row < rows; row++) for (var index = 0; index < columns; index++)
            {
                var column = row % 2 == 0 ? index : columns - index - 1;
                cells.Add(new Vector2D(low.X + (high.X - low.X) * column / (columns - 1), low.Z + (high.Z - low.Z) * row / (rows - 1)));
            }
            var result = new List<AgentTargetDirective>(agents.Count);
            for (var rank = 0; rank < agents.Count; rank++)
            {
                var agent = agents[rank];
                if (!_progress.TryGetValue(agent.AgentId, out var state)) _progress[agent.AgentId] = state = new SearchState(rank % cells.Count);
                if (worldState.SequenceNumber > state.Sequence && agent.CurrentPosition.DistanceTo(cells[state.Index]) < 0.3)
                    state.Index = (state.Index + agents.Count) % cells.Count;
                state.Sequence = Math.Max(state.Sequence, worldState.SequenceNumber);
                var chosen = false;
                for (var tries = 0; tries < cells.Count; tries++)
                {
                    if (SafeNavigation.IsFree(cells[state.Index], _bounds, worldState.Obstacles)) { chosen = true; break; }
                    state.Index = (state.Index + 1) % cells.Count;
                }
                result.Add(chosen ? PolicySupport.Goal(agent, cells[state.Index], _bounds, worldState.Obstacles, _speed, "grid_search") :
                    new AgentTargetDirective(agent.AgentId, agent.CurrentPosition, 0, "no_free_cell"));
            }
            return SafeNavigation.ValidateDirectives(worldState, result, _bounds);
        }
    }
    private sealed class SearchState
    { internal SearchState(int index) => Index = index; internal int Index; internal long Sequence = -1; }
}

internal static class PolicySupport
{
    internal static void Positive(float value, string parameter)
    { if (float.IsNaN(value) || float.IsInfinity(value) || value <= 0) throw new ArgumentOutOfRangeException(parameter); }
    internal static void Validate(WorkspaceBounds bounds, float speed)
    { bounds.Clamp(default, 0.7f); Positive(speed, nameof(speed)); if (speed > 4) throw new ArgumentOutOfRangeException(nameof(speed)); }
    internal static List<AgentTelemetry> Agents(WorldStateSnapshot world, float seconds)
    {
        if (world is null) throw new ArgumentNullException(nameof(world));
        if (float.IsNaN(seconds) || float.IsInfinity(seconds) || seconds < 0 || world.Agents.Count > 128 || world.Obstacles.Count > 128)
            throw new ArgumentOutOfRangeException(nameof(seconds));
        var result = new List<AgentTelemetry>(world.Agents); result.Sort((a, b) => a.AgentId.CompareTo(b.AgentId)); return result;
    }
    internal static AgentTargetDirective Goal(AgentTelemetry agent, Vector2D goal, WorkspaceBounds bounds,
        IReadOnlyList<SegmentedObstacle> obstacles, float speed, string status)
    {
        if (agent.Status == AgentStatus.Offline || agent.BatteryLevel <= 0.1f)
            return new AgentTargetDirective(agent.AgentId, agent.CurrentPosition, 0, "agent_unavailable");
        goal = bounds.Clamp(goal, 0.7f);
        if (!SafeNavigation.IsFree(goal, bounds, obstacles))
        {
            var found = false;
            for (var ring = 1; ring <= 8 && !found; ring++) for (var direction = 0; direction < 16; direction++)
            {
                var angle = direction * Math.PI / 8;
                var candidate = new Vector2D(goal.X + ring * 0.5f * (float)Math.Cos(angle), goal.Z + ring * 0.5f * (float)Math.Sin(angle));
                if (!SafeNavigation.IsFree(candidate, bounds, obstacles)) continue;
                goal = candidate; found = true; break;
            }
            if (!found) return new AgentTargetDirective(agent.AgentId, agent.CurrentPosition, 0, "blocked_hold");
        }
        return new AgentTargetDirective(agent.AgentId, goal, speed, status);
    }
}
