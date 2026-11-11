using System.Net;
using System.IO;
using System.Globalization;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using VoiceInput.Desktop.Asr;
using VoiceInput.Desktop.Config;

internal static class ReceiveDiagnosticsTests
{
    public static void Loopback() => RunAsync("record").GetAwaiter().GetResult();
    public static void BrokenSink() => RunAsync("broken").GetAwaiter().GetResult();
    public static void Disabled() => RunAsync("disabled").GetAwaiter().GetResult();

    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); }

    private static async Task RunAsync(string mode)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = timeout.Token;
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var server = ServeAsync();
        var logs = new List<string>();
        var receiveAttempts = 0;
        var config = AppConfig.Load(_ => null) with
        { Endpoint = new Uri($"ws://127.0.0.1:{port}/"), ApiKey = "loopback-test-only" };
        Action<string>? sink = mode == "disabled" ? null : message =>
        {
            if (message.StartsWith("websocket_receive ")) receiveAttempts++;
            if (mode == "broken" && message.StartsWith("websocket_receive "))
                throw new IOException("diagnostic-sink-failure");
            logs.Add(message);
        };
        try
        {
            await using (var provider = new ParaformerRealtimeProvider(config, sink))
            {
                await provider.ConnectAsync(token);
                var events = new List<AsrEvent>();
                await foreach (var value in provider.ReadEventsAsync(token)) events.Add(value);
                Check(events.Count == 3, "Receive instrumentation changed the event count");
                Check(events[0] is PartialTranscript { Text: "private-test-transcript" }, "Fragmented partial changed");
                Check(events[1] is StableTranscript { Text: "private-test-transcript" }, "Stable changed");
                Check(events[2] is ConnectionState { State: "task_finished" }, "Finish event changed");
            }
            await server;
            if (mode == "broken") Check(receiveAttempts >= 4, "Broken receive sink was never exercised");
            if (mode != "record") return;
            var receive = logs.Where(x => x.StartsWith("websocket_receive ")).ToArray();
            Check(receive.Length >= 4, "Missing receive completion diagnostics");
            Check(receive.Any(x => x.Contains("message_seq=2 ") && x.Contains("end_of_message=False")),
                "Fragment identity is missing");
            Check(receive.Any(x => x.Contains("message_seq=2 ") && x.Contains("end_of_message=True")),
                "Fragmented message completion is missing");
            Check(receive.All(x => x.Contains("continuation_delay_ms=") && x.Contains("completion_observed_ms=")
                && x.Contains("completion_tick=") && x.Contains("resume_tick=") && x.Contains("clock_hz=")
                && x.Contains("outcome=completed")), "Receive timing fields are incomplete");
            foreach (var entry in receive)
            {
                var fields = entry.Split(' ').Skip(1).Select(x => x.Split('=', 2))
                    .ToDictionary(x => x[0], x => x[1]);
                long Tick(string key) => long.Parse(fields[key], CultureInfo.InvariantCulture);
                var started = Tick("start_tick");
                var completed = Tick("completion_tick");
                var resumed = Tick("resume_tick");
                var frequency = Tick("clock_hz");
                Check(frequency > 0 && started <= completed && completed <= resumed, "Invalid monotonic receive timeline");
                var wait = double.Parse(fields["completion_observed_ms"], CultureInfo.InvariantCulture);
                var resume = double.Parse(fields["continuation_delay_ms"], CultureInfo.InvariantCulture);
                Check(Math.Abs(wait - (completed - started) * 1000.0 / frequency) < 0.002,
                    "Receive wait has wrong units or endpoints");
                Check(Math.Abs(resume - (resumed - completed) * 1000.0 / frequency) < 0.002,
                    "Receive continuation delay has wrong units or endpoints");
            }
            Check(!logs.Any(x => x.Contains("private-test-transcript") || x.Contains("loopback-test-only")),
                "Diagnostics leaked content or credentials");
        }
        finally
        {
            timeout.Cancel();
            listener.Stop();
            try { await server; } catch (Exception) { /* Observe server cleanup after assertion/failure. */ }
        }

        async Task ServeAsync()
        {
            var context = await listener.GetContextAsync().WaitAsync(token);
            var accepted = await context.AcceptWebSocketAsync(null);
            using var socket = accepted.WebSocket;
            var buffer = new byte[8192];
            WebSocketReceiveResult request;
            do { request = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token); }
            while (!request.EndOfMessage);
            await Send("{\"header\":{\"event\":\"task-started\"}}", true);
            await Send("{\"header\":{\"event\":\"result-generated\"},\"payload\":{\"output\":", false);
            await Task.Delay(30, token);
            await Send("{\"sentence\":{\"begin_time\":0,\"text\":\"private-test-transcript\",\"sentence_end\":false}}}}", true);
            await Send("{\"header\":{\"event\":\"result-generated\"},\"payload\":{\"output\":{\"sentence\":{\"begin_time\":0,\"text\":\"private-test-transcript\",\"sentence_end\":true}}}}", true);
            await Send("{\"header\":{\"event\":\"task-finished\"}}", true);
            await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", token);

            Task Send(string json, bool end) => socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(json)),
                WebSocketMessageType.Text, end, token);
        }
    }
}
