using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Globalization;
using System.Reflection;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using VoiceInput.Desktop.Asr;
using VoiceInput.Desktop.Config;

internal static class SendProbe
{
    private const int ChunkBytes = 3200;
    private const int ChunkCount = 100;

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args is ["prepare", var directory])
            {
                Prepare(directory);
                return 0;
            }
            if (args is ["run", var planPath, var outputPath])
                return await MeasureAsync(planPath, outputPath);
            Console.Error.WriteLine("send-probe: expected prepare DIRECTORY or run PLAN OUTPUT");
        }
        catch (Exception error)
        {
            // Provider messages may contain server data. Only expose the exception type.
            Console.Error.WriteLine($"send-probe: {error.GetType().Name}");
        }
        return 1;
    }

    private static void Prepare(string directory)
    {
        var config = AppConfig.Load();
        ValidateEndpoint(config.Endpoint);
        if (config.Model != "paraformer-realtime-v2" || config.Provider != AppConfig.ParaformerProvider)
            throw new InvalidDataException("The comparison requires the current Paraformer baseline.");
        Directory.CreateDirectory(directory);
        var audio = new byte[ChunkBytes * ChunkCount];
        using (var file = new FileStream(Path.Combine(directory, "fixture.pcm"), FileMode.CreateNew))
            file.Write(audio);
        WriteNew(Path.Combine(directory, "plan.json"), new
        {
            endpoint = config.Endpoint.AbsoluteUri,
            fixture_sha256 = Hash(audio),
            chunk_bytes = ChunkBytes,
            chunk_ms = 100,
            run_task = JsonNode.Parse(ParaformerRealtimeProvider.BuildRunTask(config, "probe")),
        });
    }

    private static void ValidateEndpoint(Uri endpoint)
    {
        var official = endpoint.Scheme == "wss" && endpoint.Host == "dashscope.aliyuncs.com"
            && endpoint.IsDefaultPort && endpoint.AbsolutePath == "/api-ws/v1/inference";
        var loopback = endpoint.Scheme == "ws" && endpoint.Host == "127.0.0.1";
        if ((!official && !loopback) || endpoint.UserInfo.Length != 0
            || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
            throw new InvalidDataException("Unsupported probe endpoint.");
    }

    private static async Task<int> MeasureAsync(string planPath, string outputPath)
    {
        var plan = JsonNode.Parse(File.ReadAllText(planPath))!;
        var audio = File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(planPath))!, "fixture.pcm"));
        if (audio.Length != ChunkBytes * ChunkCount || audio.Any(value => value != 0)
            || Hash(audio) != plan["fixture_sha256"]!.GetValue<string>()
            || plan["chunk_bytes"]!.GetValue<int>() != ChunkBytes || plan["chunk_ms"]!.GetValue<int>() != 100)
            throw new InvalidDataException("Invalid fixed PCM fixture.");
        var endpoint = new Uri(plan["endpoint"]!.GetValue<string>());
        ValidateEndpoint(endpoint);
        var request = plan["run_task"]!;
        var config = AppConfig.Load() with
        {
            Provider = AppConfig.ParaformerProvider,
            Endpoint = endpoint,
            Model = request["payload"]!["model"]!.GetValue<string>(),
            MaxSentenceSilenceMilliseconds = request["payload"]!["parameters"]!["max_sentence_silence"]!.GetValue<int>(),
        };
        if (config.Model != "paraformer-realtime-v2"
            || config.MaxSentenceSilenceMilliseconds is not (800 or 1200 or 1600)
            || !JsonNode.DeepEquals(request, JsonNode.Parse(ParaformerRealtimeProvider.BuildRunTask(config, "probe"))))
            throw new InvalidDataException("Probe request differs from the desktop protocol.");
        if (string.IsNullOrWhiteSpace(config.ApiKey) || (endpoint.IsLoopback && config.ApiKey != "loopback"))
            throw new InvalidOperationException("A key is required; loopback requires the test-only key.");

        var startedUtc = DateTimeOffset.UtcNow;
        var clock = Stopwatch.StartNew();
        var timeline = new Dictionary<string, double>();
        var counts = new Dictionary<string, int>();
        var observedSends = new List<Dictionary<string, double>>();
        string? lastEvent = null;
        double? lastEventAt = null;
        var receiverStatus = "not_started";
        string? receiverError = null;
        var sendTimes = new List<double>();
        var scheduleLag = new List<double>();
        var sent = 0;
        var sendComplete = false;
        double? connectMs = null;
        var outcome = "not_started";
        var stage = "connect";
        var finishSent = false;
        double? finishSendMs = null;
        double? finishAckMs = null;
        var finishLate = false;
        var phase = new Stopwatch();
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(85));
        using var network = new ProbeNetworkEvents();
        Task<string>? receiver = null;
        // Exercise the same send diagnostic path as the desktop, without file I/O or WPF.
        await using var provider = new ParaformerRealtimeProvider(config, ObserveSend);
        try
        {
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(12));
            var connect = Stopwatch.StartNew();
            await provider.ConnectAsync(connectTimeout.Token);
            connectMs = connect.Elapsed.TotalMilliseconds;
            timeline["provider_ready"] = clock.Elapsed.TotalMilliseconds;
            NoteEvent("task_started");
            receiverStatus = "running";
            receiver = ReceiveAsync();
            phase.Start();
            stage = "audio_send";
            using var sendTimeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            sendTimeout.CancelAfter(TimeSpan.FromSeconds(45));
            for (var index = 0; index < ChunkCount; index++)
            {
                var due = (index + 1) * 100.0;
                var delay = due - phase.Elapsed.TotalMilliseconds;
                if (delay > 0) await Task.Delay(TimeSpan.FromMilliseconds(delay), sendTimeout.Token);
                if (receiver.IsCompleted)
                    throw new IOException("Provider ended before all audio was sent.");
                scheduleLag.Add(Math.Max(0, phase.Elapsed.TotalMilliseconds - due));
                var send = Stopwatch.StartNew();
                await provider.SendAudioAsync(audio.AsMemory(index * ChunkBytes, ChunkBytes), sendTimeout.Token);
                sendTimes.Add(send.Elapsed.TotalMilliseconds);
                sent++;
            }
            phase.Stop();
            sendComplete = true;
            timeline["last_audio_send_completed"] = clock.Elapsed.TotalMilliseconds;
            stage = "finish_send";
            var finish = Stopwatch.StartNew();
            using var finishTimeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            finishTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                await provider.CompleteAsync(finishTimeout.Token);
                finishSent = true;
                timeline["finish_sent"] = clock.Elapsed.TotalMilliseconds;
            }
            finally { finishSendMs = finish.Elapsed.TotalMilliseconds; }
            stage = "finish_ack";
            finish.Restart();
            try
            {
                outcome = await WaitForFinishAsync(receiver, () => finishLate = true,
                    TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(20), lifetime.Token);
            }
            finally { finishAckMs = finish.Elapsed.TotalMilliseconds; }
            if (outcome == "completed") stage = "completed";
        }
        catch (Exception error)
        {
            outcome = error.GetType().Name;
        }
        finally
        {
            phase.Stop();
            timeline["observation_end"] = clock.Elapsed.TotalMilliseconds;
            lifetime.Cancel();
            if (receiver is not null)
            {
                try { await receiver; }
                catch (Exception) { /* The run already records its bounded failure above. */ }
            }
        }
        WriteNew(outputPath, new
        {
            diagnostics_version = 2,
            client = "csharp",
            runtime = Environment.Version.ToString(),
            started_utc = startedUtc,
            fixture_sha256 = Hash(audio),
            outcome,
            phase = stage,
            timeline_ms = timeline,
            finish_sent = finishSent,
            finish_send_ms = finishSendMs,
            finish_ack_wait_ms = finishAckMs,
            finish_wait_exceeded_5s = finishLate,
            events = new { counts, last_kind = lastEvent, last_at_ms = lastEventAt },
            receiver_status = receiverStatus,
            receiver_error_type = receiverError,
            send_completion_details = observedSends,
            network_events = network.Snapshot(),
            connection = await ProbeNetworkEvents.ConnectionSettingsAsync(endpoint),
            websocket_assembly = typeof(ClientWebSocket).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            send_phase_complete = sendComplete,
            connect_ms = connectMs,
            sent_chunks = sent,
            total_audio_bytes = sent * ChunkBytes,
            send_phase_ms = phase.Elapsed.TotalMilliseconds,
            send_ms = sendTimes,
            schedule_lag_ms = scheduleLag,
        });
        return outcome == "completed" ? 0 : 1;

        void NoteEvent(string kind)
        {
            counts[kind] = counts.GetValueOrDefault(kind) + 1;
            lastEvent = kind;
            lastEventAt = clock.Elapsed.TotalMilliseconds;
        }

        void ObserveSend(string metadata)
        {
            if (!metadata.StartsWith("websocket_send ", StringComparison.Ordinal) || observedSends.Count >= 101) return;
            var numbers = new Dictionary<string, double>();
            foreach (var field in metadata.Split(' '))
            {
                var pair = field.Split('=', 2);
                if (pair.Length == 2 && pair[0] is "audio_seq" or "completion_observed_ms" or "continuation_delay_ms")
                    if (double.TryParse(pair[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                        && double.IsFinite(value) && value >= 0) numbers[pair[0]] = value;
            }
            observedSends.Add(numbers);
        }

        async Task<string> ReceiveAsync()
        {
            try
            {
                await foreach (var item in provider.ReadEventsAsync(lifetime.Token))
                {
                    var kind = item switch
                    {
                        AsrError => "asr_error",
                        PartialTranscript or StableTranscript => "transcript_result",
                        ConnectionState { State: "task_finished" } => "task_finished",
                        ConnectionState { State: "heartbeat" } => "heartbeat",
                        ConnectionState { State: "ignored_invalid_transcript" } => "invalid_transcript",
                        _ => "other",
                    };
                    NoteEvent(kind);
                    if (kind is "asr_error" or "task_finished")
                    {
                        receiverStatus = kind;
                        return kind == "task_finished" ? "completed" : "asr_error";
                    }
                }
                return receiverStatus = "connection_ended";
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            { receiverStatus = "cancelled_by_probe"; throw; }
            catch (Exception error)
            { receiverStatus = "failed"; receiverError = error.GetType().Name; throw; }
        }
    }

    internal static async Task<string> WaitForFinishAsync(Task<string> receiver, Action overdue,
        TimeSpan firstWindow, TimeSpan deadline, CancellationToken token)
    {
        var start = Stopwatch.StartNew();
        try { return await receiver.WaitAsync(firstWindow, token); }
        catch (TimeoutException)
        {
            if (receiver.IsCompleted) return await receiver;
            overdue();
            var remaining = deadline - start.Elapsed;
            if (remaining <= TimeSpan.Zero) throw;
            return await receiver.WaitAsync(remaining, token);
        }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static void WriteNew(string path, object value)
    {
        using var file = new FileStream(path, FileMode.CreateNew);
        JsonSerializer.Serialize(file, value, new JsonSerializerOptions { WriteIndented = true });
    }
}
