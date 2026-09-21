import asyncio
from contextlib import nullcontext, redirect_stdout
from io import StringIO
from types import SimpleNamespace
import unittest
from unittest.mock import AsyncMock, patch

import websockets

from config import AsrConfig, DASHSCOPE_FUN_ASR_WS_URL, OPENAI_WS_URL
from dashscope_fun_asr_realtime import (
    connect_dashscope,
    receive_transcripts as receive_dashscope_transcripts,
    send_audio_chunks as send_dashscope_audio_chunks,
)
from dashscope_paraformer_realtime import connect_dashscope_paraformer
from latency_metrics import LatencyMetrics
from main import _run_dashscope, _run_openai, _run_paraformer, _wait_for_session
from openai_realtime import connect_realtime, receive_transcripts as receive_openai_transcripts


class ClosedWebsocket:
    def __aiter__(self):
        return self

    async def __anext__(self):
        raise websockets.exceptions.ConnectionClosedError(None, None)

    async def send(self, _message):
        raise websockets.exceptions.ConnectionClosedError(None, None)


async def one_audio_chunk():
    yield b"\x00\x00"


class ProtocolOnlyWebsocket:
    def __init__(self, send_error=None):
        self.send_error = send_error
        self.close_count = 0

    async def send(self, _message):
        if self.send_error is not None:
            raise self.send_error

    async def close(self):
        self.close_count += 1

    async def __aiter__(self):
        yield '{"header": {"event": "task-started", "task_id": "task-1"}}'
        await asyncio.Future()


class WebsocketsCompatTest(unittest.IsolatedAsyncioTestCase):
    async def test_session_closes_protocol_on_success_error_and_cancellation(self):
        for runner, connect_name, provider in (
            (_run_dashscope, "main.connect_dashscope", "dashscope_fun_asr_realtime"),
            (_run_paraformer, "main.connect_dashscope_paraformer", "dashscope_paraformer_realtime"),
            (_run_openai, "main.connect_realtime", "openai"),
        ):
            for error in (None, ConnectionError("connection lost"), asyncio.CancelledError()):
                with self.subTest(provider=provider, error=type(error).__name__):
                    websocket = ProtocolOnlyWebsocket(error)
                    config = AsrConfig(provider=provider, api_key="", ws_url="", model="")
                    microphone = SimpleNamespace(
                        open=nullcontext, chunks=one_audio_chunk, dropped_chunks=0
                    )
                    output = StringIO()
                    with patch(connect_name, new=AsyncMock(return_value=websocket)), redirect_stdout(output):
                        if isinstance(error, asyncio.CancelledError):
                            with self.assertRaises(asyncio.CancelledError):
                                await runner(config, LatencyMetrics(), microphone, 0.01)
                        else:
                            result = await runner(config, LatencyMetrics(), microphone, 0.01)
                            self.assertEqual(result, 0 if error is None else 2)
                            if error is None:
                                self.assertIn("[session] recording started", output.getvalue())
                            else:
                                self.assertIn("[network-error]", output.getvalue())
                    self.assertEqual(websocket.close_count, 1)

    async def test_dashscope_connect_uses_websockets_12_extra_headers(self):
        config = AsrConfig(
            provider="dashscope_fun_asr_realtime",
            api_key="dashscope-key",
            ws_url=DASHSCOPE_FUN_ASR_WS_URL,
            model="fun-asr-realtime",
        )

        with patch("dashscope_fun_asr_realtime.websockets.connect", new_callable=AsyncMock) as connect:
            await connect_dashscope(config)

        kwargs = connect.call_args.kwargs
        self.assertIn("extra_headers", kwargs)
        self.assertNotIn("additional_headers", kwargs)
        self.assertEqual(kwargs["extra_headers"]["Authorization"], "Bearer dashscope-key")

    async def test_openai_connect_uses_websockets_12_extra_headers(self):
        config = AsrConfig(
            provider="openai",
            api_key="openai-key",
            ws_url=OPENAI_WS_URL,
            model="gpt-live-transcribe",
        )

        with patch("openai_realtime.websockets.connect", new_callable=AsyncMock) as connect:
            await connect_realtime(config)

        kwargs = connect.call_args.kwargs
        self.assertIn("extra_headers", kwargs)
        self.assertNotIn("additional_headers", kwargs)
        self.assertEqual(kwargs["extra_headers"]["Authorization"], "Bearer openai-key")

    async def test_paraformer_connect_uses_websockets_12_extra_headers(self):
        config = AsrConfig(
            provider="dashscope_paraformer_realtime",
            api_key="dashscope-key",
            ws_url=DASHSCOPE_FUN_ASR_WS_URL,
            model="paraformer-realtime-v2",
        )

        with patch("dashscope_paraformer_realtime.websockets.connect", new_callable=AsyncMock) as connect:
            await connect_dashscope_paraformer(config)

        kwargs = connect.call_args.kwargs
        self.assertIn("extra_headers", kwargs)
        self.assertNotIn("additional_headers", kwargs)
        self.assertEqual(kwargs["extra_headers"]["Authorization"], "Bearer dashscope-key")

    async def test_dashscope_connection_closed_becomes_network_error(self):
        with self.assertRaises(ConnectionError):
            async for _event in receive_dashscope_transcripts(ClosedWebsocket(), LatencyMetrics()):
                pass

        with self.assertRaises(ConnectionError):
            await send_dashscope_audio_chunks(ClosedWebsocket(), one_audio_chunk(), LatencyMetrics(), "task-1")

    async def test_openai_connection_closed_becomes_network_error(self):
        with self.assertRaises(ConnectionError):
            async for _event in receive_openai_transcripts(ClosedWebsocket(), LatencyMetrics()):
                pass

    async def test_wait_for_session_propagates_sender_network_error(self):
        async def failed_sender():
            await asyncio.sleep(0)
            raise ConnectionError("network-error")

        async def waiting_receiver():
            await asyncio.sleep(30)

        sender = asyncio.create_task(failed_sender())
        receiver = asyncio.create_task(waiting_receiver())

        with self.assertRaises(ConnectionError):
            await _wait_for_session(sender, receiver, duration=5)


if __name__ == "__main__":
    unittest.main()
