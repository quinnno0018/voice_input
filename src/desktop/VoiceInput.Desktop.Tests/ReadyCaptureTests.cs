using System.IO;
using System.Runtime.CompilerServices;
using VoiceInput.Desktop.Asr;
using VoiceInput.Desktop.Audio;

internal static class ReadyCaptureTests
{
    private static void Check(bool value, string name)
    { if (!value) throw new Exception(name); }

    private static Task<IStreamingAsrProvider> Start(Capture audio, Provider provider,
        CancellationToken token, Action validateTarget) =>
        AudioSessionStartup.AcquireProviderThenStartCaptureAsync(audio, null, () => provider, token, validateTarget);

    public static void WaitForReady()
    {
        var audio = new Capture();
        var provider = new Provider();
        var validated = false;
        var startup = Start(audio, provider, CancellationToken.None, () => validated = true);
        var capturedBeforeReady = audio.Starts;
        provider.Ready.SetResult();
        var acquired = startup.GetAwaiter().GetResult();
        try
        {
            Check(capturedBeforeReady == 0, "Connecting must not fill the microphone queue");
            Check(audio.Starts == 1 && validated && ReferenceEquals(acquired, provider), "Ready target starts exactly once");
        }
        finally { acquired.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    public static void CancelBeforeReady()
    {
        var audio = new Capture();
        var provider = new Provider();
        using var cancellation = new CancellationTokenSource();
        var startup = Start(audio, provider, cancellation.Token, () => { });
        cancellation.Cancel();
        try { startup.GetAwaiter().GetResult(); throw new Exception("Cancelled startup succeeded"); }
        catch (OperationCanceledException) { }
        Check(audio.Starts == 0 && provider.Disposals == 1, "Cancellation must release the connection without opening the microphone");
    }

    public static void ConnectFailure()
    {
        var audio = new Capture();
        var provider = new Provider();
        provider.Ready.SetException(new IOException("fixture connection failure"));
        try { Start(audio, provider, CancellationToken.None, () => { }).GetAwaiter().GetResult(); throw new Exception("Failed connection accepted"); }
        catch (IOException) { }
        Check(audio.Starts == 0 && provider.Disposals == 1, "Failed connection must not start capture");
    }

    public static void TargetChanged()
    {
        var audio = new Capture();
        var provider = new Provider();
        var startup = Start(audio, provider, CancellationToken.None, () => throw new EndOfStreamException());
        provider.Ready.SetResult();
        var rejected = false;
        try { startup.GetAwaiter().GetResult(); }
        catch (EndOfStreamException) { rejected = true; }
        if (!rejected) provider.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Check(rejected && audio.Starts == 0 && provider.Disposals == 1, "Target must be revalidated after the connection wait");
    }

    public static void CaptureFailure()
    {
        var audio = new Capture { Fail = true };
        var provider = new Provider();
        provider.Ready.SetResult();
        try { Start(audio, provider, CancellationToken.None, () => { }).GetAwaiter().GetResult(); throw new Exception("Capture failure swallowed"); }
        catch (IOException) { }
        Check(provider.Disposals == 1, "Failure to open the microphone must release the acquired provider");
    }

    private sealed class Capture : IAudioCaptureControl
    {
        public int Starts { get; private set; }
        public bool Fail { get; init; }
        public void Start() { Starts++; if (Fail) throw new IOException("fixture capture failure"); }
    }

    private sealed class Provider : IStreamingAsrProvider
    {
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Disposals { get; private set; }
        public Task ConnectAsync(CancellationToken token) => Ready.Task.WaitAsync(token);
        public ValueTask SendAudioAsync(ReadOnlyMemory<byte> pcm, CancellationToken token) => ValueTask.CompletedTask;
        public async IAsyncEnumerable<AsrEvent> ReadEventsAsync([EnumeratorCancellation] CancellationToken token)
        { await Task.CompletedTask; yield break; }
        public Task CompleteAsync(CancellationToken token) => Task.CompletedTask;
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }
}
