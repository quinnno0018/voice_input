namespace VoiceInput.Desktop.Audio;

public sealed record AudioCaptureOptions(
    int SampleRate,
    int Channels,
    int BitsPerSample,
    int ChunkMilliseconds)
{
    public static AudioCaptureOptions WindowsMvp { get; } = new(16000, 1, 16, 100);

    public int SamplesPerChunk =>
        checked(SampleRate * ChunkMilliseconds / 1000);

    public int BytesPerChunk =>
        checked(SamplesPerChunk * Channels * (BitsPerSample / 8));

    public void ValidatePcmChunk(ReadOnlyMemory<byte> pcmAudio)
    {
        if (SampleRate != 16000
            || Channels != 1
            || BitsPerSample != 16
            || ChunkMilliseconds != 100)
        {
            throw new InvalidOperationException(
                "Desktop ASR audio must be 16 kHz, mono, signed PCM16, 100 ms.");
        }

        if (pcmAudio.Length != BytesPerChunk)
        {
            throw new ArgumentException(
                $"PCM chunk must contain exactly {BytesPerChunk} bytes; got {pcmAudio.Length}.",
                nameof(pcmAudio));
        }
    }
}
