using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using OmniTwin.Contracts;
using OmniTwin.Perception.Audio;

namespace OmniTwin.Backend.Media;

/// <summary>One active inference with bounded five-minute request-id deduplication; audio is never persisted.</summary>
public sealed class VoiceTranscriptionService
{
    private readonly object _gate = new(); private readonly IMuseVoiceClient _client; private readonly CancellationToken _hostStop; private readonly TimeProvider _time;
    private readonly Dictionary<Guid, Entry> _entries = new(); private readonly Queue<Guid> _order = new(); private bool _active;
    private sealed record Entry(byte[] Hash, Task<VoiceTranscription> Result, long Started);
    /// <summary>Creates the service using the environment-backed provider or an explicitly injected protocol-test client.</summary>
    public VoiceTranscriptionService(IMuseVoiceClient client, IHostApplicationLifetime lifetime, TimeProvider time)
    { _client = client; _hostStop = lifetime.ApplicationStopping; _time = time; }
    /// <summary>Joins identical retries; conflicting IDs reject and concurrent different recordings receive voice_busy.</summary>
    public Task<VoiceTranscription> TranscribeAsync(Guid id, ReadOnlyMemory<byte> wav, CancellationToken caller)
    {
        if (id == Guid.Empty) throw new ArgumentException("Missing voice request identity.");
        var owned = wav.ToArray(); PcmWaveCodec.Validate(owned); var hash = SHA256.HashData(owned);
        lock (_gate)
        {
            while (_order.Count > 0 && _entries[_order.Peek()].Result.IsCompleted &&
                (_entries.Count >= 16 || _time.GetElapsedTime(_entries[_order.Peek()].Started).TotalMinutes >= 5)) _entries.Remove(_order.Dequeue());
            if (_entries.TryGetValue(id, out var existing))
            {
                Array.Clear(owned);
                if (!CryptographicOperations.FixedTimeEquals(hash, existing.Hash)) throw new InvalidOperationException("conflicting_voice_request");
                return existing.Result.WaitAsync(caller);
            }
            if (_active) { Array.Clear(owned); throw new VoiceApiException("voice_busy", 0); }
            _active = true;
            var completion = new TaskCompletionSource<VoiceTranscription>(TaskCreationOptions.RunContinuationsAsynchronously);
            _entries.Add(id, new Entry(hash, completion.Task, _time.GetTimestamp())); _order.Enqueue(id);
            _ = Run(); return completion.Task.WaitAsync(caller);
            async Task Run()
            {
                try { completion.TrySetResult(await _client.TranscribeAsync(id, owned, _hostStop).ConfigureAwait(false)); }
                catch (OperationCanceledException) { completion.TrySetCanceled(_hostStop); }
                catch (Exception error) { completion.TrySetException(error); }
                finally { Array.Clear(owned); lock (_gate) _active = false; }
            }
        }
    }
}
