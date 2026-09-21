using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading.Channels;
using VoiceInput.Desktop.Asr;
using VoiceInput.Desktop.Tsf;

internal static class TsfBridgeTests
{
    private static void Check(bool value, string name)
    { if (!value) throw new Exception(name); }

    private static void Reject(Action action)
    {
        try { action(); } catch (Exception e) when (e is InvalidDataException or ArgumentException) { return; }
        throw new Exception("Invalid packet accepted.");
    }

    public static void Codec()
    {
        var p = new TsfPacket(TsfKind.Partial, Guid.NewGuid(), 1, 1, 44, "\u660e\u5929 WebSocket \ud83d\ude00");
        var bytes = TsfProtocol.Encode(p);
        Check(bytes.Length == 2104, "Native frame size");
        Check(BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(48)) == 44, "HWND offset");
        Check(bytes[56] == 0x0e && bytes[57] == 0x66, "UTF-16 little endian");
        Check(TsfProtocol.Decode(bytes) == p, "Packet round trip");
        Reject(() => TsfProtocol.Decode(bytes.AsSpan(0, 50)));
        bytes[4] = 2;
        Reject(() => TsfProtocol.Decode(bytes));
        Reject(() => TsfProtocol.Encode(p with { Text = new string('x', 1025) }));
        Reject(() => TsfProtocol.Encode(p with { Text = "\ud800" }));
        Reject(() => TsfProtocol.Encode(p with { Text = "x\0y" }));
        Reject(() => TsfProtocol.Encode(p with { Segment = 0 }));
        Reject(() => TsfProtocol.Encode(p with { Session = Guid.Empty }));
        Reject(() => TsfProtocol.Encode(p with { Kind = (TsfKind)99 }));
        var maximum = p with { Text = new string('x', 1024) };
        Check(TsfProtocol.Decode(TsfProtocol.Encode(maximum)) == maximum, "Bounded maximum accepted");
        using var fragmented = new FragmentedStream(TsfProtocol.Encode(p));
        Check(TsfProtocol.ReadAsync(fragmented, CancellationToken.None).GetAwaiter().GetResult() == p,
            "Fragmented pipe reads accumulate one exact frame");
        var ack = p with { Kind = TsfKind.Ack, Text = "" };
        TsfProtocol.ValidateAck(p, ack);
        Reject(() => TsfProtocol.ValidateAck(p, ack with { Session = Guid.NewGuid() }));
        Reject(() => TsfProtocol.ValidateAck(p, ack with { Sequence = 2 }));
        Reject(() => TsfProtocol.ValidateAck(p, ack with { Window = 45 }));
    }

    public static void Segments()
    {
        var now = DateTimeOffset.UtcNow;
        var state = new TranscriptSegments();
        Check(state.Accept(new PartialTranscript("one", now, "a"))?.Segment == 1, "First segment");
        Check(state.Accept(new PartialTranscript("revised", now, "a"))?.Segment == 1, "Revision shares segment");
        Reject(() => state.Accept(new PartialTranscript("foreign", now, "b")));
        Check(state.Accept(new StableTranscript("one.", now, "a"))?.Kind == TsfKind.Stable, "Stable emitted");
        Check(state.Accept(new StableTranscript("one.", now, "a")) is null, "Duplicate final suppressed");
        Check(state.Accept(new PartialTranscript("late", now, "a")) is null, "Closed segment stays closed");
        Check(state.Accept(new StableTranscript("two.", now, "b"))?.Segment == 2, "Stable without partial");
        Reject(() => state.Accept(new PartialTranscript("missing", now)));
        Check(new TranscriptSegments().Accept(new PartialTranscript("restart", now, "a"))?.Segment == 1,
            "Restart has independent identity state");
    }

    public static void PipeCancellation() => PipeCancellationAsync(false).GetAwaiter().GetResult();
    public static void PipeInvalidAck() => PipeCancellationAsync(true).GetAwaiter().GetResult();

    public static void FailureReasons()
    {
        static void HasReason(Action action, string reason)
        {
            try { action(); }
            catch (InvalidDataException e)
            {
                Check(e.Data["tsf_reason"]?.ToString() == reason, "Missing diagnostic reason: " + reason);
                Check(!e.Message.Contains("private-text"), "Exception leaks transcript text");
                return;
            }
            throw new Exception("Invalid data was not rejected.");
        }

        var now = DateTimeOffset.UtcNow;
        var state = new TranscriptSegments();
        HasReason(() => state.Accept(new PartialTranscript("private-text", now)), "SegmentIdentityMissing");
        state.Accept(new PartialTranscript("private-text", now, "a"));
        HasReason(() => state.Accept(new StableTranscript("private-text", now, "b")), "SegmentChangedBeforeFinal");
        Check(state.Accept(new StableTranscript("final", now, "a"))?.Segment == 1,
            "Rejected identity must not mutate the segment");
        var packet = new TsfPacket(TsfKind.Partial, Guid.NewGuid(), 1, 1, 44, "private-text");
        HasReason(() => TsfProtocol.ValidateAck(packet, packet with { Kind = TsfKind.Ack, Text = "", Sequence = 2 }),
            "AckMismatch");
        HasReason(() => TsfProtocol.Decode(new byte[TsfProtocol.FrameSize]), "FrameHeader");
        HasReason(() => TsfProtocol.Encode(packet with { Text = new string('x', 1025) }), "PacketInvalid");
        HasReason(() => TsfProtocol.Encode(packet with { Sequence = 0 }), "PacketState");
        var bytes = TsfProtocol.Encode(packet);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), 1025);
        HasReason(() => TsfProtocol.Decode(bytes), "TextTooLong");
        var full = new TranscriptSegments();
        for (var i = 0; i < 4096; i++) full.Accept(new StableTranscript("final", now, i.ToString()));
        HasReason(() => full.Accept(new StableTranscript("private-text", now, "overflow")), "SegmentLimit");

        var unknown = new InvalidDataException("private-text", new Exception("private-inner"));
        unknown.Data["tsf_reason"] = "private-text";
        Check(TsfDiagnostics.DescribeFailure(unknown).Contains("reason=Unclassified"), "Untrusted reason excluded");
        Check(!TsfDiagnostics.DescribeFailure(unknown).Contains("private"), "Diagnostic exposes exception content");
        unknown.Data["tsf_reason"] = (TsfFailureReason)999;
        Check(TsfDiagnostics.DescribeFailure(unknown).Contains("reason=Unclassified"), "Unknown reason excluded");
    }

    public static void ParserDiagnostics()
    {
        var now = DateTimeOffset.UtcNow;
        var lines = new List<string>();
        static string Payload(object sentence) => JsonSerializer.Serialize(new
        {
            header = new { @event = "result-generated", task_id = "private-task" },
            payload = new { output = new { sentence } },
        });
        var partial = Payload(new { begin_time = 170, end_time = (int?)null, text = "private-transcript", sentence_end = false, heartbeat = false });
        var parsed = ParaformerEventParser.Parse(partial, now, lines.Add);
        Check(parsed == ParaformerEventParser.Parse(partial, now), "Diagnostics change partial event");
        Check(lines.Single().Contains("begin_ms=170 end_ms=null sentence_end=false heartbeat=false"), "Typed sentence metadata");

        var invalidFinal = Payload(new { begin_time = 170, end_time = 920, text = "**.", sentence_end = true });
        Check(ParaformerEventParser.Parse(invalidFinal, now, lines.Add) is ConnectionState { State: "ignored_invalid_transcript" },
            "Punctuation-only final must keep existing filtering semantics");
        Check(lines[^1].Contains("sentence_end=true") && lines[^1].Contains("has_content=False"),
            "Filtered final boundary still visible to diagnostics");
        var heartbeat = Payload(new { text = "private-transcript", heartbeat = true });
        Check(ParaformerEventParser.Parse(heartbeat, now, lines.Add) is ConnectionState { State: "heartbeat" }, "Heartbeat unchanged");
        Check(lines[^1].Contains("begin_ms=missing"), "Missing timestamp explicit");
        var malformed = Payload(new { begin_time = "private-begin", end_time = new { secret = "private-end" },
            sentence_end = "private-flag", text = "private-transcript" });
        Check(ParaformerEventParser.Parse(malformed, now, lines.Add) is PartialTranscript { SegmentId: null }, "Invalid identity unchanged");
        Check(lines[^1].Contains("begin_ms=invalid end_ms=invalid sentence_end=invalid"), "Malformed fields are not copied");
        var final = Payload(new { begin_time = 170, end_time = 920, text = "private-transcript.", sentence_end = true });
        Check(ParaformerEventParser.Parse(final, now, lines.Add) == ParaformerEventParser.Parse(final, now), "Stable unchanged");
        Check(lines.All(line => !line.Contains("private")), "No transcript, task ID or arbitrary payload strings in metadata");
        Check(ParaformerEventParser.Parse(partial, now, _ => throw new IOException("private-log-error")) == parsed,
            "Broken diagnostic sink must not interrupt recognition");
    }

    public static void SegmentDiagnostics()
    {
        var now = DateTimeOffset.UtcNow;
        var lines = new List<string>();
        var state = new TranscriptSegments();
        state.Accept(new PartialTranscript("private-text", now, "private-id"), lines.Add);
        Reject(() => state.Accept(new StableTranscript("private-text", now, "private-other-id"), lines.Add));
        Check(lines.Count == 2 && lines[^1].Contains("active=True matches_active=False"), "Rejected identity traced before guard");
        state.Accept(new StableTranscript("private-text", now, "private-id"), lines.Add);
        Check(state.Accept(new StableTranscript("private-text", now, "private-id"), lines.Add) is null, "Duplicate still ignored");
        Check(lines[^1].Contains("closed=True"), "Duplicate outcome traceable");
        Check(lines.All(line => !line.Contains("private")), "No text or opaque identity in core diagnostics");
        var next = state.Accept(new PartialTranscript("next", now, "next-id"), _ => throw new IOException("log failed"));
        Check(next?.Segment == 2, "Sink failure must not change state progression");
    }

    private static async Task PipeCancellationAsync(bool invalidAck)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var name = "VoiceInput.TsfTest." + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        var accepting = server.WaitForConnectionAsync(deadline.Token);
        await client.ConnectAsync(deadline.Token);
        await accepting;
        var queue = Channel.CreateBounded<TsfUpdate>(2);
        queue.Writer.TryWrite(new(TsfKind.Partial, 1, "first", DateTimeOffset.UtcNow));
        queue.Writer.TryWrite(new(TsfKind.Stable, 1, "final", DateTimeOffset.UtcNow));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var applied = 0;
        var hello = new TsfPacket(TsfKind.Hello, Guid.NewGuid(), 0, 0, 44);
        var pump = TsfAsrHost.PumpUpdatesAsync(server, hello, queue.Reader, cancellation.Token, (_, _) => applied++);
        var first = await TsfProtocol.ReadAsync(client, deadline.Token);
        Check(first.Kind == TsfKind.Partial && applied == 0, "No apply before native acknowledgment");
        if (invalidAck)
        {
            await client.WriteAsync(TsfProtocol.Encode(first with { Kind = TsfKind.Ack, Text = "", Sequence = first.Sequence + 1 }), deadline.Token);
            try { await pump; throw new Exception("Mismatched acknowledgment accepted."); }
            catch (InvalidDataException e)
            { Check(TsfDiagnostics.DescribeFailure(e).Contains("reason=AckMismatch"), "Pipe error reason"); }
        }
        else
        {
            cancellation.Cancel();
            try { await pump; throw new Exception("Cancelled pipe continued."); }
            catch (OperationCanceledException) { }
        }
        Check(applied == 0 && queue.Reader.Count == 1, "Queued final not consumed after cancellation");
    }

    public static void NativePipe(string executable)
    {
        NativePipeAsync(executable, false).GetAwaiter().GetResult();
        NativePipeAsync(executable, true).GetAwaiter().GetResult();
    }

    private static async Task NativePipeAsync(string executable, bool drop)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var pipe = new NamedPipeServerStream(TsfAsrHost.PipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance);
        var accept = pipe.WaitForConnectionAsync(timeout.Token);
        using var process = Process.Start(new ProcessStartInfo(executable, drop ? "--pipe-drop" : "--pipe")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await accept;
            var hello = await TsfProtocol.ReadAsync(pipe, timeout.Token);
            Check(hello.Kind == TsfKind.Hello && hello.Window == 44, "Native hello decoded");
            if (drop)
            {
                await pipe.WriteAsync(TsfProtocol.Encode(new(TsfKind.Partial, hello.Session, 1, 1, 44, "pending")), timeout.Token);
                await Task.Delay(100, timeout.Token);
                pipe.Dispose();
                await process.WaitForExitAsync(timeout.Token);
                Check(process.ExitCode == 0, "Native pending-ack disconnect failed: " + await stdout + await stderr);
                return;
            }
            var channel = Channel.CreateBounded<TsfUpdate>(4);
            foreach (var update in new[] {
                new TsfUpdate(TsfKind.Partial, 1, "first", DateTimeOffset.UtcNow),
                new TsfUpdate(TsfKind.Stable, 1, "final.", DateTimeOffset.UtcNow),
                new TsfUpdate(TsfKind.Partial, 2, "next", DateTimeOffset.UtcNow),
                new TsfUpdate(TsfKind.Stable, 2, "next.", DateTimeOffset.UtcNow) })
                channel.Writer.TryWrite(update);
            channel.Writer.Complete();
            var applied = 0;
            try { await TsfAsrHost.PumpUpdatesAsync(pipe, hello, channel.Reader, timeout.Token, (_, _) => applied++); }
            catch (EndOfStreamException) { }
            Check(applied == 4, "All native acknowledgments matched");
            pipe.Dispose();
            await process.WaitForExitAsync(timeout.Token);
            Check(process.ExitCode == 0, "Native integration failed: " + await stdout + await stderr);
        }
        finally
        {
            pipe.Dispose();
            // Closing the test pipe is the worker's shutdown signal, not a process-wide kill.
            if (!process.HasExited) await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(6));
        }
    }

    private sealed class FragmentedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) =>
            base.ReadAsync(buffer[..Math.Min(7, buffer.Length)], token);
    }
}
