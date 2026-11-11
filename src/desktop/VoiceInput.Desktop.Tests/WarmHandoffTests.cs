using VoiceInput.Desktop.Asr;
using VoiceInput.Desktop.Audio;

internal static class WarmHandoffTests
{
    public static void Drain() => RunAsync("drain").GetAwaiter().GetResult();
    public static void Timeout() => RunAsync("timeout").GetAwaiter().GetResult();
    public static void Cancel() => RunAsync("cancel").GetAwaiter().GetResult();
    public static void CancelledSend() => RunAsync("cancelled-send").GetAwaiter().GetResult();

    private static async Task RunAsync(string scenario)
    {
        var provider = new PendingProvider();
        var cold = new RecordingStreamingAsrProvider();
        await using var warm = new WarmAsrSession(provider, AudioCaptureOptions.WindowsMvp,
            TimeSpan.FromSeconds(30));
        using var cancellation = new CancellationTokenSource();
        await warm.PrepareAsync(CancellationToken.None);
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        if (scenario == "cancelled-send") provider.Release.TrySetCanceled();
        var acquired = AsrProviderAcquisition.AcquireAsync(warm, () =>
        {
            Check(provider.Disposed, "Cold fallback started before releasing warm provider.");
            return cold;
        }, cancellation.Token);
        try
        {
            if (scenario == "drain")
            {
                Check(!provider.SendToken.IsCancellationRequested,
                    "Claim cancelled an in-flight WebSocket send.");
                Check(!acquired.IsCompleted, "Claim did not wait for the in-flight send.");
                provider.Release.TrySetResult();
                var result = await acquired.WaitAsync(TimeSpan.FromSeconds(3));
                Check(ReferenceEquals(provider, result), "Healthy drained connection was not reused.");
                await result.SendAudioAsync(new byte[3200], CancellationToken.None);
                Check(provider.SendCount == 2, "Unexpected warm sends after handoff.");
                Check(!provider.Disposed, "Claim disposed a healthy provider.");
            }
            else if (scenario == "cancel")
            {
                cancellation.Cancel();
                try
                {
                    await acquired.WaitAsync(TimeSpan.FromSeconds(3));
                    throw new Exception("Cancelled handoff returned a provider.");
                }
                catch (OperationCanceledException) { }
                Check(provider.Disposed, "Cancelled handoff leaked the warm provider.");
                Check(cold.ConnectCount == 0, "Cancellation started a cold connection.");
            }
            else
            {
                var result = await acquired.WaitAsync(TimeSpan.FromSeconds(4));
                Check(ReferenceEquals(cold, result), "Failed warm send was reused instead of cold fallback.");
                Check(provider.Disposed, "Failed handoff leaked the warm provider.");
                Check(cold.ConnectCount == 1, "Expected exactly one cold connection.");
            }
        }
        finally
        {
            provider.Release.TrySetResult();
            try { await acquired.WaitAsync(TimeSpan.FromSeconds(4)); } catch { }
            await provider.DisposeAsync();
            await cold.DisposeAsync();
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private sealed class PendingProvider : IStreamingAsrProvider
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken SendToken { get; private set; }
        public int SendCount { get; private set; }
        public bool Disposed { get; private set; }
        public Task ConnectAsync(CancellationToken token) => Task.CompletedTask;
        public async ValueTask SendAudioAsync(ReadOnlyMemory<byte> audio, CancellationToken token)
        {
            SendCount++;
            if (SendCount != 1) return;
            SendToken = token;
            Started.TrySetResult();
            await Release.Task.WaitAsync(token);
        }
        public async IAsyncEnumerable<AsrEvent> ReadEventsAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
        {
            await Task.CompletedTask;
            yield break;
        }
        public Task CompleteAsync(CancellationToken token) => Task.CompletedTask;
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
