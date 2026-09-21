namespace VoiceInput.Desktop.Asr;

public abstract record AsrEvent(DateTimeOffset Timestamp);

public sealed record PartialTranscript(string Text, DateTimeOffset Timestamp, string? SegmentId = null)
    : AsrEvent(Timestamp);

public sealed record StableTranscript(string Text, DateTimeOffset Timestamp, string? SegmentId = null)
    : AsrEvent(Timestamp);

public sealed record AsrError(string Message, DateTimeOffset Timestamp)
    : AsrEvent(Timestamp);

public sealed record ConnectionState(string State, DateTimeOffset Timestamp)
    : AsrEvent(Timestamp);

public sealed class AsrProviderException(string message) : Exception(message);
