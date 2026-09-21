import asyncio
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import unittest
import uuid
from types import SimpleNamespace

import websockets

from send_probe import buffer_snapshot, load_plan, run_python, summarize, wait_for_finish


ROOT = Path(__file__).resolve().parents[3]


def directory():
    path = ROOT / "artifacts" / "send-probe-tests" / uuid.uuid4().hex
    path.mkdir(parents=True)
    return path


def fixture(path):
    audio = bytes(3200 * 100)
    (path / "fixture.pcm").write_bytes(audio)
    plan = {
        "endpoint": "wss://dashscope.aliyuncs.com/api-ws/v1/inference",
        "fixture_sha256": hashlib.sha256(audio).hexdigest(),
        "chunk_bytes": 3200,
        "chunk_ms": 100,
        "run_task": {
            "header": {"action": "run-task", "task_id": "probe", "streaming": "duplex"},
            "payload": {
                "task_group": "audio", "task": "asr", "function": "recognition",
                "model": "paraformer-realtime-v2", "input": {},
                "parameters": {
                    "format": "pcm", "sample_rate": 16000, "language_hints": ["zh", "en"],
                    "disfluency_removal_enabled": False, "semantic_punctuation_enabled": False,
                    "max_sentence_silence": 1200, "heartbeat": True,
                },
            },
        },
    }
    target = path / "plan.json"
    target.write_text(json.dumps(plan), encoding="utf-8")
    return target, plan


class SendProbeTests(unittest.TestCase):
    def test_fixture_has_exact_shape_and_hash(self):
        target, expected = fixture(directory())
        plan, audio = load_plan(target)
        self.assertEqual(plan, expected)
        self.assertEqual(len(audio), 320000)
        self.assertFalse(any(audio))

    def test_rejects_tampered_audio(self):
        path = directory()
        target, _ = fixture(path)
        (path / "fixture.pcm").write_bytes(b"invalid")
        with self.assertRaises(ValueError):
            load_plan(target)

    def test_rejects_nonbaseline_format_or_untrusted_endpoint(self):
        for field, value in [("chunk_ms", 50), ("chunk_bytes", 6400),
                             ("endpoint", "wss://example.com/"),
                             ("endpoint", "wss://dashscope.aliyuncs.com/api-ws/v1/inference?key=secret")]:
            target, plan = fixture(directory())
            plan[field] = value
            target.write_text(json.dumps(plan), encoding="utf-8")
            with self.subTest(field=field, value=value), self.assertRaises(ValueError):
                load_plan(target)

    def test_summary_does_not_hide_slow_sends_or_missing_data(self):
        self.assertEqual(summarize([]), {"median_ms": None, "max_ms": None, "over_100ms": 0})
        self.assertEqual(summarize([1, 2, 150, 600]),
                         {"median_ms": 76.0, "max_ms": 600, "over_100ms": 2})

    def test_tls_and_underlying_buffers_are_separate_read_only_samples(self):
        transport = SimpleNamespace(get_write_buffer_size=lambda: 0,
            _ssl_protocol=SimpleNamespace(_transport=SimpleNamespace(get_write_buffer_size=lambda: 32768)))
        self.assertEqual(buffer_snapshot(transport),
                         {"transport_pending_bytes": 0,
                          "tls_underlying_pending_bytes": 32768 if sys.version_info[:2] == (3, 12) else None})
        self.assertEqual(buffer_snapshot(None),
                         {"transport_pending_bytes": None, "tls_underlying_pending_bytes": None})


class FinishObservationTests(unittest.IsolatedAsyncioTestCase):
    async def test_first_checkpoint_does_not_cancel_receiver(self):
        receiver = asyncio.create_task(asyncio.sleep(0.04, result="completed"))
        result = {}
        self.assertEqual(await wait_for_finish(receiver, result, 0.01, 0.5), "completed")
        self.assertTrue(result["finish_wait_exceeded_5s"])
        self.assertGreater(result["finish_ack_wait_ms"], 10)

    async def test_deadline_preserves_pending_receiver_for_owner_cleanup(self):
        receiver = asyncio.get_running_loop().create_future()
        result = {}
        with self.assertRaises(TimeoutError):
            await wait_for_finish(receiver, result, 0.01, 0.03)
        self.assertFalse(receiver.cancelled())
        self.assertTrue(result["finish_wait_exceeded_5s"])
        self.assertGreater(result["finish_ack_wait_ms"], 20)
        receiver.cancel()

    async def test_receiver_error_is_not_reclassified_as_late_reply(self):
        receiver = asyncio.get_running_loop().create_future()
        receiver.set_exception(TimeoutError("private-error"))
        result = {}
        with self.assertRaises(TimeoutError):
            await wait_for_finish(receiver, result, 0.01, 0.03)
        self.assertNotIn("finish_wait_exceeded_5s", result)
        self.assertNotIn("private-error", json.dumps(result))


@unittest.skipUnless(os.environ.get("VOICE_INPUT_TEST_DOTNET") and
                     os.environ.get("VOICE_INPUT_TEST_ASSEMBLY"), "requires the built C# send probe")
class SendProbeLoopbackTests(unittest.IsolatedAsyncioTestCase):
    async def prepare(self):
        path = directory()
        command = [os.environ["VOICE_INPUT_TEST_DOTNET"], os.environ["VOICE_INPUT_TEST_ASSEMBLY"],
                   "--send-probe", "prepare", str(path)]
        marker = "fixture-only-key-not-a-credential"
        prepared = await asyncio.to_thread(subprocess.run, command, capture_output=True, timeout=15,
                                          env=dict(os.environ, DASHSCOPE_API_KEY=marker))
        self.assertEqual(prepared.returncode, 0, prepared.stderr.decode(errors="replace"))
        target = path / "plan.json"
        text = target.read_text(encoding="utf-8")
        self.assertNotIn("Authorization", text)
        self.assertNotIn("ApiKey", text)
        self.assertNotIn(marker, text)
        return path, target, json.loads(text)

    async def csharp(self, target, output):
        env = dict(os.environ, DASHSCOPE_API_KEY="loopback")
        process = await asyncio.create_subprocess_exec(
            env["VOICE_INPUT_TEST_DOTNET"], env["VOICE_INPUT_TEST_ASSEMBLY"],
            "--send-probe", "run", str(target), str(output), env=env,
            stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE)
        try:
            await asyncio.wait_for(process.communicate(), 95)
        except BaseException:
            process.kill()
            await process.communicate()
            raise
        self.assertTrue(output.exists())
        return json.loads(output.read_text(encoding="utf-8"))

    async def test_same_payload_pacing_and_clean_finish(self):
        path, target, plan = await self.prepare()
        sessions = []

        async def server(socket):
            request = json.loads(await socket.recv())
            self.assertEqual(socket.request_headers["Authorization"], "Bearer loopback")
            self.assertFalse(socket.extensions)
            self.assertEqual(request["payload"], plan["run_task"]["payload"])
            await socket.send(json.dumps({"header": {"event": "task-started"}}))
            frames, times = [], []
            async for message in socket:
                if isinstance(message, bytes):
                    frames.append(message)
                    times.append(asyncio.get_running_loop().time())
                else:
                    self.assertEqual(json.loads(message)["header"]["action"], "finish-task")
                    sessions.append((frames, times))
                    await socket.send(json.dumps({"header": {"event": "task-finished"}}))
                    return

        async with websockets.serve(server, "127.0.0.1", 0) as host:
            plan["endpoint"] = f"ws://127.0.0.1:{host.sockets[0].getsockname()[1]}"
            target.write_text(json.dumps(plan), encoding="utf-8")
            for client in ("python", "csharp"):
                if client == "python":
                    result = await run_python(target, "loopback")
                else:
                    result = await self.csharp(target, path / "csharp.json")
                self.assertEqual(result["outcome"], "completed", result)
                self.assertEqual(result["sent_chunks"], 100)
                self.assertEqual(result["total_audio_bytes"], 320000)
                self.assertEqual(result["fixture_sha256"], plan["fixture_sha256"])
                self.assertEqual(len(result["send_ms"]), 100)
                self.assertEqual(result.get("diagnostics_version"), 2)
                self.assertTrue(result["finish_sent"])
                self.assertEqual(result["phase"], "completed")
                self.assertIsNotNone(result["finish_send_ms"])
                self.assertIsNotNone(result["finish_ack_wait_ms"])
                self.assertFalse(result["finish_wait_exceeded_5s"])
                self.assertEqual(result["events"]["counts"]["task_finished"], 1)
                self.assertNotIn("Authorization", json.dumps(result))
                if client == "python":
                    self.assertGreater(len(result["buffer_samples"]), 100)
                    self.assertEqual(result["connection"]["compression"], False)
                    self.assertIsNotNone(result["connection"]["tcp_nodelay"])
                else:
                    self.assertTrue(any(event["event"] == "ConnectStop" for event in result["network_events"]))
        self.assertEqual(len(sessions), 2)
        for frames, times in sessions:
            self.assertEqual(frames, [bytes(3200)] * 100)
            self.assertGreater(times[-1] - times[0], 9.0)

    async def test_server_failure_is_bounded_and_does_not_log_payload(self):
        path, target, plan = await self.prepare()

        async def server(socket):
            await socket.recv()
            await socket.send(json.dumps({"header": {"event": "task-started"}}))
            await socket.recv()
            await socket.send(json.dumps({"header": {"event": "task-failed",
                "error_code": "TEST_FAILURE", "error_message": "private-message-do-not-log"}}))
            await socket.close()

        async with websockets.serve(server, "127.0.0.1", 0) as host:
            plan["endpoint"] = f"ws://127.0.0.1:{host.sockets[0].getsockname()[1]}"
            target.write_text(json.dumps(plan), encoding="utf-8")
            for result in [await run_python(target, "loopback"),
                           await self.csharp(target, path / "failure.json")]:
                self.assertNotEqual(result["outcome"], "completed")
                self.assertFalse(result["send_phase_complete"])
                self.assertLess(result["sent_chunks"], 100)
                self.assertNotIn("private-message", json.dumps(result))
                self.assertEqual(result.get("phase"), "audio_send")
                self.assertEqual(result["events"]["counts"].get("asr_error"), 1)
                self.assertFalse(result["finish_sent"])

    async def test_finish_after_five_seconds_is_observed_without_resending(self):
        path, target, plan = await self.prepare()
        finish_count = 0

        async def server(socket):
            nonlocal finish_count
            await socket.recv()
            await socket.send(json.dumps({"header": {"event": "task-started"}}))
            for _ in range(100):
                self.assertEqual(len(await socket.recv()), 3200)
            self.assertEqual(json.loads(await socket.recv())["header"]["action"], "finish-task")
            finish_count += 1
            await asyncio.sleep(5.2)
            await socket.send(json.dumps({"header": {"event": "task-finished"}}))

        async with websockets.serve(server, "127.0.0.1", 0) as host:
            plan["endpoint"] = f"ws://127.0.0.1:{host.sockets[0].getsockname()[1]}"
            target.write_text(json.dumps(plan), encoding="utf-8")
            for result in [await run_python(target, "loopback"),
                           await self.csharp(target, path / "late.json")]:
                self.assertEqual(result["outcome"], "completed")
                self.assertTrue(result["finish_wait_exceeded_5s"])
                self.assertGreaterEqual(result["finish_ack_wait_ms"], 5000)
                self.assertTrue(result["finish_sent"])
        self.assertEqual(finish_count, 2)

    async def test_close_after_finish_is_not_a_successful_ack(self):
        path, target, plan = await self.prepare()

        async def server(socket):
            await socket.recv()
            await socket.send(json.dumps({"header": {"event": "task-started"}}))
            for _ in range(100):
                await socket.recv()
            await socket.recv()
            await socket.close(reason="private-close-reason")

        async with websockets.serve(server, "127.0.0.1", 0) as host:
            plan["endpoint"] = f"ws://127.0.0.1:{host.sockets[0].getsockname()[1]}"
            target.write_text(json.dumps(plan), encoding="utf-8")
            for result in [await run_python(target, "loopback"),
                           await self.csharp(target, path / "close.json")]:
                self.assertTrue(result["send_phase_complete"])
                self.assertTrue(result["finish_sent"])
                self.assertEqual(result["phase"], "finish_ack")
                self.assertNotEqual(result["outcome"], "completed")
                self.assertNotIn("task_finished", result["events"]["counts"])
                self.assertNotIn("private-close-reason", json.dumps(result))


if __name__ == "__main__":
    unittest.main()
