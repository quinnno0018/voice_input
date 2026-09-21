using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using VoiceInput.Desktop.Asr;
using VoiceInput.Desktop.Tsf;

internal static class SendDiagnosticsTests
{
    private static void Check(bool value, string message)
    { if (!value) throw new Exception(message); }

    private static ValueTask Measure(Func<ValueTask> send, Action<double, double, string> report) =>
        ParaformerRealtimeProvider.MeasureSendAsync(send, report);

    public static void DelayedContinuation()
    {
        using var context = new PausedContext();
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            var completion = new TaskCompletionSource();
            (double Completion, double Resume, string Outcome)? timing = null;
            var operation = Measure(() => new ValueTask(completion.Task),
                (a, b, c) => timing = (a, b, c)).AsTask();
            Task.Run(() => completion.SetResult()).GetAwaiter().GetResult();
            Check(context.Posted.Wait(TimeSpan.FromSeconds(5)), "Send completion must reach the captured context");
            Thread.Sleep(160);
            Check(!operation.IsCompleted && timing is null, "Measurement must preserve caller context behavior");
            context.Drain();
            operation.GetAwaiter().GetResult();
            Check(timing is { Resume: >= 120, Outcome: "completed" },
                "UI delay must be reported separately from send task completion");
            Console.WriteLine($"[DIAGNOSTIC] controlled_ui_pause completion_ms={timing!.Value.Completion:F1} continuation_ms={timing.Value.Resume:F1}");
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    public static void DelayedSend()
    {
        (double Completion, double Resume, string Outcome)? timing = null;
        Measure(() => new ValueTask(Task.Delay(160)), (a, b, c) => timing = (a, b, c))
            .AsTask().GetAwaiter().GetResult();
        Check(timing is { Completion: >= 120, Outcome: "completed" },
            "An incomplete send must contribute to send completion time");
        Console.WriteLine($"[DIAGNOSTIC] controlled_pending_send completion_ms={timing!.Value.Completion:F1} continuation_ms={timing.Value.Resume:F1}");
    }

    public static void Failures()
    {
        var failure = new WebSocketException("private-provider-message");
        string? outcome = null;
        try
        {
            Measure(() => new ValueTask(Task.FromException(failure)), (_, _, value) => outcome = value)
                .AsTask().GetAwaiter().GetResult();
            throw new Exception("A failed send was swallowed");
        }
        catch (WebSocketException e) { Check(ReferenceEquals(failure, e), "Original send failure must survive"); }
        Check(outcome == "failed", "Fault outcome must not copy the exception message");

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = Measure(() => new ValueTask(Task.FromCanceled(cancellation.Token)),
            (_, _, value) => outcome = value).AsTask();
        try { cancelled.GetAwaiter().GetResult(); throw new Exception("Cancellation was swallowed"); }
        catch (OperationCanceledException e) { Check(e.CancellationToken == cancellation.Token, "Cancellation token changed"); }
        Check(cancelled.IsCanceled && outcome == "cancelled", "Cancelled send must remain cancelled");

        var synchronous = new IOException("private-sync-message");
        try
        {
            Measure(() => throw synchronous, (_, _, value) => outcome = value)
                .AsTask().GetAwaiter().GetResult();
            throw new Exception("Synchronous failure was swallowed");
        }
        catch (IOException e) { Check(ReferenceEquals(synchronous, e), "Synchronous failure changed"); }
        Check(outcome == "failed", "Synchronous failure outcome");
    }

    public static void BrokenSink()
    {
        var sends = 0;
        Measure(() => { sends++; return ValueTask.CompletedTask; },
            (_, _, _) => throw new IOException("private-diagnostic-error"))
            .AsTask().GetAwaiter().GetResult();
        Check(sends == 1, "Diagnostic failure must not retry audio");
    }

    public static void AudioOverflow()
    {
        TsfAsrHost.EnsureAudioNotDropped(0);
        IOException error;
        try { TsfAsrHost.EnsureAudioNotDropped(2); throw new Exception("Dropped audio did not stop the session"); }
        catch (IOException failure) { error = failure; }
        Check(TsfDiagnostics.DescribeFailure(error).Contains("reason=AudioQueueOverflow"), "Overflow must not be unclassified");
        var message = TsfDiagnostics.UserMessage(error);
        Check(message.Contains("audio backlog", StringComparison.OrdinalIgnoreCase), "HUD must identify audio backlog");
        var unrelated = new IOException("private-provider-message");
        unrelated.Data["tsf_reason"] = "AudioQueueOverflow";
        var generic = TsfDiagnostics.UserMessage(unrelated);
        Check(!generic.Contains("private") && !generic.Contains("audio backlog", StringComparison.OrdinalIgnoreCase),
            "Only typed local failure reasons may choose a specific user message");
        var textOverflow = TsfDiagnostics.QueueOverflow(TsfFailureReason.TranscriptQueueOverflow);
        Check(TsfDiagnostics.DescribeFailure(textOverflow).Contains("reason=TranscriptQueueOverflow"), "Text backlog must be distinguishable");
        Check(TsfDiagnostics.UserMessage(textOverflow).Contains("text updates"), "Text backlog must not blame audio");
    }

    private sealed class PausedContext : SynchronizationContext, IDisposable
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _pending = new();
        public ManualResetEventSlim Posted { get; } = new();
        public override void Post(SendOrPostCallback callback, object? state)
        { _pending.Enqueue((callback, state)); Posted.Set(); }
        public void Drain()
        { while (_pending.TryDequeue(out var item)) item.Callback(item.State); }
        public void Dispose() => Posted.Dispose();
    }
}
