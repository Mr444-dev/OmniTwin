using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using OmniTwin.Contracts;
using OmniTwin.Perception.Models;

namespace OmniTwin.Backend.Media;

/// <summary>Safe generation state, without retaining a prompt or provider response.</summary>
public sealed record SceneryStatus(Guid RequestId, string Status, string? FailureCategory, string ModelId);

/// <summary>One asynchronous generation, bounded idempotency history and one owned completed texture. No generation blocks an endpoint.</summary>
public sealed class SceneryService : IHostedService
{
    private readonly object _gate = new(); private readonly IMuseImageClient _client; private readonly CancellationToken _stop;
    private readonly Dictionary<Guid, (string PromptHash, SceneryStatus Status)> _requests = new(); private readonly Queue<Guid> _order = new();
    private readonly CommandEpoch _epoch = new(); private CancellationTokenSource? _activeCancellation;
    private Task _operation = Task.CompletedTask;
    private Guid _imageId; private byte[]? _image;
    /// <summary>Creates a host-scoped generator.</summary>
    public SceneryService(IMuseImageClient client, IHostApplicationLifetime lifetime) { _client = client; _stop = lifetime.ApplicationStopping; }
    /// <summary>Queues one explicit prompt; matching identities never generate twice within the bounded history.</summary>
    public SceneryStatus Submit(Guid id, string prompt)
    {
        if (id == Guid.Empty) throw new ArgumentException("A request identity is required."); MuseImageClient.ValidatePrompt(prompt);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(prompt)));
        lock (_gate)
        {
            if (_requests.TryGetValue(id, out var existing)) { if (existing.PromptHash != hash) throw new InvalidOperationException("conflicting_scenery_request"); return existing.Status; }
            while (_order.Count >= 16) _requests.Remove(_order.Dequeue());
            var status = new SceneryStatus(id, "generating", null, MuseImageClient.ModelId);
            var epoch = _epoch.Advance(); var prior = _operation; var priorCancellation = _activeCancellation;
            priorCancellation?.Cancel();
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_stop);
            _activeCancellation = cancellation;
            _requests.Add(id, (hash, status)); _order.Enqueue(id);
            _operation = Task.Run(async () =>
            {
                try
                {
                    await prior.ConfigureAwait(false);
                    cancellation.Token.ThrowIfCancellationRequested();
                    var image = await _client.GenerateAsync(prompt, cancellation.Token).ConfigureAwait(false);
                    lock (_gate)
                    {
                        if (!_epoch.IsCurrent(epoch) || cancellation.IsCancellationRequested) return;
                        _image = image; _imageId = id; _requests[id] = (hash, status with { Status = "ready" });
                    }
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                { lock (_gate) _requests[id] = (hash, status with { Status = "failed", FailureCategory = _epoch.IsCurrent(epoch) ? "scenery_cancelled" : "scenery_superseded" }); }
                catch (ModelApiException error) { lock (_gate) _requests[id] = (hash, status with { Status = "failed", FailureCategory = error.Category }); }
                catch (Exception)
                { lock (_gate) _requests[id] = (hash, status with { Status = "failed", FailureCategory = "scenery_worker_failed" }); }
                finally
                {
                    lock (_gate) if (_epoch.IsCurrent(epoch)) _activeCancellation = null;
                    cancellation.Dispose();
                }
            });
            return status;
        }
    }
    /// <summary>Reads only a known request's safe status.</summary>
    public SceneryStatus? Read(Guid id) { lock (_gate) return _requests.TryGetValue(id, out var value) ? value.Status : null; }
    /// <summary>Returns a copy only for the most recently completed request.</summary>
    public byte[]? ReadImage(Guid id) { lock (_gate) return _imageId == id && _image is not null ? (byte[])_image.Clone() : null; }
    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task operation; lock (_gate) { _activeCancellation?.Cancel(); operation = _operation; }
        await operation.WaitAsync(cancellationToken).ConfigureAwait(false);
    }
}
