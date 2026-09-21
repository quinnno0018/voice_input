using System.Buffers.Binary;

namespace VoiceInput.Desktop.Audio;

public sealed record Pcm16AudioStatistics(
    int ByteCount,
    int SampleCount,
    double Rms,
    double Peak)
{
    public static Pcm16AudioStatistics Analyze(ReadOnlySpan<byte> pcmAudio)
    {
        if (pcmAudio.IsEmpty || pcmAudio.Length % sizeof(short) != 0)
        {
            throw new ArgumentException(
                "PCM16 audio must contain a non-empty, even number of bytes.",
                nameof(pcmAudio));
        }

        double sumOfSquares = 0;
        var peak = 0;
        for (var offset = 0; offset < pcmAudio.Length; offset += sizeof(short))
        {
            var sample = BinaryPrimitives.ReadInt16LittleEndian(
                pcmAudio.Slice(offset, sizeof(short)));
            var magnitude = Math.Abs((int)sample);
            peak = Math.Max(peak, magnitude);
            sumOfSquares += (double)sample * sample;
        }

        var sampleCount = pcmAudio.Length / sizeof(short);
        return new Pcm16AudioStatistics(
            pcmAudio.Length,
            sampleCount,
            Math.Sqrt(sumOfSquares / sampleCount) / 32768.0,
            peak / 32768.0);
    }
}
