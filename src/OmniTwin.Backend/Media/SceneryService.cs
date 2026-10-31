using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using OmniTwin.Perception.Models;

namespace OmniTwin.Backend.Media;

/// <summary>Safe generation state, without retaining a prompt or provider response.</summary>
public sealed record SceneryStatus(Guid RequestId, string Status, string? FailureCategory, string ModelId);

/// <summary>One asynchronous generation, bounded idempotency history and one owned completed texture. No generation blocks an endpoint.</summary>
public sealed class SceneryService : IHostedService
{
    private readonly object _gate = new(); private readonly IMuseImageClient _client; private readonly CancellationToken _stop;
    private readonly Dictionary<Guid, (string PromptHash, SceneryStatus Status)> _requests = new(); private readonly Queue<Guid> _order = new();
    private Task _operation = Task.CompletedTask;
    private bool _active; private Guid _imageId; private byte[]? _image;
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
            if (_active) throw new ModelApiException("scenery_busy");
            while (_order.Count >= 16) _requests.Remove(_order.Dequeue());
            var status = new SceneryStatus(id, "generating", null, MuseImageClient.ModelId);
            _requests.Add(id, (hash, status)); _order.Enqueue(id); _active = true;
            _operation = Task.Run(async () =>
            {
                try
                {
                    var image = await _client.GenerateAsync(prompt, _stop).ConfigureAwait(false);
                    lock (_gate) { if (_stop.IsCancellationRequested) return; _image = image; _imageId = id; _requests[id] = (hash, status with { Status = "ready" }); }
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { lock (_gate) _requests[id] = (hash, status with { Status = "failed", FailureCategory = "scenery_cancelled" }); }
                catch (ModelApiException error) { lock (_gate) _requests[id] = (hash, status with { Status = "failed", FailureCategory = error.Category }); }
                finally { lock (_gate) _active = false; }
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
    public async Task StopAsync(CancellationToken cancellationToken) { Task operation; lock (_gate) operation = _operation; await operation.WaitAsync(cancellationToken).ConfigureAwait(false); }
}
