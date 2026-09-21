using System.Text.Json;

namespace VoiceInput.Desktop.Asr;

public static class ParaformerEventParser
{
    public static AsrEvent Parse(string json, DateTimeOffset? receivedAt = null, Action<string>? diagnostics = null)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var timestamp = receivedAt ?? DateTimeOffset.UtcNow;
        var header = PropertyOrDefault(root, "header");
        var payload = PropertyOrDefault(root, "payload");
        var eventName = StringOrEmpty(header, "event");

        if (eventName == "task-started")
        {
            return new ConnectionState("task_started", timestamp);
        }

        if (eventName == "task-finished")
        {
            return new ConnectionState("task_finished", timestamp);
        }

        if (eventName == "task-failed")
        {
            var message = StringOrEmpty(header, "error_message");
            return new AsrError(
                string.IsNullOrWhiteSpace(message) ? "Paraformer task failed." : message,
                timestamp);
        }

        if (eventName == "result-generated")
        {
            var output = PropertyOrDefault(payload, "output");
            var sentence = PropertyOrDefault(output, "sentence");
            var text = StringOrEmpty(sentence, "text");
            var hasContent = text.Any(char.IsLetterOrDigit);
            if (diagnostics is not null)
            {
                try
                {
                    diagnostics($"result_generated begin_ms={NumberMetadata(PropertyOrDefault(sentence, "begin_time"))} "
                        + $"end_ms={NumberMetadata(PropertyOrDefault(sentence, "end_time"))} "
                        + $"sentence_end={BoolMetadata(PropertyOrDefault(sentence, "sentence_end"))} "
                        + $"heartbeat={BoolMetadata(PropertyOrDefault(sentence, "heartbeat"))} "
                        + $"text_kind={PropertyOrDefault(sentence, "text").ValueKind} length={text.Length} has_content={hasContent}");
                }
                catch (Exception) { /* Diagnostics cannot interrupt ASR or expose raw payloads. */ }
            }
            if (BoolOrFalse(sentence, "heartbeat"))
            {
                return new ConnectionState("heartbeat", timestamp);
            }

            if (!hasContent)
            {
                return new ConnectionState("ignored_invalid_transcript", timestamp);
            }

            var begin = PropertyOrDefault(sentence, "begin_time");
            var segmentId = begin.ValueKind == JsonValueKind.Number
                && begin.TryGetInt64(out var milliseconds) && milliseconds >= 0
                    ? milliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : null;
            return BoolOrFalse(sentence, "sentence_end")
                ? new StableTranscript(text, timestamp, segmentId)
                : new PartialTranscript(text, timestamp, segmentId);
        }

        return new ConnectionState(
            string.IsNullOrWhiteSpace(eventName) ? "unknown" : eventName,
            timestamp);
    }

    private static JsonElement PropertyOrDefault(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
            ? value
            : default;

    private static string NumberMetadata(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Undefined => "missing",
        JsonValueKind.Null => "null",
        JsonValueKind.Number when value.TryGetInt64(out var number) =>
            number.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => "invalid",
    };

    private static string BoolMetadata(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Undefined => "missing",
        JsonValueKind.Null => "null",
        _ => "invalid",
    };

    private static string StringOrEmpty(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static bool BoolOrFalse(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind is JsonValueKind.True;
}
