from __future__ import annotations

import base64
import json
from collections.abc import AsyncIterator
from typing import Any

import websockets

from config import AsrConfig
from latency_metrics import LatencyMetrics, TranscriptUpdate
from provider_events import AsrError, ConnectionState, PartialTranscript, ProviderEvent, StableTranscript
from transcript_events import normalize_server_event


class RealtimeAsrError(RuntimeError):
    pass


def build_session_update(config: AsrConfig) -> dict[str, Any]:
    return {
        "type": "session.update",
        "session": {
            "type": "transcription",
            "audio": {
                "input": {
                    "format": {
                        "type": "audio/pcm",
                        "rate": config.sample_rate,
                    },
                    "transcription": {
                        "model": config.model,
                        "languages": list(config.languages),
                        "prompt": "Transcribe Mandarin Chinese and English speech with low latency.",
                    },
                    "turn_detection": {
                        "type": "server_vad",
                        "threshold": config.vad_threshold,
                        "prefix_padding_ms": config.prefix_padding_ms,
                        "silence_duration_ms": config.silence_duration_ms,
                    },
                    "transcription_delay": config.transcription_delay,
                }
            },
        },
    }


async def connect_realtime(config: AsrConfig):
    headers = {
        "Authorization": f"Bearer {config.api_key}",
        "OpenAI-Beta": "realtime=v1",
    }
    return await websockets.connect(
        config.ws_url,
        extra_headers=headers,
        ping_interval=20,
        ping_timeout=20,
        max_size=16 * 1024 * 1024,
    )


async def send_audio_chunks(
    websocket,
    chunks: AsyncIterator[bytes],
    metrics: LatencyMetrics,
) -> None:
    try:
        async for chunk in chunks:
            payload = {
                "type": "input_audio_buffer.append",
                "audio": base64.b64encode(chunk).decode("ascii"),
            }
            send_start = metrics.monotonic_now()
            await websocket.send(json.dumps(payload))
            metrics.record_audio_send(send_start, metrics.monotonic_now())
    except websockets.exceptions.ConnectionClosed as exc:
        raise ConnectionError(f"Realtime connection closed while sending audio: {exc}") from None


async def receive_transcripts(
    websocket,
    metrics: LatencyMetrics,
) -> AsyncIterator[ProviderEvent]:
    try:
        async for message in websocket:
            try:
                raw_event = json.loads(message)
            except json.JSONDecodeError as exc:
                raise RealtimeAsrError(f"Received malformed realtime event: {exc}") from exc

            normalized = normalize_server_event(raw_event)
            if normalized.kind == "error":
                raise RealtimeAsrError(normalized.message or "Realtime ASR error")
            if normalized.kind == "partial":
                event = PartialTranscript(
                    text=normalized.text,
                    raw_type=normalized.raw_type,
                    timestamp=normalized.timestamp,
                )
                metrics.record_transcript_update(
                    TranscriptUpdate(kind="partial", text=event.text, timestamp=event.timestamp)
                )
                yield event
            elif normalized.kind == "stable":
                event = StableTranscript(
                    text=normalized.text,
                    raw_type=normalized.raw_type,
                    timestamp=normalized.timestamp,
                )
                metrics.record_transcript_update(
                    TranscriptUpdate(kind="stable", text=event.text, timestamp=event.timestamp)
                )
                yield event
            elif normalized.raw_type in {
                "session.created",
                "session.updated",
                "input_audio_buffer.speech_started",
                "input_audio_buffer.speech_stopped",
            }:
                yield ConnectionState(
                    state=normalized.raw_type,
                    raw_type=normalized.raw_type,
                    timestamp=normalized.timestamp,
                )
    except websockets.exceptions.ConnectionClosed as exc:
        raise ConnectionError(f"Realtime connection closed while receiving events: {exc}") from None

