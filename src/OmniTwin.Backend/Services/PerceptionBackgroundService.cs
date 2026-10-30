using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OmniTwin.Backend.Streaming;

namespace OmniTwin.Backend.Services;

/// <summary>Supervises one perception session and terminates the stream on unrecoverable source failure.</summary>
public sealed class PerceptionBackgroundService : BackgroundService
{
    private readonly IWorldPerceptionSource _source;
    private readonly SnapshotBroker _broker;
    private readonly BackendRuntimeState _state;
    private readonly ILogger<PerceptionBackgroundService> _logger;
    /// <summary>Creates the perception/transport composition worker.</summary>
    public PerceptionBackgroundService(IWorldPerceptionSource source, SnapshotBroker broker, BackendRuntimeState state,
        ILogger<PerceptionBackgroundService> logger)
    { _source = source; _broker = broker; _state = state; _logger = logger; }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_source.IsEnabled) { _state.Set("disabled", "perception_not_enabled"); return; }
        _state.Set("starting");
        try
        {
            await foreach (var publication in _source.ReadAllAsync(stoppingToken).WithCancellation(stoppingToken).ConfigureAwait(false))
            {
                _broker.Publish(publication.Snapshot, publication.FailureCategory, publication.RejectedDetectionCount);
                var degraded = publication.FailureCategory is not null || publication.RejectedDetectionCount != 0;
                _state.Set(degraded ? "degraded" : "running", publication.FailureCategory ?? (degraded ? "projection_rejected" : null));
            }
            _state.Set("stopped", "source_completed");
            _broker.Complete(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        { _state.Set("stopped", "host_stopping"); _broker.Complete(false); }
        catch (Exception error)
        {
            var category = error switch
            {
                JsonException or FormatException or ArgumentException => "invalid_source_data_or_configuration",
                IOException => "capture_or_calibration_io",
                _ => "perception_source_failed"
            };
            _state.Set("faulted", category);
            _broker.Complete(true);
            _logger.LogError("Perception stopped: {Category}; exception type {ExceptionType}", category, error.GetType().Name);
        }
    }
}
