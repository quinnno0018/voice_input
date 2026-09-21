namespace VoiceInput.Desktop.Hud;

public sealed class PartialTranscriptCoalescer(TimeSpan interval)
{
    private DateTimeOffset? _lastRenderedAt;
    private string? _pendingText;

    public PartialDisplayUpdate Submit(string text, DateTimeOffset now)
    {
        if (_lastRenderedAt is null || now - _lastRenderedAt >= interval)
        {
            _lastRenderedAt = now;
            _pendingText = null;
            return new PartialDisplayUpdate(text, null);
        }

        _pendingText = text;
        return new PartialDisplayUpdate(
            null,
            interval - (now - _lastRenderedAt.Value));
    }

    public string? Flush(DateTimeOffset now)
    {
        var text = _pendingText;
        if (text is null)
        {
            return null;
        }

        _pendingText = null;
        _lastRenderedAt = now;
        return text;
    }

    public void Reset()
    {
        _lastRenderedAt = null;
        _pendingText = null;
    }
}

public sealed record PartialDisplayUpdate(
    string? ImmediateText,
    TimeSpan? FlushAfter);
