using VoiceInput.Desktop.Audio;

namespace VoiceInput.Desktop.Asr;

public sealed class WarmAsrSession : IAsyncDisposable
{
    private readonly IStreamingAsrProvider _provider;
    private readonly AudioCaptureOptions _audio;
    private readonly TimeSpan _lifetime;
    private readonly Action<string>? _diagnostics;
    private readonly CancellationTokenSource _warmCancellation = new();
    private readonly object _sync = new();
    private Task? _prepareTask;
    private Task? _silenceTask;
    private bool _claimed;
    private bool _disposed;
    private bool _expired;

    internal bool IsExpired { get { lock (_sync) return _expired; } }

    public WarmAsrSession(
        IStreamingAsrProvider provider,
        AudioCaptureOptions audio,
        TimeSpan lifetime,
        Action<string>? diagnostics = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lifetime, TimeSpan.Zero);
        _provider = provider;
        _audio = audio;
        _lifetime = lifetime;
        _diagnostics = diagnostics;
    }

    public Task PrepareAsync(CancellationToken cancellationToken)
    {
        Task preparation;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            preparation = _prepareTask ??= PrepareCoreAsync();
        }

        return preparation.WaitAsync(cancellationToken);
    }

    public async Task<IStreamingAsrProvider> ClaimAsync(
        CancellationToken cancellationToken)
    {
        await PrepareAsync(cancellationToken);

        Task? silenceTask;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_claimed)
            {
                throw new InvalidOperationException(
                    "The warm ASR session has already been claimed.");
            }

            _claimed = true;
            _warmCancellation.Cancel();
            silenceTask = _silenceTask;
        }

        try
        {
            await IgnoreCancellation(silenceTask);
        }
        catch
        {
            // Failed handoff retains cleanup ownership until the cold fallback can start.
            lock (_sync) { _claimed = false; }
            await DisposeAsync();
            throw;
        }
        _warmCancellation.Dispose();
        Report(_diagnostics, "warm_claimed");
        return _provider;
    }

    public async ValueTask DisposeAsync()
    {
        Task? preparation;
        Task? silenceTask;
        lock (_sync)
        {
            if (_claimed || _disposed)
            {
                return;
            }

            _disposed = true;
            _warmCancellation.Cancel();
            preparation = _prepareTask;
            silenceTask = _silenceTask;
        }

        await IgnoreFailure(preparation);
        await IgnoreFailure(silenceTask);
        await CompleteAndDisposeProviderAsync();
        _warmCancellation.Dispose();
    }

    private async Task PrepareCoreAsync()
    {
        await _provider.ConnectAsync(_warmCancellation.Token);
        _warmCancellation.Token.ThrowIfCancellationRequested();

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _silenceTask = PumpSilenceAsync(_warmCancellation.Token);
        }

        _ = ExpireAsync();
        Report(_diagnostics, $"warm_ready retention_seconds={_lifetime.TotalSeconds:F0}");
    }

    private async Task PumpSilenceAsync(CancellationToken cancellationToken)
    {
        var silence = new byte[_audio.BytesPerChunk];
        while (true)
        {
            await _provider.SendAudioAsync(silence, cancellationToken);
            await Task.Delay(
                _audio.ChunkMilliseconds,
                cancellationToken);
        }
    }

    private async Task ExpireAsync()
    {
        try
        {
            await Task.Delay(_lifetime, _warmCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var shouldDispose = false;
        lock (_sync)
        {
            if (!_claimed && !_disposed)
            {
                _expired = true;
                _disposed = true;
                _warmCancellation.Cancel();
                shouldDispose = true;
            }
        }

        if (!shouldDispose)
        {
            return;
        }

        Report(_diagnostics, "warm_expired");
        await IgnoreFailure(_silenceTask);
        await CompleteAndDisposeProviderAsync();
        _warmCancellation.Dispose();
    }

    private async Task CompleteAndDisposeProviderAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        try
        {
            await _provider.CompleteAsync(timeout.Token);
        }
        catch (Exception)
        {
        }

        try
        {
            await _provider.DisposeAsync();
        }
        catch (Exception)
        {
        }
    }

    internal static void Report(Action<string>? diagnostics, string metadata)
    {
        try { diagnostics?.Invoke(metadata); }
        catch (Exception) { /* Diagnostic sinks must not affect connection ownership. */ }
    }

    private static async Task IgnoreCancellation(Task? task)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task IgnoreFailure(Task? task)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            await task;
        }
        catch (Exception)
        {
        }
    }
}

public static class AsrProviderAcquisition
{
    public static async Task<IStreamingAsrProvider> AcquireAsync(
        WarmAsrSession? warmSession,
        Func<IStreamingAsrProvider> providerFactory,
        CancellationToken cancellationToken,
        Action<string>? diagnostics = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var coldReason = "not_prepared";
        if (warmSession is not null)
        {
            try
            {
                var claimed = await warmSession.ClaimAsync(cancellationToken);
                WarmAsrSession.Report(diagnostics, "asr_connection mode=warm");
                return claimed;
            }
            catch
            {
                coldReason = warmSession.IsExpired ? "expired" : "unavailable";
                await warmSession.DisposeAsync();
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        WarmAsrSession.Report(diagnostics, $"asr_connection mode=cold reason={coldReason}");
        var provider = providerFactory();
        try
        {
            await provider.ConnectAsync(cancellationToken);
            return provider;
        }
        catch
        {
            await provider.DisposeAsync();
            throw;
        }
    }
}
