import json
import unittest

from config import AsrConfig, DASHSCOPE_PARAFORMER_WS_URL
from dashscope_paraformer_realtime import (
    build_finish_task_message,
    build_run_task_message,
    normalize_paraformer_event,
    receive_transcripts,
)
from latency_metrics import LatencyMetrics
from provider_events import AsrError, ConnectionState, PartialTranscript, StableTranscript


class Messages:
    def __init__(self, events):
        self.events = list(events)

    def __aiter__(self):
        return self

    async def __anext__(self):
        if not self.events:
            raise StopAsyncIteration
        return json.dumps(self.events.pop(0))


class DashScopeParaformerTest(unittest.IsolatedAsyncioTestCase):
    def config(self):
        return AsrConfig(
            provider="dashscope_paraformer_realtime",
            api_key="test",
            ws_url=DASHSCOPE_PARAFORMER_WS_URL,
            model="paraformer-realtime-v2",
            languages=("zh",),
            sample_rate=16000,
            chunk_ms=100,
            audio_format="pcm",
        )

    def test_builds_paraformer_v2_realtime_request(self):
        payload = json.loads(build_run_task_message(self.config(), task_id="task-1"))
        parameters = payload["payload"]["parameters"]

        self.assertEqual(payload["header"], {
            "action": "run-task", "task_id": "task-1", "streaming": "duplex"
        })
        self.assertEqual(payload["payload"]["task_group"], "audio")
        self.assertEqual(payload["payload"]["task"], "asr")
        self.assertEqual(payload["payload"]["function"], "recognition")
        self.assertEqual(payload["payload"]["model"], "paraformer-realtime-v2")
        self.assertEqual(parameters["format"], "pcm")
        self.assertEqual(parameters["sample_rate"], 16000)
        self.assertEqual(parameters["language_hints"], ["zh"])
        self.assertFalse(parameters["disfluency_removal_enabled"])
        self.assertFalse(parameters["semantic_punctuation_enabled"])
        self.assertEqual(payload["payload"]["input"], {})

    def test_builds_finish_task_request(self):
        payload = json.loads(build_finish_task_message("task-1"))
        self.assertEqual(payload["header"]["action"], "finish-task")
        self.assertEqual(payload["header"]["task_id"], "task-1")

    def test_maps_standard_provider_events(self):
        partial = normalize_paraformer_event({
            "header": {"event": "result-generated"},
            "payload": {"output": {"sentence": {"text": "hello", "sentence_end": False}}},
        }, now=1.0)
        stable = normalize_paraformer_event({
            "header": {"event": "result-generated"},
            "payload": {"output": {"sentence": {"text": "hello world", "sentence_end": True}}},
        }, now=2.0)
        error = normalize_paraformer_event({
            "header": {"event": "task-failed", "error_message": "bad request"},
            "payload": {},
        }, now=3.0)
        started = normalize_paraformer_event({
            "header": {"event": "task-started"}, "payload": {}
        }, now=4.0)

        self.assertIsInstance(partial, PartialTranscript)
        self.assertIsInstance(stable, StableTranscript)
        self.assertIsInstance(error, AsrError)
        self.assertIsInstance(started, ConnectionState)

    async def test_receive_records_every_valid_partial_and_stable(self):
        websocket = Messages([
            {"header": {"event": "result-generated"}, "payload": {"output": {"sentence": {"text": "one", "sentence_end": False}}}},
            {"header": {"event": "result-generated"}, "payload": {"output": {"sentence": {"text": "one two", "sentence_end": False}}}},
            {"header": {"event": "result-generated"}, "payload": {"output": {"sentence": {"text": "one two", "sentence_end": True}}}},
        ])
        metrics = LatencyMetrics()

        received = [event async for event in receive_transcripts(websocket, metrics)]

        self.assertEqual([event.kind for event in received], ["partial", "partial", "stable"])
        self.assertEqual(metrics.summary().transcript_update_count, 3)

    def test_heartbeat_and_invalid_text_are_not_transcripts(self):
        heartbeat = normalize_paraformer_event({
            "header": {"event": "result-generated"},
            "payload": {"output": {"sentence": {"text": "", "heartbeat": True, "sentence_end": False}}},
        }, now=1.0)
        invalid = normalize_paraformer_event({
            "header": {"event": "result-generated"},
            "payload": {"output": {"sentence": {"text": "**.", "sentence_end": True}}},
        }, now=2.0)

        self.assertEqual(heartbeat.state, "heartbeat")
        self.assertEqual(invalid.state, "ignored_invalid_transcript")


if __name__ == "__main__":
    unittest.main()
