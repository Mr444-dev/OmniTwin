using System;
using System.Collections.Generic;
using OmniTwin.Contracts;

namespace OmniTwin.Transport;

/// <summary>Single-reader sequence/session validation for one connected backend stream.</summary>
public sealed class WorldStreamOrderGuard
{
    private WorldStreamMessage? _last;
    private readonly HashSet<Guid> _sessions = new HashSet<Guid>();

    /// <summary>Accepts increasing messages; gaps are allowed, but replay, calibration mutation, and retired-session resurrection fail.</summary>
    public void Accept(WorldStreamMessage message)
    {
        if (message is null) throw new ArgumentNullException(nameof(message));
        if (_last is not null)
        {
            if (message.StreamId != _last.StreamId || message.SequenceNumber <= _last.SequenceNumber)
                throw new WorldStreamProtocolException("stream_order");
            if (message.World.SessionId == _last.World.SessionId)
            {
                if (message.World.CalibrationId != _last.World.CalibrationId || message.World.SequenceNumber <= _last.World.SequenceNumber)
                    throw new WorldStreamProtocolException("world_order_or_calibration");
                if (_last.World.SourceSequenceNumber.HasValue && (!message.World.SourceSequenceNumber.HasValue ||
                    message.World.SourceSequenceNumber.Value < _last.World.SourceSequenceNumber.Value))
                    throw new WorldStreamProtocolException("source_order");
                if (message.World.SourceSequenceNumber == _last.World.SourceSequenceNumber &&
                    _last.World.SourceAgeMilliseconds.HasValue && message.World.SourceAgeMilliseconds < _last.World.SourceAgeMilliseconds)
                    throw new WorldStreamProtocolException("source_age_reversal");
            }
            else if (_sessions.Contains(message.World.SessionId)) throw new WorldStreamProtocolException("retired_session");
        }
        if (!_sessions.Contains(message.World.SessionId) && _sessions.Count >= 16) throw new WorldStreamProtocolException("session_limit");
        _sessions.Add(message.World.SessionId);
        _last = message;
    }
}
