using System.Threading.Channels;
using NAudio.Wave;

namespace VoiceInput.Desktop.Audio;

public sealed class MicrophoneCapture : IAudioCaptureControl, IDisposable
{
    private readonly AudioCaptureOptions _options;
    private readonly Channel<byte[]> _chunks;
    private readonly PcmChunker _chunker;
    private readonly int _deviceIndex;
    private WaveIn? _waveIn;
    private bool _stopped;

    public MicrophoneCapture(AudioCaptureOptions options, int deviceIndex = -1)
    {
        if (deviceIndex < -1 || deviceIndex >= WaveIn.DeviceCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(deviceIndex),
                $"Input device index must be -1 through {WaveIn.DeviceCount - 1}.");
        }

        _options = options;
        _deviceIndex = deviceIndex;
        _chunker = new PcmChunker(options.BytesPerChunk);
        _chunks = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(50)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

        var capabilities = WaveIn.GetCapabilities(deviceIndex);
        SelectedDevice = new AudioInputDevice(
            deviceIndex,
            capabilities.ProductName,
            capabilities.Channels);
    }

    public ChannelReader<byte[]> Chunks => _chunks.Reader;

    public AudioInputDevice SelectedDevice { get; }

    public AudioCaptureOptions Options => _options;

    public long DroppedChunks { get; private set; }

    public static IReadOnlyList<AudioInputDevice> GetInputDevices()
    {
        var devices = new List<AudioInputDevice>();
        for (var index = -1; index < WaveIn.DeviceCount; index++)
        {
            var capabilities = WaveIn.GetCapabilities(index);
            devices.Add(new AudioInputDevice(
                index,
                capabilities.ProductName,
                capabilities.Channels));
        }

        return devices;
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_stopped, this);
        if (_waveIn is not null)
        {
            throw new InvalidOperationException("Microphone capture is already running.");
        }

        _waveIn = new WaveIn
        {
            DeviceNumber = _deviceIndex,
            WaveFormat = new WaveFormat(
                _options.SampleRate,
                _options.BitsPerSample,
                _options.Channels),
            BufferMilliseconds = _options.ChunkMilliseconds,
            NumberOfBuffers = 3,
        };
        _waveIn.DataAvailable += OnDataAvailable;
        _waveIn.RecordingStopped += OnRecordingStopped;
        _waveIn.StartRecording();
    }

    public void Stop()
    {
        if (_stopped)
        {
            return;
        }

        _stopped = true;
        if (_waveIn is null)
        {
            _chunks.Writer.TryComplete();
            return;
        }

        _waveIn.StopRecording();
    }

    public void Dispose()
    {
        Stop();
        if (_waveIn is null)
        {
            return;
        }

        _waveIn.DataAvailable -= OnDataAvailable;
        _waveIn.RecordingStopped -= OnRecordingStopped;
        _waveIn.Dispose();
        _waveIn = null;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        foreach (var chunk in _chunker.Append(e.Buffer.AsSpan(0, e.BytesRecorded)))
        {
            if (!_chunks.Writer.TryWrite(chunk))
            {
                DroppedChunks++;
            }
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        _chunks.Writer.TryComplete(e.Exception);
    }
}

public sealed record AudioInputDevice(int Index, string Name, int Channels);
