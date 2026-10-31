using System;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using OmniTwin.Backend.Services;
using OmniTwin.Brain;
using OmniTwin.Contracts;

namespace OmniTwin.Backend.Swarm;

/// <summary>Latest-command synthesis worker; baseline/Hold missions cancel any superseded generation immediately.</summary>
public sealed class BrainMissionService : BackgroundService
{
    private readonly Channel<(SwarmMissionRequest Request, long Revision)> _commands = Channel.CreateBounded<(SwarmMissionRequest, long)>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false });
    private readonly object _gate = new(); private CancellationTokenSource? _active; private long _revision;
    private readonly SwarmCoordinator _swarm; private readonly BackendRuntimeState _runtime;
    private readonly SelfHealingLoop _loop; private readonly MuseSparkOpenClient _client;
    /// <summary>Registers the real model handler; no provider call occurs until an explicit model mission arrives.</summary>
    public BrainMissionService(SwarmCoordinator swarm, BackendRuntimeState runtime)
    {
        _swarm = swarm; _runtime = runtime; _client = new MuseSparkOpenClient();
        _loop = new SelfHealingLoop(_client, new RoslynPolicyCompiler(), Path.Combine(AppContext.BaseDirectory, "PolicyWorker"), Path.Combine(Environment.CurrentDirectory, "local", "policy-sandbox"));
        _swarm.RegisterBrain(QueueAsync);
    }
    private Task QueueAsync(SwarmMissionRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (_gate) { _active?.Cancel(); if (!_commands.Writer.TryWrite((request, ++_revision))) throw new InvalidOperationException("Brain service has stopped."); }
        return Task.CompletedTask;
    }
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var queued in _commands.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            var request = queued.Request;
            if (!request.UseBrain || request.Command.Kind == SwarmMissionKind.HoldPosition) continue;
            using var command = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken); command.CancelAfter(TimeSpan.FromMinutes(3));
            lock (_gate) { if (queued.Revision != _revision) continue; _active = command; }
            try
            {
                if (!_runtime.Read().IsObservationCurrent)
                { _swarm.ReportBrain(request.Command.CommandId, "fallback", "perception_not_current", null, false, 0, Array.Empty<string>()); continue; }
                var world = _swarm.BuildWorld();
                var telemetry = _swarm.Read().Telemetry;
                if (world.Agents.Count == 0 || telemetry is null || DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - telemetry.TimestampUnixMs > 2000)
                { _swarm.ReportBrain(request.Command.CommandId, "fallback", "agent_telemetry_missing_or_stale", null, false, 0, Array.Empty<string>()); continue; }
                var policy = await _loop.RunAsync(world, request.Command, progress => _swarm.ReportBrain(request.Command.CommandId, progress.Phase, progress.Category,
                    progress.ModelIdUsed, progress.UsedModelFallback, progress.Attempts, progress.Diagnostics), command.Token, _swarm.Bounds).ConfigureAwait(false);
                if (policy is not null && (!_runtime.Read().IsObservationCurrent || !_swarm.Activate(request.Command.CommandId, policy))) (policy as IDisposable)?.Dispose();
            }
            catch (OperationCanceledException) when (command.IsCancellationRequested)
            { if (!stoppingToken.IsCancellationRequested) _swarm.ReportBrain(request.Command.CommandId, "fallback", "generation_cancelled_or_expired", null, false, 0, Array.Empty<string>()); }
            finally { lock (_gate) if (ReferenceEquals(_active, command)) _active = null; }
        }
    }
    /// <inheritdoc />
    public override void Dispose() { lock (_gate) _active?.Cancel(); _commands.Writer.TryComplete(); base.Dispose(); _client.Dispose(); }
}
