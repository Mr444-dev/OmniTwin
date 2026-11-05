using System;
using System.Collections.Generic;
using System.Linq;
using OmniTwin.Contracts;
using OmniTwin.Policies;
using Xunit;

namespace OmniTwin.Tests;

public sealed class BuiltInPolicyTests
{
    private static readonly WorkspaceBounds Bounds = WorkspaceBounds.TacticalDesk;
    private static AgentTelemetry Agent(int id, float x, float z, AgentRole role = AgentRole.GroundVehicle) =>
        new(id, role, new Vector2D(x, z), default, 1, AgentStatus.Idle);
    private static WorldStateSnapshot World(IReadOnlyList<SegmentedObstacle>? obstacles = null,
        IReadOnlyList<AgentTelemetry>? agents = null, long sequence = 0) => new(100000,
        obstacles ?? Array.Empty<SegmentedObstacle>(), agents ?? new[] { Agent(1, -7, -3), Agent(2, -6, -3),
            Agent(3, -5, -3), Agent(4, -4, -3, AgentRole.AerialDrone), Agent(5, -3, -3, AgentRole.AerialDrone) }, "baseline", "", sequence);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void BaselinesAreCompleteDeterministicAndIndependentOfTelemetryOrder(int policyKind)
    {
        ISwarmPolicy Policy() => policyKind switch { 0 => new PerimeterPatrolPolicy(Bounds), 1 => new HazardQuarantinePolicy(Bounds), _ => new GridSearchAndRescuePolicy(Bounds) };
        var world = World(new[] { new SegmentedObstacle(7, "cup", default, 1, 0.9f, true) });
        var first = Policy().ComputeDirectives(world, 12);
        var second = Policy().ComputeDirectives(World(world.Obstacles, world.Agents.Reverse().ToArray()), 12);
        Assert.Equal(first, second); Assert.Equal(5, first.Count); Assert.Equal(5, first.Select(item => item.AgentId).Distinct().Count());
        foreach (var directive in first)
        {
            var agent = world.Agents.Single(item => item.AgentId == directive.AgentId);
            Assert.InRange(directive.DesiredSpeed, 0, 4);
            if (directive.DesiredSpeed > 0) Assert.True(SafeNavigation.SegmentIsFree(agent.CurrentPosition, directive.TargetPosition, Bounds, world.Obstacles));
        }
    }

    [Fact]
    public void HoldPreservesEveryMeasuredPositionAtZeroSpeed()
    {
        var world = World(); var hold = new EmergencyHoldPolicy().ComputeDirectives(world, 0);
        foreach (var directive in hold) { Assert.Equal(0, directive.DesiredSpeed); Assert.Equal(world.Agents.Single(item => item.AgentId == directive.AgentId).CurrentPosition, directive.TargetPosition); }
    }

    [Fact]
    public void PatrolPhasesAreUniformAlongTheInsetPerimeter()
    {
        var agents = new[] { Agent(1, -9.3f, -5.55f), Agent(2, 8, -5), Agent(3, 9, 4), Agent(4, -5, 5) };
        var result = new PerimeterPatrolPolicy(Bounds).ComputeDirectives(World(agents: agents), 0);
        var width = 18.6; var depth = 11.1; var perimeter = 2 * (width + depth);
        var first = result[0].TargetPosition;
        Assert.InRange(first.X, -9.301f, -9.299f); Assert.InRange(first.Z, -5.551f, -5.549f);
        Assert.InRange(result[1].TargetPosition.X, (float)(-9.3 + perimeter / 4) - 0.001f, (float)(-9.3 + perimeter / 4) + 0.001f);
        Assert.InRange(result[3].TargetPosition.X, (float)(9.3 - (perimeter * 0.75 - width - depth)) - 0.001f, (float)(9.3 - (perimeter * 0.75 - width - depth)) + 0.001f);
    }

    [Fact]
    public void QuarantinePrefersHazardThenLargestThenStableId()
    {
        var obstacles = new[] { new SegmentedObstacle(1, "box", new Vector2D(-4, 0), 2, 1, false),
            new SegmentedObstacle(9, "hazard", new Vector2D(2, 0), 0.5f, 1, true) };
        var world = World(obstacles, new[] { Agent(1, 8, 0) });
        var target = Assert.Single(new HazardQuarantinePolicy(Bounds, 3).ComputeDirectives(world, 0));
        Assert.Equal(new Vector2D(5, 0), target.TargetPosition);
        Assert.Equal(0, Assert.Single(new HazardQuarantinePolicy(Bounds).ComputeDirectives(World(agents: new[] { Agent(1, 8, 0) }), 0)).DesiredSpeed);
    }

    [Fact]
    public void LocalHybridBaselineSeparatesGroundCordonAndAerialPerimeterRoles()
    {
        var obstacle = new SegmentedObstacle(7, "cup", new Vector2D(0, 0), 0.35f, 0.95f, true);
        var world = World(new[] { obstacle });
        var policy = new HybridCordonPatrolBaselinePolicy(Bounds);
        var first = policy.ComputeDirectives(world, 8);
        var second = policy.ComputeDirectives(World(new[] { obstacle }, world.Agents.Reverse().ToArray()), 8);

        Assert.Equal(first, second);
        Assert.Equal(5, first.Count);
        foreach (var directive in first)
        {
            var agent = world.Agents.Single(item => item.AgentId == directive.AgentId);
            Assert.InRange(directive.DesiredSpeed, 0, 4);
            if (directive.DesiredSpeed <= 0) continue;
            Assert.True(SafeNavigation.SegmentIsFree(agent.CurrentPosition, directive.TargetPosition, Bounds, world.Obstacles));
            if (agent.Role == AgentRole.GroundVehicle)
            {
                Assert.Equal("local_hybrid_ground_cordon", directive.StatusTag);
                // The safe route planner can return its next waypoint instead of the final ring point.
                Assert.InRange(directive.TargetPosition.DistanceTo(obstacle.WorldPosition), 0.5f, 3.6f);
            }
            else
            {
                Assert.Equal("local_hybrid_aerial_perimeter", directive.StatusTag);
                Assert.True(MathF.Abs(directive.TargetPosition.X) > 8.5f || MathF.Abs(directive.TargetPosition.Z) > 4.8f);
            }
        }
    }

    [Fact]
    public void SearchAdvancesOnlyAfterArrivalAndDoesNotAdvanceTwiceForOneSequence()
    {
        var policy = new GridSearchAndRescuePolicy(Bounds);
        var world = World(agents: new[] { Agent(1, 0, 0) });
        var first = Assert.Single(policy.ComputeDirectives(world, 0));
        var standing = World(agents: new[] { new AgentTelemetry(1, AgentRole.GroundVehicle, first.TargetPosition, default, 1, AgentStatus.Idle) }, sequence: 1);
        var second = Assert.Single(policy.ComputeDirectives(standing, 1));
        Assert.NotEqual(first.TargetPosition, second.TargetPosition);
        Assert.Equal(second, Assert.Single(policy.ComputeDirectives(standing, 1)));
    }

    [Fact]
    public void PlannerRoutesAroundCircleAndRejectsAnEnclosedStart()
    {
        var obstacles = new[] { new SegmentedObstacle(1, "box", default, 1, 1, false) };
        Assert.True(SafeNavigation.TryNextWaypoint(new Vector2D(-4, 0), new Vector2D(4, 0), Bounds, obstacles, out var waypoint));
        Assert.True(SafeNavigation.SegmentIsFree(new Vector2D(-4, 0), waypoint, Bounds, obstacles));
        Assert.False(SafeNavigation.TryNextWaypoint(default, new Vector2D(4, 0), Bounds, obstacles, out _));
    }

    [Fact]
    public void SpawnRecoveryFindsNearestFreePointDeterministically()
    {
        var obstacle = new[] { new SegmentedObstacle(1, "container", new Vector2D(-2.91f, 0.339f), 0.744f, 0.72f, false) };
        var blockedSpawn = new Vector2D(-3, 1);
        Assert.False(SafeNavigation.IsFree(blockedSpawn, Bounds, obstacle));
        Assert.True(SafeNavigation.TryFindNearestFreePoint(blockedSpawn, Bounds, obstacle, out var first));
        Assert.True(SafeNavigation.TryFindNearestFreePoint(blockedSpawn, Bounds, obstacle, out var second));
        Assert.Equal(first, second);
        Assert.True(SafeNavigation.IsFree(first, Bounds, obstacle));
        Assert.InRange(blockedSpawn.DistanceTo(first), 0.5f, 1.25f);
    }

    [Fact]
    public void IngestionClampsTargetsButRejectsInvalidRecipientsAndObstacleTargets()
    {
        var world = World(agents: new[] { Agent(1, 0, 0) });
        var clamped = Assert.Single(SafeNavigation.ValidateDirectives(world, new[] { new AgentTargetDirective(1, new Vector2D(100, 100), 1, "test") }, Bounds));
        Assert.True(Bounds.Contains(clamped.TargetPosition, SafeNavigation.ClearanceMeters));
        Assert.Throws<ArgumentException>(() => SafeNavigation.ValidateDirectives(world, new[] { new AgentTargetDirective(2, default, 1, "test") }, Bounds));
        var obstacleWorld = World(new[] { new SegmentedObstacle(1, "cup", new Vector2D(2, 0), 1, 1, false) }, world.Agents);
        Assert.Throws<ArgumentException>(() => SafeNavigation.ValidateDirectives(obstacleWorld, new[] { new AgentTargetDirective(1, new Vector2D(2, 0), 1, "test") }, Bounds));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PerimeterPatrolPolicy(Bounds).ComputeDirectives(world, float.NaN));
    }
}
