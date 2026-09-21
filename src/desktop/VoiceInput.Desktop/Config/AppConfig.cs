using VoiceInput.Desktop.Audio;
using VoiceInput.Desktop.Hotkey;

namespace VoiceInput.Desktop.Config;

public sealed record AppConfig(
    string Provider,
    string Model,
    Uri Endpoint,
    string? ApiKey,
    HotkeyGesture Hotkey,
    int AudioDeviceIndex,
    int MaxSentenceSilenceMilliseconds,
    int TsfWarmSessionSeconds,
    AudioCaptureOptions Audio)
{
    public const string ParaformerProvider = "dashscope_paraformer_realtime";

    public static AppConfig Load(Func<string, string?>? read = null)
    {
        read ??= Environment.GetEnvironmentVariable;
        var provider = Value(read, "VOICE_INPUT_ASR_PROVIDER")
            ?? Value(read, "ASR_PROVIDER")
            ?? ParaformerProvider;
        var model = Value(read, "DASHSCOPE_PARAFORMER_MODEL")
            ?? Value(read, "ASR_MODEL")
            ?? "paraformer-realtime-v2";
        var endpointText = Value(read, "DASHSCOPE_PARAFORMER_WS_URL")
            ?? Value(read, "ASR_WS_URL")
            ?? "wss://dashscope.aliyuncs.com/api-ws/v1/inference";
        var apiKey = Value(read, "DASHSCOPE_API_KEY") ?? Value(read, "ASR_API_KEY");
        var hotkey = HotkeyGesture.Parse(
            Value(read, "VOICE_INPUT_HOTKEY") ?? "Ctrl+Alt+Space");
        var audioDeviceIndex = ParseAudioDeviceIndex(
            Value(read, "VOICE_INPUT_AUDIO_DEVICE_INDEX"));
        var maxSentenceSilenceMilliseconds = ParseMaxSentenceSilence(
            Value(read, "VOICE_INPUT_MAX_SENTENCE_SILENCE_MS"));
        var tsfWarmSessionSeconds = ParseTsfWarmSeconds(
            Value(read, "VOICE_INPUT_TSF_WARM_SECONDS"));

        return new AppConfig(
            provider,
            model,
            new Uri(endpointText, UriKind.Absolute),
            apiKey,
            hotkey,
            audioDeviceIndex,
            maxSentenceSilenceMilliseconds,
            tsfWarmSessionSeconds,
            AudioCaptureOptions.WindowsMvp);
    }

    private static string? Value(Func<string, string?> read, string name)
    {
        var value = read(name);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static int ParseAudioDeviceIndex(string? value)
    {
        if (value is null)
        {
            return -1;
        }

        if (!int.TryParse(value, out var deviceIndex) || deviceIndex < -1)
        {
            throw new FormatException(
                "VOICE_INPUT_AUDIO_DEVICE_INDEX must be -1 or a non-negative integer.");
        }

        return deviceIndex;
    }

    private static int ParseTsfWarmSeconds(string? value)
    {
        if (value is null) return 180;
        if (!int.TryParse(value, out var seconds) || seconds is < 30 or > 300)
            throw new FormatException("VOICE_INPUT_TSF_WARM_SECONDS must be an integer from 30 to 300.");
        return seconds;
    }

    private static int ParseMaxSentenceSilence(string? value)
    {
        if (value is null)
        {
            return 1200;
        }

        if (!int.TryParse(value, out var milliseconds)
            || milliseconds is not (800 or 1200 or 1600))
        {
            throw new FormatException(
                "VOICE_INPUT_MAX_SENTENCE_SILENCE_MS must be 800, 1200, or 1600.");
        }

        return milliseconds;
    }
}
