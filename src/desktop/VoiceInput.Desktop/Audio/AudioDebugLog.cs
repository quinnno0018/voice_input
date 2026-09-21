using System.IO;
using System.Text;

namespace VoiceInput.Desktop.Audio;

public sealed class AudioDebugLog : IDisposable
{
    private readonly StreamWriter _writer;
    private long _capturedChunkCount;
    private long _sentChunkCount;
    private long _totalAudioBytesSent;

    private AudioDebugLog(MicrophoneCapture microphone)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(directory);
        LogPath = Path.Combine(directory, "desktop-audio-debug.log");
        _writer = new StreamWriter(
            LogPath,
            append: true,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        Write($"session_start selected_device_index={microphone.SelectedDevice.Index} "
            + $"selected_device_name={microphone.SelectedDevice.Name} "
            + $"selected_device_channels={microphone.SelectedDevice.Channels}");
        Write("available_devices="
            + string.Join(
                " | ",
                MicrophoneCapture.GetInputDevices()
                    .Select(device =>
                        $"index={device.Index},name={device.Name},channels={device.Channels}")));
        Write($"audio_format sample_rate={microphone.Options.SampleRate} "
            + $"channels={microphone.Options.Channels} "
            + $"bits_per_sample={microphone.Options.BitsPerSample} "
            + $"encoding=pcm_signed_little_endian "
            + $"chunk_ms={microphone.Options.ChunkMilliseconds} "
            + $"samples_per_chunk={microphone.Options.SamplesPerChunk} "
            + $"bytes_per_chunk={microphone.Options.BytesPerChunk}");
        _writer.Flush();
    }

    public string LogPath { get; }

    public static AudioDebugLog? TryCreate(MicrophoneCapture microphone)
    {
        try
        {
            return new AudioDebugLog(microphone);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void RecordProviderReady() =>
        Write("provider_ready");

    public void RecordCapturedChunk(ReadOnlySpan<byte> pcmAudio)
    {
        var statistics = Pcm16AudioStatistics.Analyze(pcmAudio);
        _capturedChunkCount++;
        Write($"chunk_captured index={_capturedChunkCount} "
            + $"bytes={statistics.ByteCount} samples={statistics.SampleCount} "
            + $"rms={statistics.Rms:F6} peak={statistics.Peak:F6}");
    }

    public void RecordAudioSent(
        int byteCount,
        DateTimeOffset startedAt,
        TimeSpan duration)
    {
        _sentChunkCount++;
        _totalAudioBytesSent += byteCount;
        if (_sentChunkCount == 1)
        {
            Write($"first_audio_send_timestamp={startedAt:O}");
        }

        Write($"chunk_sent index={_sentChunkCount} bytes={byteCount} "
            + $"send_duration_ms={duration.TotalMilliseconds:F3} "
            + $"total_audio_bytes_sent={_totalAudioBytesSent}");
    }

    public void RecordSummary(long droppedChunks)
    {
        Write($"session_summary captured_chunk_count={_capturedChunkCount} "
            + $"sent_chunk_count={_sentChunkCount} "
            + $"total_audio_bytes_sent={_totalAudioBytesSent} "
            + $"dropped_audio_chunks={droppedChunks}");
        _writer.Flush();
    }

    public void Dispose()
    {
        _writer.Flush();
        _writer.Dispose();
    }

    private void Write(string message) =>
        _writer.WriteLine($"{DateTimeOffset.UtcNow:O} {message}");
}
