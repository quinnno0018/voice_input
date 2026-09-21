internal static class SendProbeTests
{
    public static void FinishObservation() => RunAsync().GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        var receiver = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var overdue = false;
        var checkpoint = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observation = SendProbe.WaitForFinishAsync(receiver.Task, () => { overdue = true; checkpoint.SetResult(); },
            TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(2), CancellationToken.None);
        await checkpoint.Task.WaitAsync(TimeSpan.FromSeconds(1));
        if (receiver.Task.IsCompleted || observation.IsCompleted || !overdue)
            throw new Exception("The first checkpoint must observe, not cancel the receiver.");
        receiver.SetResult("completed");
        if (await observation != "completed") throw new Exception("Late completion was lost.");

        receiver = new TaskCompletionSource<string>();
        try
        {
            await SendProbe.WaitForFinishAsync(receiver.Task, () => { },
                TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(40), CancellationToken.None);
            throw new Exception("Missing terminal event must time out.");
        }
        catch (TimeoutException) { }
        if (receiver.Task.IsCompleted) throw new Exception("Receiver cleanup belongs to its owner.");
        receiver.SetResult("completed");

        overdue = false;
        var fault = new TimeoutException("private-receive-error");
        try
        {
            await SendProbe.WaitForFinishAsync(Task.FromException<string>(fault), () => overdue = true,
                TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(40), CancellationToken.None);
            throw new Exception("Original receiver fault was swallowed.");
        }
        catch (TimeoutException error)
        { if (!ReferenceEquals(error, fault) || overdue) throw new Exception("Receiver fault was reclassified."); }
    }

    public static void NumericTelemetry()
    {
        if (ProbeNetworkEvents.Numeric("private-header") is not null
            || ProbeNetworkEvents.Numeric(new object()) is not null
            || ProbeNetworkEvents.Numeric(772) != 772)
            throw new Exception("Only numeric framework fields may enter network diagnostics.");
    }
}
