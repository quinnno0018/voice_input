using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading.Channels;
using System.Windows.Threading;
using Microsoft.Win32.SafeHandles;
using VoiceInput.Desktop.Asr;
using VoiceInput.Desktop.Audio;
using VoiceInput.Desktop.Config;
using VoiceInput.Desktop.Hud;
using VoiceInput.Desktop.Windows;
using Forms = System.Windows.Forms;

namespace VoiceInput.Desktop.Tsf;

internal sealed class TsfAsrHost(AppConfig config, Dispatcher dispatcher) : IDisposable
{
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ForegroundTargetDetector _targets = new();
    private readonly HudWindow _hud = new();
    private Forms.NotifyIcon? _tray;
    private NamedPipeServerStream? _server;
    private Task? _loop;
    private WarmAsrSession? _warm;
    private MicrophoneCapture? _microphone;
    private StreamWriter? _log;
    private long _warmGeneration;

    internal static string PipeName => "VoiceInput.TsfAsr.v1." + WindowsIdentity.GetCurrent().User!.Value;

    public void Start()
    {
        dispatcher.VerifyAccess();
        if (string.IsNullOrWhiteSpace(config.ApiKey))
            throw new AsrProviderException("DASHSCOPE_API_KEY or ASR_API_KEY is required.");
        _server = NewServer();
        var directory = Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(directory);
        _log = new StreamWriter(Path.Combine(directory, "desktop-tsf-asr.log"), append: true) { AutoFlush = true };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Exit", null, async (_, _) =>
        {
            _shutdown.Cancel();
            _server?.Dispose();
            _microphone?.Stop();
            if (_loop is not null) await _loop;
            System.Windows.Application.Current.Shutdown();
        });
        _tray = new Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "Voice Input: TSF ASR (Ctrl+Alt+J)",
            ContextMenuStrip = menu,
            Visible = true,
        };
        Log($"host_started mode=tsf-asr diagnostics=5 capture_policy=after_provider_ready max_sentence_silence_ms={config.MaxSentenceSilenceMilliseconds} "
            + $"warm_retention_seconds={config.TsfWarmSessionSeconds}");
        PrepareWarm();
        _loop = ListenAsync();
    }

    private static NamedPipeServerStream NewServer() => new(PipeName, PipeDirection.InOut, 1,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance);

    private void PrepareWarm()
    {
        if (_shutdown.IsCancellationRequested) return;
        var generation = ++_warmGeneration;
        var warm = new WarmAsrSession(CreateProvider(), config.Audio, TimeSpan.FromSeconds(config.TsfWarmSessionSeconds),
            metadata => Log($"{metadata} warm={generation}"));
        _warm = warm;
        _ = PrepareAsync();
        async Task PrepareAsync()
        {
            try { await warm.PrepareAsync(_shutdown.Token); }
            catch (Exception e) { Log($"warm_unavailable warm={generation} type={e.GetType().Name}"); }
        }
    }

    private IStreamingAsrProvider CreateProvider() => new ParaformerRealtimeProvider(config, Log);

    private async Task ListenAsync()
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                using var pipe = _server!;
                await pipe.WaitForConnectionAsync(_shutdown.Token);
                try { await RunSessionAsync(pipe); }
                catch (Exception e) when (e is IOException or InvalidDataException or OperationCanceledException)
                { Log($"start_rejected {TsfDiagnostics.DescribeFailure(e)}"); }
                pipe.Dispose();
                if (!_shutdown.IsCancellationRequested) _server = NewServer();
            }
        }
        catch (Exception e) when (_shutdown.IsCancellationRequested)
        { Log($"host_stopped type={e.GetType().Name}"); }
        catch (Exception e)
        {
            Log($"host_error type={e.GetType().Name}");
            _hud.ShowError("TSF host stopped. Exit and restart the app.");
        }
        finally
        {
            if (_warm is not null) await _warm.DisposeAsync();
        }
    }

    private async Task RunSessionAsync(NamedPipeServerStream pipe)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        using var helloTimeout = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
        helloTimeout.CancelAfter(TimeSpan.FromSeconds(3));
        var hello = await TsfProtocol.ReadAsync(pipe, helloTimeout.Token);
        var target = _targets.Capture();
        if (hello.Kind != TsfKind.Hello || target is null
            || hello.Window != (ulong)target.WindowHandle.ToInt64()
            || !GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var clientPid)
            || clientPid != target.ProcessId
            || !(target.ProcessName.Equals("notepad", StringComparison.OrdinalIgnoreCase)
                || target.ProcessName.Equals("chrome", StringComparison.OrdinalIgnoreCase)))
            throw TsfDiagnostics.InvalidData(TsfFailureReason.TargetMismatch, "TSF start target did not match the pipe client.");

        Log($"session_start id={hello.Session:N} pid={clientPid}");
        var updates = Channel.CreateBounded<TsfUpdate>(new BoundedChannelOptions(32)
        { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        Task? cloud = null;
        Task? transport = null;
        AudioDebugLog? audioLog = null;
        var warm = _warm;
        _warm = null;
        var error = false;
        try
        {
            transport = PumpUpdatesAsync(pipe, hello, updates.Reader, session.Token,
                (packet, elapsed) => Log($"applied seq={packet.Sequence} segment={packet.Segment} "
                    + $"kind={packet.Kind} length={packet.Text.Length} roundtrip_ms={elapsed.TotalMilliseconds:F2}"));
            dispatcher.VerifyAccess();
            if (!_targets.IsActive(target)) throw new EndOfStreamException();
            _microphone = new MicrophoneCapture(config.Audio, config.AudioDeviceIndex);
            audioLog = AudioDebugLog.TryCreate(_microphone);
            _hud.ShowConnecting();
            cloud = RunCloudAsync(_microphone, warm, audioLog, updates.Writer, target, session.Token);
            await await Task.WhenAny(transport, cloud);
        }
        catch (Exception e) when (e is EndOfStreamException || session.IsCancellationRequested)
        { Log($"session_cancelled id={hello.Session:N}"); }
        catch (Exception e)
        {
            error = true;
            Log($"session_error id={hello.Session:N} {TsfDiagnostics.DescribeFailure(e)} "
                + $"cloud_state={cloud?.Status.ToString() ?? "not_started"} transport_state={transport?.Status.ToString() ?? "not_started"}");
            _hud.ShowError(TsfDiagnostics.UserMessage(e));
        }
        finally
        {
            // Stop capture before waiting for any network or pipe shutdown.
            session.Cancel();
            pipe.Dispose();
            _microphone?.Stop();
            await ObserveAsync(cloud);
            await ObserveAsync(transport);
            if (warm is not null) await warm.DisposeAsync();
            audioLog?.RecordSummary(_microphone?.DroppedChunks ?? 0);
            audioLog?.Dispose();
            _microphone?.Dispose();
            _microphone = null;
            if (!error) _hud.HideHud();
            Log($"session_end id={hello.Session:N}");
            PrepareWarm();
        }
    }

    private async Task RunCloudAsync(MicrophoneCapture audio, WarmAsrSession? warm, AudioDebugLog? audioLog,
        ChannelWriter<TsfUpdate> updates, ForegroundTargetSnapshot target, CancellationToken token)
    {
        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        connectTimeout.CancelAfter(TimeSpan.FromSeconds(12));
        await using var provider = await AudioSessionStartup.AcquireProviderThenStartCaptureAsync(
            audio, warm, CreateProvider, connectTimeout.Token,
            () => { if (!_targets.IsActive(target)) throw new EndOfStreamException(); }, Log);
        token.ThrowIfCancellationRequested();
        audioLog?.RecordProviderReady();
        Log("capture_started provider_ready=True");
        LogAudioQueue("provider_ready", 0, 0);
        _hud.ShowListening();
        using var streaming = CancellationTokenSource.CreateLinkedTokenSource(token);
        var sender = SendAudioAsync();
        var receiver = ReceiveAsync();
        try { await await Task.WhenAny(sender, receiver); }
        finally
        {
            streaming.Cancel();
            await ObserveAsync(sender);
            await ObserveAsync(receiver);
        }
        throw new AsrProviderException("ASR stream ended unexpectedly.");

        async Task SendAudioAsync()
        {
            long sentChunks = 0;
            await foreach (var chunk in audio.Chunks.ReadAllAsync(streaming.Token))
            {
                audioLog?.RecordCapturedChunk(chunk);
                var started = DateTimeOffset.UtcNow;
                var clock = Stopwatch.GetTimestamp();
                await provider.SendAudioAsync(chunk, streaming.Token);
                var elapsed = Stopwatch.GetElapsedTime(clock);
                audioLog?.RecordAudioSent(chunk.Length, started, elapsed);
                sentChunks++;
                if (sentChunks == 1 || elapsed.TotalMilliseconds >= 100 || audio.DroppedChunks != 0)
                    LogAudioQueue("after_send", sentChunks, elapsed.TotalMilliseconds);
                EnsureAudioNotDropped(audio.DroppedChunks);
            }
        }

        void LogAudioQueue(string phase, long sentChunks, double sendDuration)
        {
            var queued = audio.Chunks.Count;
            Log(FormattableString.Invariant($"audio_queue phase={phase} sent_chunk_count={sentChunks} queued_chunks={queued} queued_audio_ms={queued * config.Audio.ChunkMilliseconds} send_total_ms={sendDuration:F3} dropped_audio_chunks={audio.DroppedChunks}"));
        }

        async Task ReceiveAsync()
        {
            var segments = new TranscriptSegments();
            await foreach (var value in provider.ReadEventsAsync(streaming.Token))
            {
                if (value is AsrError) throw new AsrProviderException("ASR provider reported an error.");
                var update = segments.Accept(value, Log);
                if (update is null) continue;
                streaming.Token.ThrowIfCancellationRequested();
                if (!updates.TryWrite(update))
                    throw TsfDiagnostics.QueueOverflow(TsfFailureReason.TranscriptQueueOverflow);
                Log($"asr_received segment={update.Segment} kind={update.Kind} length={update.Text.Length}");
                _hud.ShowPartial(update.Text);
            }
        }
    }

    internal static void EnsureAudioNotDropped(long droppedChunks)
    {
        if (droppedChunks != 0) throw TsfDiagnostics.QueueOverflow(TsfFailureReason.AudioQueueOverflow);
    }

    internal static async Task PumpUpdatesAsync(Stream pipe, TsfPacket hello, ChannelReader<TsfUpdate> updates,
        CancellationToken token, Action<TsfPacket, TimeSpan>? applied = null)
    {
        ulong sequence = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (!updates.TryRead(out var update))
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
                idle.CancelAfter(TimeSpan.FromMilliseconds(250));
                try
                {
                    if (!await updates.WaitToReadAsync(idle.Token)) throw new EndOfStreamException();
                    updates.TryRead(out update);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
            }
            var packet = new TsfPacket(update?.Kind ?? TsfKind.Ping, hello.Session, ++sequence,
                update?.Segment ?? 0, hello.Window, update?.Text ?? "");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            var clock = Stopwatch.GetTimestamp();
            TsfPacket ack;
            try
            {
                await pipe.WriteAsync(TsfProtocol.Encode(packet), deadline.Token);
                ack = await TsfProtocol.ReadAsync(pipe, deadline.Token);
            }
            catch (IOException exception)
            {
                throw new EndOfStreamException("TSF target disconnected.", exception);
            }
            TsfProtocol.ValidateAck(packet, ack);
            if (update is not null) applied?.Invoke(packet, Stopwatch.GetElapsedTime(clock));
        }
    }

    private static async Task ObserveAsync(Task? task)
    {
        if (task is null) return;
        try { await task; } catch (Exception) { /* First failing task is reported by its owner. */ }
    }

    private void Log(string metadata) => _log?.WriteLine($"{DateTimeOffset.UtcNow:O} {metadata}");

    public void Dispose()
    {
        _shutdown.Cancel();
        _server?.Dispose();
        _microphone?.Stop();
        _tray?.Dispose();
        _hud.Close();
        // The normal tray exit awaits the loop before disposing the log.
        if (_loop?.IsCompleted != false) { _log?.Dispose(); _log = null; }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);
}
