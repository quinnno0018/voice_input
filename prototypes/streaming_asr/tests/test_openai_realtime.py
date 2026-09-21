import unittest

from config import AsrConfig, OPENAI_WS_URL
from openai_realtime import build_session_update
from transcript_events import normalize_server_event


class OpenAiRealtimeTest(unittest.TestCase):
    def test_builds_transcription_session_update(self):
        payload = build_session_update(
            AsrConfig(
                provider="openai",
                api_key="test",
                ws_url=OPENAI_WS_URL,
                model="gpt-live-transcribe",
                languages=("zh-cn", "en"),
                sample_rate=24000,
            )
        )

        self.assertEqual(payload["type"], "session.update")
        session = payload["session"]
        self.assertEqual(session["type"], "transcription")
        audio_input = session["audio"]["input"]
        self.assertEqual(audio_input["format"]["type"], "audio/pcm")
        self.assertEqual(audio_input["format"]["rate"], 24000)
        self.assertEqual(audio_input["transcription"]["model"], "gpt-live-transcribe")
        self.assertEqual(audio_input["transcription"]["languages"], ["zh-cn", "en"])

    def test_normalizes_partial_and_stable_transcript_events(self):
        partial = normalize_server_event(
            {
                "type": "conversation.item.input_audio_transcription.delta",
                "delta": "你好",
                "item_id": "item-1",
            },
            now=1.0,
        )
        stable = normalize_server_event(
            {
                "type": "conversation.item.input_audio_transcription.completed",
                "transcript": "你好世界",
                "item_id": "item-1",
            },
            now=2.0,
        )

        self.assertEqual(partial.kind, "partial")
        self.assertEqual(partial.text, "你好")
        self.assertEqual(stable.kind, "stable")
        self.assertEqual(stable.text, "你好世界")


if __name__ == "__main__":
    unittest.main()


