using System;
using System.Collections.Generic;
using OmniTwin.Contracts;

namespace OmniTwin.Policies;

/// <summary>Bounded deterministic navigation geometry shared by baseline policies and directive ingestion.</summary>
public static class SafeNavigation
{
    /// <summary>Radius of the authored agents, including additional separation from obstacle proxies.</summary>
    public const float ClearanceMeters = 0.55f;

    /// <summary>Tests a target disc against bounds and every observed circular footprint.</summary>
    public static bool IsFree(Vector2D point, WorkspaceBounds bounds, IReadOnlyList<SegmentedObstacle> obstacles)
    {
        if (!bounds.Contains(point, ClearanceMeters)) return false;
        foreach (var obstacle in obstacles) if (point.DistanceTo(obstacle.WorldPosition) < obstacle.RadiusMeters + ClearanceMeters) return false;
        return true;
    }

    /// <summary>Finds the nearest deterministic free grid point for correcting an authored spawn overlap.</summary>
    public static bool TryFindNearestFreePoint(Vector2D origin, WorkspaceBounds bounds,
        IReadOnlyList<SegmentedObstacle> obstacles, out Vector2D point, float gridStepMeters = 0.25f)
    {
        if (obstacles is null) throw new ArgumentNullException(nameof(obstacles));
        if (!Finite(origin.X) || !Finite(origin.Z) || !Finite(gridStepMeters) || gridStepMeters < 0.1f || gridStepMeters > 1f)
            throw new ArgumentOutOfRangeException(nameof(gridStepMeters));
        point = default;
        if (obstacles.Count > 128) throw new ArgumentException("At most 128 obstacle footprints are supported.", nameof(obstacles));
        if (IsFree(origin, bounds, obstacles)) { point = origin; return true; }

        var maximumRing = (int)Math.Ceiling(Math.Max(bounds.MaximumX - bounds.MinimumX, bounds.MaximumZ - bounds.MinimumZ) / gridStepMeters);
        for (var ring = 1; ring <= maximumRing; ring++)
        {
            var found = false;
            var bestDistance = double.PositiveInfinity;
            for (var offsetX = -ring; offsetX <= ring; offsetX++)
            for (var offsetZ = -ring; offsetZ <= ring; offsetZ++)
            {
                if (Math.Max(Math.Abs(offsetX), Math.Abs(offsetZ)) != ring) continue;
                var candidate = new Vector2D(origin.X + offsetX * gridStepMeters, origin.Z + offsetZ * gridStepMeters);
                if (!IsFree(candidate, bounds, obstacles)) continue;
                var distance = origin.DistanceTo(candidate);
                if (found && (distance > bestDistance || (distance == bestDistance &&
                    (candidate.X > point.X || (candidate.X == point.X && candidate.Z >= point.Z))))) continue;
                point = candidate; bestDistance = distance; found = true;
            }
            if (found) return true;
        }
        return false;
    }

    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

    /// <summary>Tests the complete swept segment against expanded obstacle circles.</summary>
    public static bool SegmentIsFree(Vector2D start, Vector2D end, WorkspaceBounds bounds, IReadOnlyList<SegmentedObstacle> obstacles)
    {
        if (!IsFree(start, bounds, obstacles) || !IsFree(end, bounds, obstacles)) return false;
        var dx = (double)end.X - start.X; var dz = (double)end.Z - start.Z; var length = dx * dx + dz * dz;
        foreach (var obstacle in obstacles)
        {
            var t = length == 0 ? 0 : Math.Max(0, Math.Min(1, ((obstacle.WorldPosition.X - start.X) * dx + (obstacle.WorldPosition.Z - start.Z) * dz) / length));
            var x = start.X + t * dx - obstacle.WorldPosition.X; var z = start.Z + t * dz - obstacle.WorldPosition.Z;
            var radius = obstacle.RadiusMeters + ClearanceMeters;
            if (x * x + z * z < radius * radius) return false;
        }
        return true;
    }

    /// <summary>Finds a collision-free next waypoint using a bounded grid and a deterministic A* heap.</summary>
    public static bool TryNextWaypoint(Vector2D start, Vector2D goal, WorkspaceBounds bounds,
        IReadOnlyList<SegmentedObstacle> obstacles, out Vector2D waypoint)
    {
        if (obstacles is null) throw new ArgumentNullException(nameof(obstacles));
        waypoint = start;
        goal = bounds.Clamp(goal, ClearanceMeters);
        if (!IsFree(start, bounds, obstacles) || !IsFree(goal, bounds, obstacles)) return false;
        if (SegmentIsFree(start, goal, bounds, obstacles)) { waypoint = goal; return true; }
        var width = bounds.MaximumX - bounds.MinimumX - 2 * ClearanceMeters;
        var depth = bounds.MaximumZ - bounds.MinimumZ - 2 * ClearanceMeters;
        var step = Math.Max(0.4, Math.Max(width, depth) / 64);
        var columns = (int)Math.Ceiling(width / step) + 1; var rows = (int)Math.Ceiling(depth / step) + 1;
        var count = columns * rows;
        var points = new Vector2D[count]; var free = new bool[count]; var distance = new double[count]; var parent = new int[count];
        for (var z = 0; z < rows; z++) for (var x = 0; x < columns; x++)
        {
            var index = z * columns + x;
            points[index] = new Vector2D(bounds.MinimumX + ClearanceMeters + width * x / (columns - 1),
                bounds.MinimumZ + ClearanceMeters + depth * z / (rows - 1));
            free[index] = IsFree(points[index], bounds, obstacles); distance[index] = double.PositiveInfinity; parent[index] = -2;
        }
        var heap = new Heap();
        var seeded = 0;
        // Include nearby visible grid vertices so rounding cannot place the start inside a carved footprint.
        var candidates = new List<int>();
        for (var i = 0; i < count; i++) if (free[i] && points[i].DistanceTo(start) <= 2.5 * step) candidates.Add(i);
        candidates.Sort((a, b) => { var comparison = points[a].DistanceTo(start).CompareTo(points[b].DistanceTo(start)); return comparison == 0 ? a.CompareTo(b) : comparison; });
        foreach (var index in candidates)
        {
            if (!SegmentIsFree(start, points[index], bounds, obstacles)) continue;
            distance[index] = start.DistanceTo(points[index]); parent[index] = -1;
            heap.Push(index, distance[index] + points[index].DistanceTo(goal));
            if (++seeded == 8) break;
        }
        var closed = new bool[count];
        while (heap.Count > 0)
        {
            var index = heap.Pop(); if (closed[index]) continue; closed[index] = true;
            if (points[index].DistanceTo(goal) < 2.5 * step && SegmentIsFree(points[index], goal, bounds, obstacles))
            {
                var path = new List<int>();
                for (var cursor = index; cursor >= 0; cursor = parent[cursor]) path.Add(cursor);
                path.Reverse();
                foreach (var node in path)
                { if (!SegmentIsFree(start, points[node], bounds, obstacles)) break; waypoint = points[node]; }
                return waypoint.DistanceTo(start) > 0.01;
            }
            var column = index % columns; var row = index / columns;
            for (var dz = -1; dz <= 1; dz++) for (var dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dz == 0) continue;
                var x = column + dx; var z = row + dz;
                if (x < 0 || z < 0 || x >= columns || z >= rows) continue;
                var next = z * columns + x;
                if (!free[next] || closed[next] || !SegmentIsFree(points[index], points[next], bounds, obstacles)) continue;
                var cost = distance[index] + points[index].DistanceTo(points[next]);
                if (cost >= distance[next]) continue;
                distance[next] = cost; parent[next] = index; heap.Push(next, cost + points[next].DistanceTo(goal));
            }
        }
        return false;
    }

    /// <summary>Validates complete policy output, clamps workspace targets, and turns them into safe next waypoints.</summary>
    public static IReadOnlyList<AgentTargetDirective> ValidateDirectives(WorldStateSnapshot world,
        IReadOnlyList<AgentTargetDirective> directives, WorkspaceBounds bounds, float maximumSpeed = 4)
    {
        if (world is null || directives is null) throw new ArgumentNullException(nameof(world));
        if (directives.Count != world.Agents.Count || directives.Count > 128) throw new ArgumentException("Exactly one directive per agent is required.");
        var agents = new Dictionary<int, AgentTelemetry>(); foreach (var agent in world.Agents) agents.Add(agent.AgentId, agent);
        var result = new List<AgentTargetDirective>(directives.Count); var ids = new HashSet<int>();
        foreach (var directive in directives)
        {
            if (directive is null || !ids.Add(directive.AgentId) || !agents.TryGetValue(directive.AgentId, out var agent) ||
                directive.DesiredSpeed > maximumSpeed) throw new ArgumentException("Invalid policy recipient or speed.");
            if (directive.DesiredSpeed == 0) { result.Add(new AgentTargetDirective(agent.AgentId, agent.CurrentPosition, 0, directive.StatusTag)); continue; }
            var goal = bounds.Clamp(directive.TargetPosition, ClearanceMeters);
            if (!IsFree(goal, bounds, world.Obstacles)) throw new ArgumentException("Policy destination intersects an obstacle.");
            var moving = TryNextWaypoint(agent.CurrentPosition, goal, bounds, world.Obstacles, out var target);
            result.Add(new AgentTargetDirective(agent.AgentId, target, moving ? directive.DesiredSpeed : 0, moving ? directive.StatusTag : "blocked_hold"));
        }
        result.Sort((a, b) => a.AgentId.CompareTo(b.AgentId));
        return result.AsReadOnly();
    }

    private sealed class Heap
    {
        private readonly List<(int Index, double Score)> _items = new List<(int, double)>();
        internal int Count => _items.Count;
        private static bool Less((int Index, double Score) a, (int Index, double Score) b) => a.Score < b.Score || (a.Score == b.Score && a.Index < b.Index);
        internal void Push(int index, double score)
        {
            _items.Add((index, score)); var current = _items.Count - 1;
            while (current > 0) { var parent = (current - 1) / 2; if (!Less(_items[current], _items[parent])) break;
                (_items[current], _items[parent]) = (_items[parent], _items[current]); current = parent; }
        }
        internal int Pop()
        {
            var result = _items[0].Index; var last = _items[_items.Count - 1]; _items.RemoveAt(_items.Count - 1);
            if (_items.Count == 0) return result;
            _items[0] = last; var current = 0;
            while (true) { var left = 2 * current + 1; if (left >= _items.Count) break;
                var right = left + 1; var child = right < _items.Count && Less(_items[right], _items[left]) ? right : left;
                if (!Less(_items[child], _items[current])) break;
                (_items[current], _items[child]) = (_items[child], _items[current]); current = child; }
            return result;
        }
    }
}
