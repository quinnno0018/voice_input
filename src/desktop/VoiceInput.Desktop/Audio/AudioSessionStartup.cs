using VoiceInput.Desktop.Asr;

namespace VoiceInput.Desktop.Audio;

public interface IAudioCaptureControl
{
    void Start();
}

public static class AudioSessionStartup
{
    public static async Task<IStreamingAsrProvider> AcquireProviderThenStartCaptureAsync(
        IAudioCaptureControl audio,
        WarmAsrSession? warmSession,
        Func<IStreamingAsrProvider> providerFactory,
        CancellationToken cancellationToken,
        Action validateTarget,
        Action<string>? diagnostics = null)
    {
        var provider = await AsrProviderAcquisition.AcquireAsync(
            warmSession, providerFactory, cancellationToken, diagnostics);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            validateTarget();
            audio.Start();
            return provider;
        }
        catch
        {
            await provider.DisposeAsync();
            throw;
        }
    }

    public static async Task StartCaptureThenConnectAsync(
        IAudioCaptureControl audio,
        IStreamingAsrProvider provider,
        CancellationToken cancellationToken)
    {
        audio.Start();
        await provider.ConnectAsync(cancellationToken);
    }

    public static async Task<IStreamingAsrProvider> StartCaptureThenAcquireProviderAsync(
        IAudioCaptureControl audio,
        WarmAsrSession? warmSession,
        Func<IStreamingAsrProvider> providerFactory,
        CancellationToken cancellationToken)
    {
        audio.Start();
        return await AsrProviderAcquisition.AcquireAsync(
            warmSession,
            providerFactory,
            cancellationToken);
    }
}
