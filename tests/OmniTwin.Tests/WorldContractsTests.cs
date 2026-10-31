using System;
using System.Collections.Generic;
using System.Text.Json;
using OmniTwin.Contracts;
using Xunit;

namespace OmniTwin.Tests;

public sealed class WorldContractsTests
{
    private static SegmentedObstacle Obstacle(int id = 1) => new(id, "cup", new Vector2D(2, 3), 0.4f, 0.9f, false);
    private static AgentTelemetry Agent(int id = 1) => new(id, AgentRole.GroundVehicle, new Vector2D(-2, 1),
        new Vector2D(0.5f, 0), 0.75f, AgentStatus.Navigating);

    [Fact]
    public void SnapshotOwnsCollectionsAndExposesReadOnlyViews()
    {
        var obstacles = new List<SegmentedObstacle> { Obstacle() };
        var agents = new List<AgentTelemetry> { Agent() };
        var snapshot = new WorldStateSnapshot(123, obstacles, agents, "Hold", "");
        obstacles.Clear();
        agents[0] = Agent(99);
        Assert.Single(snapshot.Obstacles);
        Assert.Equal(1, snapshot.Agents[0].AgentId);
        Assert.Throws<NotSupportedException>(() => ((IList<SegmentedObstacle>)snapshot.Obstacles).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<AgentTelemetry>)snapshot.Agents)[0] = Agent(99));
    }

    [Fact]
    public void RejectsDuplicateAndNullCollectionEntries()
    {
        Assert.Throws<ArgumentException>(() => new WorldStateSnapshot(0, new[] { Obstacle(), Obstacle() }, Array.Empty<AgentTelemetry>(), "Hold", ""));
        Assert.Throws<ArgumentException>(() => new WorldStateSnapshot(0, Array.Empty<SegmentedObstacle>(), new[] { Agent(), Agent() }, "Hold", ""));
        Assert.Throws<ArgumentException>(() => new WorldStateSnapshot(0, new SegmentedObstacle[] { null! }, Array.Empty<AgentTelemetry>(), "Hold", ""));
        Assert.Throws<ArgumentException>(() => new WorldStateSnapshot(0, Array.Empty<SegmentedObstacle>(), new AgentTelemetry[] { null! }, "Hold", ""));
        Assert.Throws<ArgumentNullException>(() => new WorldStateSnapshot(0, null!, Array.Empty<AgentTelemetry>(), "Hold", ""));
    }

    [Fact]
    public void JsonRoundTripPreservesSnapshotAndValues()
    {
        var snapshot = new WorldStateSnapshot(123456789, new[] { Obstacle() }, new[] { Agent() }, "Quarantine", "Guard cup", 42);
        var json = JsonSerializer.Serialize(snapshot);
        var result = JsonSerializer.Deserialize<WorldStateSnapshot>(json);
        Assert.NotNull(result);
        Assert.Equal(snapshot.TimestampUnixMs, result.TimestampUnixMs);
        Assert.Equal(42, result.SequenceNumber);
        Assert.Equal(2, result.SchemaVersion);
        Assert.Equal(snapshot.Obstacles[0], result.Obstacles[0]);
        Assert.Equal(snapshot.Agents[0], result.Agents[0]);
        Assert.Equal("Quarantine", result.ActivePolicyName);
        Assert.Equal("Guard cup", result.LastVoiceCommand);
    }

    [Fact]
    public void JsonRoundTripPreservesMissionAndDirective()
    {
        var mission = new SwarmMissionCommand(Guid.NewGuid(), SwarmMissionKind.HazardQuarantine, "Guard cup", 123, 1, 3);
        Assert.Equal(mission, JsonSerializer.Deserialize<SwarmMissionCommand>(JsonSerializer.Serialize(mission)));
        var directive = new AgentTargetDirective(1, new Vector2D(4, 5), 0.5f, "Guarding");
        Assert.Equal(directive, JsonSerializer.Deserialize<AgentTargetDirective>(JsonSerializer.Serialize(directive)));
    }

    [Fact]
    public void LidarTelemetryEnforcesRayAndDistanceInvariants()
    {
        var clear = new LidarTelemetry(48, 0, 6, null, 0);
        var hit = new LidarTelemetry(48, 3, 6, 0.35f, 12);
        Assert.Null(clear.MinimumDistanceMeters);
        Assert.Equal(0, clear.HitCount);
        Assert.Equal(0.35f, hit.MinimumDistanceMeters);
        Assert.Throws<ArgumentOutOfRangeException>(() => new LidarTelemetry(7, 0, 6, null, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LidarTelemetry(48, 49, 6, 0.2f, 0));
        Assert.Throws<ArgumentException>(() => new LidarTelemetry(48, 0, 6, 0.2f, 0));
        Assert.Throws<ArgumentException>(() => new LidarTelemetry(48, 1, 6, null, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LidarTelemetry(48, 1, 6, float.NaN, 0));
    }

    [Fact]
    public void JsonRoundTripPreservesLidarMeasurementOnAgent()
    {
        var lidar = new LidarTelemetry(48, 2, 6, 0.8f, 15);
        var agent = new AgentTelemetry(4, AgentRole.AerialDrone, new Vector2D(1, 2), default, 0.8f, AgentStatus.Navigating, lidar);
        var snapshot = new WorldStateSnapshot(123, Array.Empty<SegmentedObstacle>(), new[] { agent }, "Hold", "");
        var restored = JsonSerializer.Deserialize<WorldStateSnapshot>(JsonSerializer.Serialize(snapshot));
        Assert.NotNull(restored);
        Assert.Equal(lidar, restored.Agents[0].Lidar);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void RejectsNonfiniteScalars(float value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Vector2D(value, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Vector2D(0, value));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SegmentedObstacle(0, "cup", default, value, 1, false));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SegmentedObstacle(0, "cup", default, 1, value, false));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AgentTargetDirective(0, default, value, "Hold"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Vector2D(1, 2) with { X = value });
        Assert.Throws<ArgumentOutOfRangeException>(() => new Vector2D(1, 2) with { Z = value });
    }

    [Fact]
    public void DistanceUsesDoubleIntermediates()
    {
        var a = new Vector2D(float.MaxValue, float.MaxValue);
        var b = new Vector2D(-float.MaxValue, -float.MaxValue);
        var distance = a.DistanceTo(b);
        Assert.True(double.IsFinite(distance));
        Assert.True(distance > float.MaxValue);
        Assert.Equal(5, new Vector2D(1, 2).DistanceTo(new Vector2D(4, 6)));
    }

    [Fact]
    public void RejectsInvalidIdentifiersEnumsAndRanges()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Obstacle(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Agent(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SegmentedObstacle(0, "cup", default, 0, 1, false));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SegmentedObstacle(0, "cup", default, 1, 1.01f, false));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AgentTelemetry(0, (AgentRole)99, default, default, 1, AgentStatus.Idle));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AgentTelemetry(0, AgentRole.AerialDrone, default, default, 1, (AgentStatus)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AgentTelemetry(0, AgentRole.AerialDrone, default, default, -0.1f, AgentStatus.Idle));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AgentTargetDirective(0, default, -1, "Hold"));
        Assert.Throws<ArgumentException>(() => new SwarmMissionCommand(Guid.Empty, SwarmMissionKind.HoldPosition, "Hold", 0, null, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SwarmMissionCommand(Guid.NewGuid(), (SwarmMissionKind)99, "Hold", 0, null, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SwarmMissionCommand(Guid.NewGuid(), SwarmMissionKind.HoldPosition, "Hold", 0, -1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SwarmMissionCommand(Guid.NewGuid(), SwarmMissionKind.HoldPosition, "Hold", 0, null, 0));
    }

    [Fact]
    public void RejectsInvalidSnapshotMetadataAndUnboundedText()
    {
        WorldStateSnapshot Create(long timestamp = 0, long sequence = 0, int version = 2, string name = "Hold", string command = "") =>
            new(timestamp, Array.Empty<SegmentedObstacle>(), Array.Empty<AgentTelemetry>(), name, command, sequence, version);
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(timestamp: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(sequence: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(version: 1));
        Assert.Throws<ArgumentException>(() => Create(name: " "));
        Assert.Throws<ArgumentException>(() => Create(name: new string('x', 129)));
        Assert.Throws<ArgumentException>(() => Create(command: new string('x', 4097)));
        Assert.Throws<ArgumentNullException>(() => Create(command: null!));
        Assert.Throws<ArgumentException>(() => new SegmentedObstacle(0, " ", default, 1, 1, false));
    }
}
