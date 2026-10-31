using System;
using System.Linq;
using System.Text;
using OmniTwin.Contracts;
using OmniTwin.Transport;
using Xunit;

namespace OmniTwin.Tests;

public sealed class SwarmWireTests
{
    private static SwarmTelemetrySnapshot Telemetry() => new(Guid.NewGuid(), 1, 100000, new[] {
        new AgentTelemetry(1, AgentRole.GroundVehicle, new Vector2D(1.25f, -2.5f), default, 0.9f, AgentStatus.Holding) });

    [Fact]
    public void TelemetryAndControlRoundTripPreserveNonzeroCoordinatesAndProvenance()
    {
        var input = Telemetry(); var copy = SwarmWireCodec.DecodeTelemetry(SwarmWireCodec.EncodeTelemetry(input));
        Assert.Equal(input.SessionId, copy.SessionId); Assert.Equal(input.Agents, copy.Agents);
        var control = new SwarmControlSnapshot(input.SessionId, 1, 100000, false, null, null, null, WorkspaceBounds.TacticalDesk,
            "EmergencyHoldPolicy", "auth_missing_or_invalid_401", "fallback", "baseten_key_missing", null, false, 0,
            Array.Empty<string>(), input.Agents.Select(agent => new AgentTargetDirective(agent.AgentId, agent.CurrentPosition, 0, "hold")).ToArray());
        var decoded = SwarmWireCodec.DecodeControl(SwarmWireCodec.EncodeControl(control));
        Assert.Equal("auth_missing_or_invalid_401", decoded.PerceptionFailureCategory);
        Assert.Equal(new Vector2D(1.25f, -2.5f), Assert.Single(decoded.Directives).TargetPosition);
        Assert.False(decoded.IsSourceCurrent);
    }

    [Fact]
    public void RejectsMissingUnknownDuplicateVersionEnumAndNonfiniteInput()
    {
        var json = Encoding.UTF8.GetString(SwarmWireCodec.EncodeTelemetry(Telemetry()));
        foreach (var invalid in new[] { "{}", json.Replace("\"schemaVersion\":1", "\"schemaVersion\":2"),
            json.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"extra\":0"), json.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1"),
            json.Replace("\"role\":0", "\"role\":99"), json.Replace("\"x\":1.25", "\"x\":1e999") })
            Assert.Throws<WorldStreamProtocolException>(() => SwarmWireCodec.DecodeTelemetry(Encoding.UTF8.GetBytes(invalid)));
        Assert.Throws<WorldStreamProtocolException>(() => SwarmWireCodec.DecodeTelemetry(new byte[SwarmWireCodec.MaximumBytes + 1]));
    }

    [Fact]
    public void MissionRetainsExplicitInferenceConsentAndRejectsDuplicateTargets()
    {
        var request = new SwarmMissionRequest(new SwarmMissionCommand(Guid.NewGuid(), SwarmMissionKind.GridSearchAndRescue, "Search the workspace", 100000, null, 3), true);
        var copy = SwarmWireCodec.DecodeMission(SwarmWireCodec.EncodeMission(request));
        Assert.True(copy.UseBrain); Assert.Equal(request.Command, copy.Command);
        var directives = new[] { new AgentTargetDirective(1, default, 1, "patrol"), new AgentTargetDirective(1, default, 1, "patrol") };
        Assert.Throws<WorldStreamProtocolException>(() => SwarmWireCodec.DecodeDirectives(SwarmWireCodec.EncodeDirectives(directives)));
    }
}
