namespace VoiceInput.Desktop.Asr;

public interface IStreamingAsrProvider : IAsyncDisposable
{
    Task ConnectAsync(CancellationToken cancellationToken);

    ValueTask SendAudioAsync(
        ReadOnlyMemory<byte> pcmAudio,
        CancellationToken cancellationToken);

    IAsyncEnumerable<AsrEvent> ReadEventsAsync(
        CancellationToken cancellationToken);

    Task CompleteAsync(CancellationToken cancellationToken);
}
