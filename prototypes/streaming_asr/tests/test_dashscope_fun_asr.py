import json
import unittest
from contextlib import redirect_stderr
from io import StringIO
from unittest.mock import patch

from dashscope_fun_asr_realtime import (
    DEFAULT_DASHSCOPE_WS_URL,
    build_finish_task_message,
    build_run_task_message,
    normalize_dashscope_event,
    receive_transcripts,
)
from config import AsrConfig
from latency_metrics import LatencyMetrics


class DashScopeFunAsrTest(unittest.IsolatedAsyncioTestCase):
    def test_builds_run_task_message_for_realtime_recognition(self):
        message = build_run_task_message(
            AsrConfig(
                provider="dashscope_fun_asr_realtime",
                api_key="test",
                ws_url=DEFAULT_DASHSCOPE_WS_URL,
                model="fun-asr-realtime",
                sample_rate=16000,
                audio_format="pcm",
            ),
            task_id="task-1",
        )

        payload = json.loads(message)
        self.assertEqual(payload["header"]["action"], "run-task")
        self.assertEqual(payload["header"]["task_id"], "task-1")
        self.assertEqual(payload["payload"]["task_group"], "audio")
        self.assertEqual(payload["payload"]["task"], "asr")
        self.assertEqual(payload["payload"]["function"], "recognition")
        self.assertEqual(payload["payload"]["model"], "fun-asr-realtime")
        self.assertEqual(payload["payload"]["parameters"]["format"], "pcm")
        self.assertEqual(payload["payload"]["parameters"]["sample_rate"], 16000)
        self.assertEqual(payload["payload"]["parameters"]["language_hints"], ["zh"])
        self.assertFalse(payload["payload"]["parameters"]["semantic_punctuation_enabled"])
        self.assertEqual(payload["payload"]["input"], {})

    def test_builds_finish_task_message(self):
        payload = json.loads(build_finish_task_message(task_id="task-1"))

        self.assertEqual(payload["header"]["action"], "finish-task")
        self.assertEqual(payload["header"]["task_id"], "task-1")

    def test_normalizes_result_generated_as_partial_or_stable(self):
        partial = normalize_dashscope_event(
            {
                "header": {"event": "result-generated", "task_id": "task-1"},
                "payload": {"output": {"sentence": {"text": "你好", "sentence_end": False}}},
            },
            now=1.0,
        )
        stable = normalize_dashscope_event(
            {
                "header": {"event": "result-generated", "task_id": "task-1"},
                "payload": {"output": {"sentence": {"text": "你好世界", "sentence_end": True}}},
            },
            now=2.0,
        )

        self.assertEqual(partial.kind, "partial")
        self.assertEqual(partial.text, "你好")
        self.assertEqual(stable.kind, "stable")
        self.assertEqual(stable.text, "你好世界")

    def test_ignores_invalid_result_generated_text(self):
        event = normalize_dashscope_event(
            {
                "header": {"event": "result-generated", "task_id": "task-1"},
                "payload": {"output": {"sentence": {"text": "**。", "sentence_end": True}}},
            },
            now=3.0,
        )

        self.assertEqual(event.kind, "connection_state")
        self.assertEqual(event.state, "ignored_invalid_transcript")

    def test_normalizes_heartbeat_as_connection_state(self):
        event = normalize_dashscope_event(
            {
                "header": {"event": "result-generated", "task_id": "task-1"},
                "payload": {"output": {"sentence": {"text": "", "sentence_end": False, "heartbeat": True}}},
            },
            now=3.5,
        )

        self.assertEqual(event.kind, "connection_state")
        self.assertEqual(event.state, "heartbeat")

    def test_normalizes_task_failed_as_error(self):
        event = normalize_dashscope_event(
            {"header": {"event": "task-failed", "error_message": "bad request"}},
            now=3.0,
        )

        self.assertEqual(event.kind, "error")
        self.assertEqual(event.message, "bad request")

    async def test_receive_yields_every_valid_partial_and_debugs_arrival_gap(self):
        class Messages:
            def __aiter__(self):
                return self

            async def __anext__(self):
                if not events:
                    raise StopAsyncIteration
                return json.dumps(events.pop(0))

        events = [
            {"header": {"event": "result-generated"}, "payload": {"output": {"sentence": {"text": "first", "sentence_end": False}}}},
            {"header": {"event": "result-generated"}, "payload": {"output": {"sentence": {"text": "second", "sentence_end": False}}}},
        ]
        stderr = StringIO()
        metrics = LatencyMetrics()
        with patch("dashscope_fun_asr_realtime.monotonic", side_effect=[1.0, 3.1]), redirect_stderr(stderr):
            received = [event async for event in receive_transcripts(Messages(), metrics, debug_raw_events=True)]

        self.assertEqual([event.text for event in received], ["first", "second"])
        self.assertEqual(metrics.summary().transcript_update_count, 2)
        self.assertIn("arrival_gap_ms=2100.000", stderr.getvalue())


if __name__ == "__main__":
    unittest.main()



