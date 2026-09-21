using System.Collections.Concurrent;
using System.Text.Json;
using VoiceInput.Desktop.Asr;
using VoiceInput.Desktop.Audio;
using VoiceInput.Desktop.Config;
using VoiceInput.Desktop.Hotkey;
using VoiceInput.Desktop.Hud;
using VoiceInput.Desktop.Windows;

if (args.Length > 0 && args[0] == "--send-probe")
    return await SendProbe.RunAsync(args[1..]);

var tests = new (string Name, Action Run)[]
{
    ("Send probe observes late finish without cancellation or retry", SendProbeTests.FinishObservation),
    ("Network probe excludes nonnumeric event payloads", SendProbeTests.NumericTelemetry),
    ("AppConfig uses the Windows MVP baseline", TestAppConfigDefaults),
    ("TSF warm retention configuration rejects unbounded or invalid values", TestTsfWarmConfig),
    ("AppConfig accepts only the three sentence silence test values", TestSentenceSilenceConfig),
    ("Paraformer run-task uses bilingual low-latency VAD parameters", TestParaformerRunTaskParameters),
    ("HotkeyGesture parses configurable shortcuts", TestHotkeyGesture),
    ("Paraformer partial events stay provider-neutral", TestPartialEvent),
    ("Paraformer stable events stay provider-neutral", TestStableEvent),
    ("Live TSF requires neutral segment identity and an isolated bridge", TestLiveBoundary),
    ("TSF wire rejects malformed frames and stale acknowledgments", TsfBridgeTests.Codec),
    ("TSF segments isolate revisions and duplicate finals", TsfBridgeTests.Segments),
    ("TSF invalid data identifies the failing guard without transcript text", TsfBridgeTests.FailureReasons),
    ("Paraformer diagnostics preserve events and exclude payload content", TsfBridgeTests.ParserDiagnostics),
    ("TSF segment diagnostics include rejected and duplicate events", TsfBridgeTests.SegmentDiagnostics),
    ("TSF pipe cancellation discards a queued stable", TsfBridgeTests.PipeCancellation),
    ("TSF mismatched pipe acknowledgment reports a reason and stops writes", TsfBridgeTests.PipeInvalidAck),
    ("ASR send diagnostics separate UI scheduling from task completion", SendDiagnosticsTests.DelayedContinuation),
    ("ASR send diagnostics measure a pending send", SendDiagnosticsTests.DelayedSend),
    ("ASR send diagnostics preserve faults and cancellation", SendDiagnosticsTests.Failures),
    ("ASR send diagnostics cannot fail a successful send", SendDiagnosticsTests.BrokenSink),
    ("TSF audio backlog stops with a specific safe message", SendDiagnosticsTests.AudioOverflow),
    ("Paraformer failures become AsrError", TestErrorEvent),
    ("Paraformer implements the streaming provider boundary", TestProviderBoundary),
    ("PCM chunker emits exact 100 ms blocks", TestPcmChunker),
    ("PCM diagnostics interpret signed little-endian samples", TestPcmDiagnostics),
    ("Audio payload validation requires one exact PCM chunk", TestAudioPayloadValidation),
    ("Audio device selection defaults to the Windows mapper", TestDefaultAudioDevice),
    ("Audio device selection accepts an explicit NAudio index", TestExplicitAudioDevice),
    ("Audio capture starts before the ASR provider connects", TestCaptureStartsBeforeConnect),
    ("TSF capture waits until the provider is ready", ReadyCaptureTests.WaitForReady),
    ("TSF cancelled startup never captures audio", ReadyCaptureTests.CancelBeforeReady),
    ("TSF failed startup never captures audio", ReadyCaptureTests.ConnectFailure),
    ("TSF target loss during connection never starts capture", ReadyCaptureTests.TargetChanged),
    ("TSF capture failure releases its acquired provider", ReadyCaptureTests.CaptureFailure),
    ("Warm ASR session sends silence until claimed", TestWarmSessionClaim),
    ("Warm ASR session stops and disposes after its lifetime", TestWarmSessionExpiration),
    ("Warm ASR failure falls back to a cold provider", TestWarmSessionFallback),
    ("Failed warm audio send releases the provider before cold fallback", TestWarmSendFailureFallback),
    ("Failed warm audio send still permits disposal", TestWarmSendFailureDisposal),
    ("Expired warm session reports cold fallback and releases once", TestExpiredWarmFallback),
    ("Claimed warm session survives its former expiry timer", TestClaimedWarmSurvivesExpiry),
    ("Failed warm audio send is still cleaned up on expiry", TestFailedWarmExpiry),
    ("Cancelled acquisition never starts a cold connection", TestCancelledWarmAcquisition),
    ("HUD partial coalescing is immediate then trailing", TestPartialCoalescing),
    ("SendInput INPUT uses the native platform size", TestSendInputStructureSize),
    ("Text injection skips a target that lost focus", TestInjectionSkipsInactiveTarget),
    ("Text injection restores clipboard after paste failure", TestInjectionRestoresClipboard),
};

if (args.Length == 2 && args[0] == "--native-tsf-test")
    tests = tests.Append(("C# to native pipe handshake and two segments", (Action)(() => TsfBridgeTests.NativePipe(args[1])))).ToArray();

var failures = 0;
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"[PASS] {test.Name}");
    }
    catch (Exception exception)
    {
        failures++;
        Console.Error.WriteLine($"[FAIL] {test.Name}: {exception.Message}");
    }
}

Console.WriteLine($"Tests run: {tests.Length}; failures: {failures}");
return failures == 0 ? 0 : 1;

static void TestAppConfigDefaults()
{
    var values = new Dictionary<string, string?>
    {
        ["DASHSCOPE_API_KEY"] = "test-key",
    };
    var config = AppConfig.Load(name => values.GetValueOrDefault(name));

    Equal("dashscope_paraformer_realtime", config.Provider);
    Equal("paraformer-realtime-v2", config.Model);
    Equal(16000, config.Audio.SampleRate);
    Equal(1, config.Audio.Channels);
    Equal(16, config.Audio.BitsPerSample);
    Equal(100, config.Audio.ChunkMilliseconds);
    Equal(1200, config.MaxSentenceSilenceMilliseconds);
    Equal("test-key", config.ApiKey);
}

static void TestSentenceSilenceConfig()
{
    foreach (var value in new[] { 800, 1200, 1600 })
    {
        var values = new Dictionary<string, string?>
        {
            ["VOICE_INPUT_MAX_SENTENCE_SILENCE_MS"] = value.ToString(),
        };

        var config = AppConfig.Load(name => values.GetValueOrDefault(name));
        Equal(value, config.MaxSentenceSilenceMilliseconds);
    }

    Throws<FormatException>(() => AppConfig.Load(name =>
        name == "VOICE_INPUT_MAX_SENTENCE_SILENCE_MS" ? "1000" : null));
}

static void TestTsfWarmConfig()
{
    Equal(180, AppConfig.Load(_ => null).TsfWarmSessionSeconds);
    foreach (var seconds in new[] { 30, 60, 180, 300 })
    {
        var config = AppConfig.Load(name => name == "VOICE_INPUT_TSF_WARM_SECONDS" ? $" {seconds} " : null);
        Equal(seconds, config.TsfWarmSessionSeconds);
        Equal(1200, config.MaxSentenceSilenceMilliseconds);
        Equal(100, config.Audio.ChunkMilliseconds);
    }
    foreach (var value in new[] { "0", "29", "301", "-1", "abc", "180.5" })
        Throws<FormatException>(() => AppConfig.Load(name =>
            name == "VOICE_INPUT_TSF_WARM_SECONDS" ? value : null));
}

static void TestParaformerRunTaskParameters()
{
    var values = new Dictionary<string, string?>
    {
        ["DASHSCOPE_API_KEY"] = "test-key",
        ["VOICE_INPUT_MAX_SENTENCE_SILENCE_MS"] = "1600",
    };
    var config = AppConfig.Load(name => values.GetValueOrDefault(name));

    using var json = JsonDocument.Parse(
        ParaformerRealtimeProvider.BuildRunTask(config, "test-task"));
    var parameters = json.RootElement
        .GetProperty("payload")
        .GetProperty("parameters");

    Equal(1600, parameters.GetProperty("max_sentence_silence").GetInt32());
    False(parameters.GetProperty("semantic_punctuation_enabled").GetBoolean());
    True(parameters.GetProperty("heartbeat").GetBoolean());
    var languages = parameters.GetProperty("language_hints")
        .EnumerateArray()
        .Select(item => item.GetString())
        .ToArray();
    ListEqual<string?>(new[] { "zh", "en" }, languages);
}

static void TestHotkeyGesture()
{
    var gesture = HotkeyGesture.Parse("Ctrl+Shift+F9");

    Equal(HotkeyModifiers.Control | HotkeyModifiers.Shift, gesture.Modifiers);
    Equal(0x78u, gesture.VirtualKey);
}

static void TestPartialEvent()
{
    var parsed = ParaformerEventParser.Parse("""
        {"header":{"event":"result-generated"},"payload":{"output":{"sentence":{"text":"live","sentence_end":false}}}}
        """);

    var transcript = IsType<PartialTranscript>(parsed);
    Equal("live", transcript.Text);
}

static void TestStableEvent()
{
    var parsed = ParaformerEventParser.Parse("""
        {"header":{"event":"result-generated"},"payload":{"output":{"sentence":{"text":"live speech.","sentence_end":true}}}}
        """);

    var transcript = IsType<StableTranscript>(parsed);
    Equal("live speech.", transcript.Text);
}

static void TestErrorEvent()
{
    var parsed = ParaformerEventParser.Parse("""
        {"header":{"event":"task-failed","error_message":"invalid request"}}
        """);

    var error = IsType<AsrError>(parsed);
    Equal("invalid request", error.Message);
}

static void TestLiveBoundary()
{
    var transcript = ParaformerEventParser.Parse("""
        {"header":{"event":"result-generated"},"payload":{"output":{"sentence":{"begin_time":170,"text":"live","sentence_end":false}}}}
        """);
    Equal("170", transcript.GetType().GetProperty("SegmentId")?.GetValue(transcript) as string);
    True(typeof(PartialTranscript).Assembly.GetType("VoiceInput.Desktop.Tsf.TsfAsrHost") is not null);
}

static void TestProviderBoundary()
{
    True(typeof(IStreamingAsrProvider).IsAssignableFrom(typeof(ParaformerRealtimeProvider)));
}

static void TestPcmChunker()
{
    var chunker = new PcmChunker(3200);
    var input = Enumerable.Range(0, 6400).Select(index => (byte)(index % 251)).ToArray();

    var first = chunker.Append(input.AsSpan(0, 1000));
    var second = chunker.Append(input.AsSpan(1000));

    Equal(0, first.Count);
    Equal(2, second.Count);
    SequenceEqual(input.AsSpan(0, 3200), second[0]);
    SequenceEqual(input.AsSpan(3200, 3200), second[1]);
}

static void TestPcmDiagnostics()
{
    var audio = new byte[]
    {
        0x00, 0x40,
        0x00, 0xC0,
    };

    var statistics = Pcm16AudioStatistics.Analyze(audio);

    Equal(2, statistics.SampleCount);
    Near(0.5, statistics.Rms);
    Near(0.5, statistics.Peak);
}

static void TestAudioPayloadValidation()
{
    var options = AudioCaptureOptions.WindowsMvp;

    Equal(1600, options.SamplesPerChunk);
    Equal(3200, options.BytesPerChunk);
    options.ValidatePcmChunk(new byte[3200]);

    Throws<ArgumentException>(() => options.ValidatePcmChunk(Array.Empty<byte>()));
    Throws<ArgumentException>(() => options.ValidatePcmChunk(new byte[3198]));
}

static void TestDefaultAudioDevice()
{
    var values = new Dictionary<string, string?>
    {
        ["DASHSCOPE_API_KEY"] = "test-key",
    };

    var config = AppConfig.Load(name => values.GetValueOrDefault(name));

    Equal(-1, config.AudioDeviceIndex);
}

static void TestExplicitAudioDevice()
{
    var values = new Dictionary<string, string?>
    {
        ["DASHSCOPE_API_KEY"] = "test-key",
        ["VOICE_INPUT_AUDIO_DEVICE_INDEX"] = "3",
    };

    var config = AppConfig.Load(name => values.GetValueOrDefault(name));

    Equal(3, config.AudioDeviceIndex);
}

static void TestCaptureStartsBeforeConnect()
{
    var calls = new List<string>();
    var audio = new FakeAudioCaptureControl(calls);
    var provider = new FakeStreamingAsrProvider(calls);

    AudioSessionStartup.StartCaptureThenAcquireProviderAsync(
            audio,
            null,
            () => provider,
            CancellationToken.None)
        .GetAwaiter()
        .GetResult();

    ListEqual(
        new[] { "audio-start", "provider-connect" },
        calls);
}

static void TestWarmSessionClaim()
{
    var provider = new RecordingStreamingAsrProvider();
    var warm = new WarmAsrSession(
        provider,
        AudioCaptureOptions.WindowsMvp,
        TimeSpan.FromSeconds(5));

    warm.PrepareAsync(CancellationToken.None).GetAwaiter().GetResult();
    SpinWait.SpinUntil(() => provider.AudioChunks.Count > 0, 1000);

    var claimed = warm.ClaimAsync(CancellationToken.None).GetAwaiter().GetResult();
    var countAfterClaim = provider.AudioChunks.Count;
    Thread.Sleep(150);

    True(ReferenceEquals(provider, claimed));
    True(countAfterClaim > 0);
    Equal(countAfterClaim, provider.AudioChunks.Count);
    True(provider.AudioChunks.All(chunk =>
        chunk.Length == 3200 && chunk.All(value => value == 0)));

    claimed.DisposeAsync().AsTask().GetAwaiter().GetResult();
    warm.DisposeAsync().AsTask().GetAwaiter().GetResult();
}

static void TestWarmSessionFallback()
{
    var failedWarmProvider = new RecordingStreamingAsrProvider(failConnect: true);
    var fallbackProvider = new RecordingStreamingAsrProvider();
    var warm = new WarmAsrSession(
        failedWarmProvider,
        AudioCaptureOptions.WindowsMvp,
        TimeSpan.FromSeconds(5));

    var acquired = AsrProviderAcquisition.AcquireAsync(
            warm,
            () => fallbackProvider,
            CancellationToken.None)
        .GetAwaiter()
        .GetResult();

    True(ReferenceEquals(fallbackProvider, acquired));
    Equal(1, fallbackProvider.ConnectCount);
    True(failedWarmProvider.Disposed);

    acquired.DisposeAsync().AsTask().GetAwaiter().GetResult();
}

static void TestWarmSessionExpiration()
{
    var provider = new RecordingStreamingAsrProvider();
    var warm = new WarmAsrSession(
        provider,
        AudioCaptureOptions.WindowsMvp,
        TimeSpan.FromMilliseconds(120));

    warm.PrepareAsync(CancellationToken.None).GetAwaiter().GetResult();
    True(SpinWait.SpinUntil(() => provider.Disposed, 1000));
    var countAfterExpiration = provider.AudioChunks.Count;
    Thread.Sleep(150);

    Equal(countAfterExpiration, provider.AudioChunks.Count);
    warm.DisposeAsync().AsTask().GetAwaiter().GetResult();
}

static void TestWarmSendFailureFallback()
{
    var failed = new RecordingStreamingAsrProvider(failSend: true);
    var fallback = new RecordingStreamingAsrProvider();
    var warm = new WarmAsrSession(failed, AudioCaptureOptions.WindowsMvp, TimeSpan.FromSeconds(5));
    var trace = new ConcurrentQueue<string>();
    try
    {
        warm.PrepareAsync(CancellationToken.None).GetAwaiter().GetResult();
        var acquired = AsrProviderAcquisition.AcquireAsync(warm, () => fallback, CancellationToken.None, trace.Enqueue)
            .GetAwaiter().GetResult();
        True(ReferenceEquals(fallback, acquired));
        True(failed.Disposed);
        Equal(1, failed.DisposeCount);
        True(trace.Contains("asr_connection mode=cold reason=unavailable"));
        False(trace.Any(line => line.Contains("warm silence send failed")));
    }
    finally
    {
        warm.DisposeAsync().AsTask().GetAwaiter().GetResult();
        fallback.DisposeAsync().AsTask().GetAwaiter().GetResult();
        if (!failed.Disposed) failed.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}

static void TestWarmSendFailureDisposal()
{
    var provider = new RecordingStreamingAsrProvider(failSend: true);
    var warm = new WarmAsrSession(provider, AudioCaptureOptions.WindowsMvp, TimeSpan.FromSeconds(5));
    try
    {
        warm.PrepareAsync(CancellationToken.None).GetAwaiter().GetResult();
        warm.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Equal(1, provider.DisposeCount);
    }
    finally
    {
        if (!provider.Disposed) provider.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}

static void TestExpiredWarmFallback()
{
    var provider = new RecordingStreamingAsrProvider();
    var fallback = new RecordingStreamingAsrProvider();
    var trace = new ConcurrentQueue<string>();
    var warm = new WarmAsrSession(provider, AudioCaptureOptions.WindowsMvp, TimeSpan.FromMilliseconds(80), trace.Enqueue);
    try
    {
        warm.PrepareAsync(CancellationToken.None).GetAwaiter().GetResult();
        True(SpinWait.SpinUntil(() => provider.Disposed, 2000));
        var acquired = AsrProviderAcquisition.AcquireAsync(warm, () => fallback, CancellationToken.None, trace.Enqueue)
            .GetAwaiter().GetResult();
        True(ReferenceEquals(fallback, acquired));
        Equal(1, fallback.ConnectCount);
        Equal(1, provider.DisposeCount);
        True(trace.Contains("warm_expired"));
        True(trace.Contains("asr_connection mode=cold reason=expired"));
    }
    finally
    {
        warm.DisposeAsync().AsTask().GetAwaiter().GetResult();
        fallback.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}

static void TestClaimedWarmSurvivesExpiry()
{
    var provider = new RecordingStreamingAsrProvider();
    var trace = new ConcurrentQueue<string>();
    void Diagnostics(string value) { trace.Enqueue(value); throw new InvalidOperationException("diagnostic sink failed"); }
    var warm = new WarmAsrSession(provider, AudioCaptureOptions.WindowsMvp, TimeSpan.FromMilliseconds(200), Diagnostics);
    try
    {
        warm.PrepareAsync(CancellationToken.None).GetAwaiter().GetResult();
        var acquired = AsrProviderAcquisition.AcquireAsync(warm,
            () => throw new Exception("Unexpected cold connection"), CancellationToken.None, Diagnostics)
            .GetAwaiter().GetResult();
        var sent = provider.AudioChunks.Count;
        Thread.Sleep(300);
        True(ReferenceEquals(provider, acquired));
        Equal(1, provider.ConnectCount);
        Equal(sent, provider.AudioChunks.Count);
        False(provider.Disposed);
        True(trace.Any(line => line.StartsWith("warm_ready ")));
        True(trace.Contains("warm_claimed"));
        True(trace.Contains("asr_connection mode=warm"));
        False(trace.Contains("warm_expired"));
        warm.DisposeAsync().AsTask().GetAwaiter().GetResult();
        False(provider.Disposed);
    }
    finally { provider.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
}

static void TestFailedWarmExpiry()
{
    var provider = new RecordingStreamingAsrProvider(failSend: true);
    var warm = new WarmAsrSession(provider, AudioCaptureOptions.WindowsMvp, TimeSpan.FromMilliseconds(80));
    warm.PrepareAsync(CancellationToken.None).GetAwaiter().GetResult();
    True(SpinWait.SpinUntil(() => provider.Disposed, 2000));
    warm.DisposeAsync().AsTask().GetAwaiter().GetResult();
    Equal(1, provider.DisposeCount);
}

static void TestCancelledWarmAcquisition()
{
    var provider = new RecordingStreamingAsrProvider();
    var warm = new WarmAsrSession(provider, AudioCaptureOptions.WindowsMvp, TimeSpan.FromSeconds(5));
    using var cancelled = new CancellationTokenSource();
    warm.PrepareAsync(CancellationToken.None).GetAwaiter().GetResult();
    cancelled.Cancel();
    var created = false;
    Throws<OperationCanceledException>(() => AsrProviderAcquisition.AcquireAsync(warm,
        () => { created = true; return new RecordingStreamingAsrProvider(); }, cancelled.Token).GetAwaiter().GetResult());
    False(created);
    warm.DisposeAsync().AsTask().GetAwaiter().GetResult();
    Equal(1, provider.DisposeCount);
}

static void TestPartialCoalescing()
{
    var coalescer = new PartialTranscriptCoalescer(TimeSpan.FromMilliseconds(75));
    var origin = DateTimeOffset.UnixEpoch;

    var first = coalescer.Submit("a", origin);
    var second = coalescer.Submit("ab", origin.AddMilliseconds(20));
    var third = coalescer.Submit("abc", origin.AddMilliseconds(40));

    Equal("a", first.ImmediateText);
    Equal<string?>(null, second.ImmediateText);
    Equal<string?>(null, third.ImmediateText);
    Equal("abc", coalescer.Flush(origin.AddMilliseconds(75)));
    Equal<string?>(null, coalescer.Flush(origin.AddMilliseconds(150)));

    coalescer.Submit("stale", origin.AddMilliseconds(160));
    coalescer.Reset();
    Equal<string?>(null, coalescer.Flush(origin.AddMilliseconds(300)));
}

static void TestSendInputStructureSize()
{
    var expected = IntPtr.Size == 8 ? 40 : 28;

    Equal(expected, SendInputPasteSender.InputStructureSize);
}

static void TestInjectionSkipsInactiveTarget()
{
    var target = new ForegroundTargetSnapshot((nint)123, 1, "notepad", "Notes");
    var clipboard = new FakeClipboardStore("original");
    var paste = new FakePasteSender(true);
    var injector = new ClipboardTextInjector(
        new FakeTargetDetector(target, false),
        clipboard,
        paste,
        TimeSpan.Zero);

    var result = injector.InsertAsync(target, "dictation", CancellationToken.None)
        .GetAwaiter().GetResult();

    False(result.Success);
    Equal(0, clipboard.SetTextCount);
    Equal(0, paste.SendCount);
}

static void TestInjectionRestoresClipboard()
{
    var target = new ForegroundTargetSnapshot((nint)123, 1, "notepad", "Notes");
    var clipboard = new FakeClipboardStore("original");
    var paste = new FakePasteSender(false);
    var injector = new ClipboardTextInjector(
        new FakeTargetDetector(target, true),
        clipboard,
        paste,
        TimeSpan.Zero);

    var result = injector.InsertAsync(target, "dictation", CancellationToken.None)
        .GetAwaiter().GetResult();

    False(result.Success);
    Equal("original", clipboard.Current);
    Equal(1, clipboard.RestoreCount);
}

static T IsType<T>(object? value)
{
    if (value is not T typed)
    {
        throw new InvalidOperationException(
            $"Expected {typeof(T).Name}, got {value?.GetType().Name ?? "null"}.");
    }

    return typed;
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }
}

static void True(bool value)
{
    if (!value)
    {
        throw new InvalidOperationException("Expected true, got false.");
    }
}

static void False(bool value) => True(!value);

static void Near(double expected, double actual, double tolerance = 0.000001)
{
    if (Math.Abs(expected - actual) > tolerance)
    {
        throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }
}

static void Throws<TException>(Action action)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}

static void SequenceEqual(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual)
{
    if (!expected.SequenceEqual(actual))
    {
        throw new InvalidOperationException("Byte sequences differ.");
    }
}

static void ListEqual<T>(
    IReadOnlyList<T> expected,
    IReadOnlyList<T> actual)
{
    if (!expected.SequenceEqual(actual))
    {
        throw new InvalidOperationException(
            $"Expected {string.Join(",", expected)}, got {string.Join(",", actual)}.");
    }
}

sealed class FakeTargetDetector(
    ForegroundTargetSnapshot target,
    bool active) : IForegroundTargetDetector
{
    public ForegroundTargetSnapshot? Capture() => target;

    public bool IsActive(ForegroundTargetSnapshot candidate) =>
        active && candidate.WindowHandle == target.WindowHandle;
}

sealed class FakeClipboardStore(object? initial) : IClipboardStore
{
    public object? Current { get; private set; } = initial;
    public int SetTextCount { get; private set; }
    public int RestoreCount { get; private set; }

    public object? Capture() => Current;

    public void SetText(string text)
    {
        SetTextCount++;
        Current = text;
    }

    public void Restore(object? snapshot)
    {
        RestoreCount++;
        Current = snapshot;
    }
}

sealed class FakePasteSender(bool succeeds) : IPasteSender
{
    public int SendCount { get; private set; }

    public bool SendPaste()
    {
        SendCount++;
        return succeeds;
    }
}

sealed class FakeAudioCaptureControl(List<string> calls) : IAudioCaptureControl
{
    public void Start() => calls.Add("audio-start");
}

sealed class FakeStreamingAsrProvider(List<string> calls) : IStreamingAsrProvider
{
    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        calls.Add("provider-connect");
        return Task.CompletedTask;
    }

    public ValueTask SendAudioAsync(
        ReadOnlyMemory<byte> pcmAudio,
        CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    public IAsyncEnumerable<AsrEvent> ReadEventsAsync(
        CancellationToken cancellationToken) =>
        EmptyEvents();

    public Task CompleteAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static async IAsyncEnumerable<AsrEvent> EmptyEvents()
    {
        await Task.CompletedTask;
        yield break;
    }
}

sealed class RecordingStreamingAsrProvider(bool failConnect = false, bool failSend = false)
    : IStreamingAsrProvider
{
    private readonly object _sync = new();

    public int ConnectCount { get; private set; }
    public bool Disposed { get; private set; }
    public int DisposeCount { get; private set; }
    public List<byte[]> AudioChunks { get; } = [];

    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        ConnectCount++;
        return failConnect
            ? Task.FromException(new AsrProviderException("warm-up failed"))
            : Task.CompletedTask;
    }

    public ValueTask SendAudioAsync(
        ReadOnlyMemory<byte> pcmAudio,
        CancellationToken cancellationToken)
    {
        if (failSend) return ValueTask.FromException(new AsrProviderException("warm silence send failed"));
        lock (_sync)
        {
            AudioChunks.Add(pcmAudio.ToArray());
        }

        return ValueTask.CompletedTask;
    }

    public IAsyncEnumerable<AsrEvent> ReadEventsAsync(
        CancellationToken cancellationToken) => EmptyEvents();

    public Task CompleteAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        Disposed = true;
        return ValueTask.CompletedTask;
    }

    private static async IAsyncEnumerable<AsrEvent> EmptyEvents()
    {
        await Task.CompletedTask;
        yield break;
    }
}
