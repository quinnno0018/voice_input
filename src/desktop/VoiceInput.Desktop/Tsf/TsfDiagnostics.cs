using System.IO;

namespace VoiceInput.Desktop.Tsf;

internal enum TsfFailureReason
{
    SegmentIdentityMissing, SegmentChangedBeforeFinal, SegmentLimit,
    FrameHeader, TextTooLong, PacketInvalid, PacketState, AckMismatch, TargetMismatch,
    AudioQueueOverflow, TranscriptQueueOverflow,
}

internal static class TsfDiagnostics
{
    public static InvalidDataException InvalidData(TsfFailureReason reason, string message)
    {
        var error = new InvalidDataException(message);
        error.Data["tsf_reason"] = reason;
        return error;
    }

    public static string DescribeFailure(Exception error)
    {
        var reason = error.Data["tsf_reason"] is TsfFailureReason value && Enum.IsDefined(value)
            ? value.ToString() : "Unclassified";
        // Exception messages can contain provider text or credentials; only emit typed metadata.
        return $"type={error.GetType().Name} reason={reason} hresult=0x{error.HResult:X8}";
    }

    public static IOException QueueOverflow(TsfFailureReason reason)
    {
        var error = new IOException("Streaming queue overflow.");
        error.Data["tsf_reason"] = reason;
        return error;
    }

    public static string UserMessage(Exception error) => error.Data["tsf_reason"] switch
    {
        TsfFailureReason.AudioQueueOverflow =>
            "Voice input stopped: audio backlog exceeded the buffer. Audio was dropped; see the diagnostic log.",
        TsfFailureReason.TranscriptQueueOverflow =>
            "Voice input stopped: text updates could not keep up. See the diagnostic log.",
        _ => "Voice input stopped: ASR or TSF connection failed. See local diagnostic log.",
    };
}
