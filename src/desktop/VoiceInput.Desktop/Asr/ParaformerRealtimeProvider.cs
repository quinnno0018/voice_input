using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.IO;
using System.Diagnostics;
using VoiceInput.Desktop.Config;

namespace VoiceInput.Desktop.Asr;

public sealed class ParaformerRealtimeProvider(AppConfig config, Action<string>? diagnostics = null)
    : IStreamingAsrProvider
{
    private readonly ClientWebSocket _socket = new();
    private readonly string _taskId = Guid.NewGuid().ToString("N");
    private bool _connected;
    private bool _completed;
    private long _eventSequence;
    private long _audioSequence;
    private long _receiveSequence;

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(config.ApiKey))
        {
            throw new AsrProviderException(
                "DASHSCOPE_API_KEY or ASR_API_KEY is required.");
        }

        if (!string.Equals(
                config.Provider,
                AppConfig.ParaformerProvider,
                StringComparison.Ordinal))
        {
            throw new AsrProviderException(
                $"Unsupported desktop ASR provider '{config.Provider}'.");
        }

        _socket.Options.SetRequestHeader(
            "Authorization",
            $"Bearer {config.ApiKey}");
        await _socket.ConnectAsync(config.Endpoint, cancellationToken);
        await SendTextAsync(BuildRunTask(config, _taskId), cancellationToken);

        while (_socket.State == WebSocketState.Open)
        {
            var json = await ReceiveTextAsync(cancellationToken);
            var parsed = ParseEvent(json);
            if (parsed is AsrError error)
            {
                throw new AsrProviderException(error.Message);
            }

            if (parsed is ConnectionState { State: "task_started" })
            {
                _connected = true;
                return;
            }
        }

        throw new AsrProviderException(
            "Paraformer connection closed before task-started.");
    }

    public async ValueTask SendAudioAsync(
        ReadOnlyMemory<byte> pcmAudio,
        CancellationToken cancellationToken)
    {
        if (!_connected || _completed || _socket.State != WebSocketState.Open)
        {
            throw new InvalidOperationException("Paraformer is not accepting audio.");
        }

        config.Audio.ValidatePcmChunk(pcmAudio);
        if (diagnostics is null)
        {
            await _socket.SendAsync(pcmAudio, WebSocketMessageType.Binary, true, cancellationToken);
            return;
        }

        var sequence = ++_audioSequence;
        await MeasureSendAsync(
            () => _socket.SendAsync(pcmAudio, WebSocketMessageType.Binary, true, cancellationToken),
            (completion, continuation, outcome) =>
            {
                if (sequence == 1 || completion + continuation >= 100 || outcome != "completed")
                    diagnostics(FormattableString.Invariant($"websocket_send task={_taskId} audio_seq={sequence} bytes={pcmAudio.Length} completion_observed_ms={completion:F3} continuation_delay_ms={continuation:F3} total_ms={completion + continuation:F3} outcome={outcome}"));
            });
    }

    internal static ValueTask MeasureSendAsync(Func<ValueTask> send, Action<double, double, string> report) =>
        MeasureIoAsync(send, (started, completed, resumed, outcome) => report(
            Stopwatch.GetElapsedTime(started, completed).TotalMilliseconds,
            Stopwatch.GetElapsedTime(completed, resumed).TotalMilliseconds, outcome));

    private static async ValueTask MeasureIoAsync(Func<ValueTask> operation, Action<long, long, long, string> report)
    {
        var started = Stopwatch.GetTimestamp();
        long completed = 0;
        var outcome = "failed";
        try
        {
            var pending = operation().AsTask();
            // Observe completion without the WPF context, then resume on the original context.
            await pending.ContinueWith(_ => { completed = Stopwatch.GetTimestamp(); },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            pending.GetAwaiter().GetResult();
            outcome = "completed";
        }
        catch (OperationCanceledException)
        {
            outcome = "cancelled";
            throw;
        }
        finally
        {
            var resumed = Stopwatch.GetTimestamp();
            if (completed == 0) completed = resumed;
            try
            {
                report(started, completed, resumed, outcome);
            }
            catch (Exception) { /* Diagnostic sinks must not change I/O completion or failure. */ }
        }
    }

    public async IAsyncEnumerable<AsrEvent> ReadEventsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (_socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            var parsed = ParseEvent(await ReceiveTextAsync(cancellationToken));
            yield return parsed;

            if (parsed is AsrError)
            {
                yield break;
            }

            if (parsed is ConnectionState { State: "task_finished" })
            {
                yield break;
            }
        }
    }

    public async Task CompleteAsync(CancellationToken cancellationToken)
    {
        if (!_connected || _completed || _socket.State != WebSocketState.Open)
        {
            return;
        }

        _completed = true;
        await SendTextAsync(BuildFinishTask(), cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _socket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "session closed",
                    timeout.Token);
            }
            catch (Exception exception) when (exception is WebSocketException or OperationCanceledException)
            {
                _socket.Abort();
            }
        }

        _socket.Dispose();
    }

    private AsrEvent ParseEvent(string json)
    {
        var sequence = ++_eventSequence;
        return ParaformerEventParser.Parse(json, diagnostics: diagnostics is null ? null : metadata =>
            diagnostics($"asr_wire task={_taskId} seq={sequence} phase={(_connected ? "stream" : "connect")} {metadata}"));
    }

    internal static string BuildRunTask(AppConfig config, string taskId) =>
        JsonSerializer.Serialize(new
        {
            header = new
            {
                action = "run-task",
                task_id = taskId,
                streaming = "duplex",
            },
            payload = new
            {
                task_group = "audio",
                task = "asr",
                function = "recognition",
                model = config.Model,
                parameters = new
                {
                    format = "pcm",
                    sample_rate = config.Audio.SampleRate,
                    language_hints = new[] { "zh", "en" },
                    disfluency_removal_enabled = false,
                    semantic_punctuation_enabled = false,
                    max_sentence_silence = config.MaxSentenceSilenceMilliseconds,
                    heartbeat = true,
                },
                input = new { },
            },
        });

    private string BuildFinishTask() =>
        JsonSerializer.Serialize(new
        {
            header = new
            {
                action = "finish-task",
                task_id = _taskId,
                streaming = "duplex",
            },
            payload = new { input = new { } },
        });

    private async Task SendTextAsync(
        string text,
        CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await _socket.SendAsync(
            bytes,
            WebSocketMessageType.Text,
            true,
            cancellationToken);
    }

    private async Task<string> ReceiveTextAsync(CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        var buffer = new byte[16 * 1024];

        while (true)
        {
            WebSocketReceiveResult result;
            if (diagnostics is null)
            {
                result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
            }
            else
            {
                var readSequence = ++_receiveSequence;
                WebSocketReceiveResult? received = null;
                await MeasureIoAsync(async () =>
                {
                    received = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken)
                        .ConfigureAwait(false);
                }, (started, completed, resumed, outcome) =>
                {
                    var metadata = $"websocket_receive task={_taskId} read_seq={readSequence} message_seq={_eventSequence + 1} phase={(_connected ? "stream" : "connect")} "
                        + $"bytes={received?.Count ?? 0} message_type={received?.MessageType.ToString() ?? "unknown"} end_of_message={received?.EndOfMessage.ToString() ?? "unknown"} "
                        + $"start_tick={started} completion_tick={completed} resume_tick={resumed} clock_hz={Stopwatch.Frequency} ";
                    diagnostics(metadata + FormattableString.Invariant($"completion_observed_ms={Stopwatch.GetElapsedTime(started, completed).TotalMilliseconds:F3} continuation_delay_ms={Stopwatch.GetElapsedTime(completed, resumed).TotalMilliseconds:F3} outcome={outcome}"));
                });
                result = received!;
            }
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new AsrProviderException(
                    $"Paraformer connection closed: {_socket.CloseStatusDescription}");
            }

            if (result.MessageType != WebSocketMessageType.Text)
            {
                throw new AsrProviderException(
                    $"Unexpected Paraformer message type: {result.MessageType}.");
            }

            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(stream.GetBuffer(), 0, checked((int)stream.Length));
            }
        }
    }
}
