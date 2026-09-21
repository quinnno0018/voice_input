using System.Buffers.Binary;
using System.IO;
using System.Text;
using VoiceInput.Desktop.Asr;

namespace VoiceInput.Desktop.Tsf;

internal enum TsfKind : uint { Hello = 1, Partial = 2, Stable = 3, Ack = 4, Ping = 5 }

internal sealed record TsfPacket(TsfKind Kind, Guid Session, ulong Sequence,
    ulong Segment, ulong Window, string Text = "");

internal static class TsfProtocol
{
    public const int MaxTextLength = 1024;
    public const int FrameSize = 56 + MaxTextLength * 2;
    private const uint Magic = 0x36534956; // VIS6, followed by wire version 1.
    private static readonly Encoding Utf16 = new UnicodeEncoding(false, false, true);

    public static byte[] Encode(TsfPacket packet)
    {
        Validate(packet);
        var bytes = new byte[FrameSize];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), (uint)packet.Kind);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), (uint)packet.Text.Length);
        packet.Session.TryWriteBytes(bytes.AsSpan(16, 16));
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(32), packet.Sequence);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(40), packet.Segment);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(48), packet.Window);
        Utf16.GetBytes(packet.Text, bytes.AsSpan(56));
        return bytes;
    }

    public static TsfPacket Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != FrameSize || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != Magic
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) != 1)
            throw TsfDiagnostics.InvalidData(TsfFailureReason.FrameHeader, "Invalid TSF frame header.");
        var length = BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]);
        if (length > MaxTextLength) throw TsfDiagnostics.InvalidData(TsfFailureReason.TextTooLong, "Oversized TSF text.");
        var packet = new TsfPacket((TsfKind)BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]),
            new Guid(bytes.Slice(16, 16)), BinaryPrimitives.ReadUInt64LittleEndian(bytes[32..]),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[40..]),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[48..]),
            Utf16.GetString(bytes.Slice(56, checked((int)length * 2))));
        Validate(packet);
        return packet;
    }

    public static async Task<TsfPacket> ReadAsync(Stream stream, CancellationToken token)
    {
        var bytes = new byte[FrameSize];
        await stream.ReadExactlyAsync(bytes, token);
        return Decode(bytes);
    }

    public static void Validate(TsfPacket p)
    {
        if (!Enum.IsDefined(p.Kind) || p.Session == Guid.Empty || p.Window == 0
            || p.Text.Length > MaxTextLength || p.Text.Contains('\0'))
            throw TsfDiagnostics.InvalidData(TsfFailureReason.PacketInvalid, "Invalid TSF packet.");
        _ = Utf16.GetByteCount(p.Text);
        var text = p.Kind is TsfKind.Partial or TsfKind.Stable;
        if (text && (p.Text.Length == 0 || p.Segment == 0)
            || !text && p.Text.Length != 0
            || p.Kind == TsfKind.Hello && (p.Sequence != 0 || p.Segment != 0)
            || p.Kind != TsfKind.Hello && p.Sequence == 0
            || p.Kind == TsfKind.Ping && p.Segment != 0)
            throw TsfDiagnostics.InvalidData(TsfFailureReason.PacketState, "Invalid TSF packet state.");
    }

    public static void ValidateAck(TsfPacket sent, TsfPacket ack)
    {
        if (ack.Kind != TsfKind.Ack || ack.Session != sent.Session || ack.Sequence != sent.Sequence
            || ack.Segment != sent.Segment || ack.Window != sent.Window || ack.Text.Length != 0)
            throw TsfDiagnostics.InvalidData(TsfFailureReason.AckMismatch, "Mismatched TSF acknowledgment.");
    }
}

internal sealed record TsfUpdate(TsfKind Kind, ulong Segment, string Text, DateTimeOffset ReceivedAt);

internal sealed class TranscriptSegments
{
    private readonly HashSet<string> _closed = new(StringComparer.Ordinal);
    private string? _active;
    private ulong _segment = 1;

    public TsfUpdate? Accept(AsrEvent value, Action<string>? diagnostics = null)
    {
        var (id, text, stable) = value switch
        {
            PartialTranscript p => (p.SegmentId, p.Text, false),
            StableTranscript s => (s.SegmentId, s.Text, true),
            _ => (null, null, false),
        };
        if (text is null) return null;
        if (diagnostics is not null)
        {
            try
            {
                diagnostics($"asr_segment segment={_segment} kind={(stable ? "Stable" : "Partial")} "
                    + $"length={text.Length} identity_present={!string.IsNullOrWhiteSpace(id)} "
                    + $"active={_active is not null} matches_active={_active == id} closed={id is not null && _closed.Contains(id)}");
            }
            catch (Exception) { /* Diagnostic output must not change segment acceptance. */ }
        }
        if (string.IsNullOrWhiteSpace(id))
            throw TsfDiagnostics.InvalidData(TsfFailureReason.SegmentIdentityMissing, "ASR segment identity is missing.");
        if (_closed.Contains(id)) return null;
        if (_active is not null && _active != id)
            throw TsfDiagnostics.InvalidData(TsfFailureReason.SegmentChangedBeforeFinal, "ASR segment changed before final.");
        _active = id;
        var update = new TsfUpdate(stable ? TsfKind.Stable : TsfKind.Partial, _segment, text, value.Timestamp);
        if (stable)
        {
            if (_closed.Count >= 4096)
                throw TsfDiagnostics.InvalidData(TsfFailureReason.SegmentLimit, "ASR session segment limit reached.");
            _closed.Add(id);
            _active = null;
            _segment++;
        }
        return update;
    }
}
