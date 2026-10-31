using System;
using System.Text;

namespace OmniTwin.Contracts;

/// <summary>A validated transcription draft; receiving it never executes a mission.</summary>
public sealed class VoiceTranscription
{
    /// <summary>Creates a bounded draft, normalizing spoken whitespace and preserving a legitimate empty transcript.</summary>
    public VoiceTranscription(Guid requestId, string transcript, int audioDurationMilliseconds)
    {
        if (requestId == Guid.Empty) throw new ArgumentException("A request identity is required.", nameof(requestId));
        if (transcript is null || transcript.Length > 4096) throw new ArgumentException("Transcript exceeds its limit.", nameof(transcript));
        if (audioDurationMilliseconds < 0 || audioDurationMilliseconds > 5100) throw new ArgumentOutOfRangeException(nameof(audioDurationMilliseconds));
        var normalized = new StringBuilder(transcript.Length); var space = false;
        foreach (var character in transcript)
        {
            if (char.IsWhiteSpace(character)) { space = normalized.Length > 0; continue; }
            if (char.IsControl(character)) throw new ArgumentException("Transcript contains controls.", nameof(transcript));
            if (space) normalized.Append(' '); normalized.Append(character); space = false;
        }
        RequestId = requestId; Transcript = normalized.ToString(); AudioDurationMilliseconds = audioDurationMilliseconds;
    }
    /// <summary>Gets the local response schema.</summary>
    public int SchemaVersion => 1;
    /// <summary>Gets the upload correlation identity.</summary>
    public Guid RequestId { get; }
    /// <summary>Gets plain text, or empty when no speech was recognized.</summary>
    public string Transcript { get; }
    /// <summary>Gets processed duration, checked against the uploaded recording.</summary>
    public int AudioDurationMilliseconds { get; }
}
