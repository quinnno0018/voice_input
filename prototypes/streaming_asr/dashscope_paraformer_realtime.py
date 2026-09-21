from __future__ import annotations

import asyncio
import json
import sys
from collections.abc import AsyncIterator
from time import monotonic
from typing import Any
from uuid import uuid4

import websockets

from config import AsrConfig
from latency_metrics import LatencyMetrics, TranscriptUpdate, is_valid_transcript_text
from provider_events import AsrError, ConnectionState, PartialTranscript, ProviderEvent, StableTranscript


class DashScopeParaformerError(RuntimeError):
    pass


def build_run_task_message(config: AsrConfig, task_id: str | None = None) -> str:
    resolved_task_id = task_id or uuid4().hex
    return json.dumps(
        {
            "header": {
                "action": "run-task",
                "task_id": resolved_task_id,
                "streaming": "duplex",
            },
            "payload": {
                "task_group": "audio",
                "task": "asr",
                "function": "recognition",
                "model": config.model,
                "parameters": {
                    "format": config.audio_format,
                    "sample_rate": config.sample_rate,
                    "language_hints": list(config.languages),
                    "disfluency_removal_enabled": False,
                    "semantic_punctuation_enabled": False,
                },
                "input": {},
            },
        },
        ensure_ascii=False,
    )


def build_finish_task_message(task_id: str) -> str:
    return json.dumps(
        {
            "header": {
                "action": "finish-task",
                "task_id": task_id,
                "streaming": "duplex",
            },
            "payload": {"input": {}},
        },
        ensure_ascii=False,
    )


def normalize_paraformer_event(
    event: dict[str, Any], now: float | None = None
) -> ProviderEvent:
    timestamp = monotonic() if now is None else now
    header = event.get("header") or {}
    payload = event.get("payload") or {}
    event_type = str(header.get("event") or "")

    if event_type == "task-started":
        return ConnectionState(
            state="task_started", raw_type=event_type, timestamp=timestamp
        )
    if event_type == "task-finished":
        return ConnectionState(
            state="task_finished", raw_type=event_type, timestamp=timestamp
        )
    if event_type == "task-failed":
        return AsrError(
            message=str(header.get("error_message") or payload.get("message") or event),
            raw_type=event_type,
            timestamp=timestamp,
        )
    if event_type == "result-generated":
        sentence = ((payload.get("output") or {}).get("sentence") or {})
        if bool(sentence.get("heartbeat")):
            return ConnectionState(
                state="heartbeat", raw_type=event_type, timestamp=timestamp
            )

        text = str(sentence.get("text") or "")
        if not is_valid_transcript_text(text):
            return ConnectionState(
                state="ignored_invalid_transcript",
                raw_type=event_type,
                timestamp=timestamp,
            )
        if bool(sentence.get("sentence_end")):
            return StableTranscript(
                text=text, raw_type=event_type, timestamp=timestamp
            )
        return PartialTranscript(text=text, raw_type=event_type, timestamp=timestamp)

    return ConnectionState(
        state=event_type or "unknown", raw_type=event_type, timestamp=timestamp
    )


async def connect_dashscope_paraformer(config: AsrConfig):
    headers = {"Authorization": f"Bearer {config.api_key}"}
    return await websockets.connect(
        config.ws_url,
        extra_headers=headers,
        ping_interval=20,
        ping_timeout=20,
        max_size=16 * 1024 * 1024,
    )


async def wait_for_task_started(websocket) -> str:
    try:
        async for message in websocket:
            raw_event = _parse_json_message(message)
            header = raw_event.get("header") or {}
            task_id = str(header.get("task_id") or "")
            event = normalize_paraformer_event(raw_event)
            if isinstance(event, AsrError):
                raise DashScopeParaformerError(event.message)
            if isinstance(event, ConnectionState) and event.state == "task_started":
                return task_id
    except websockets.exceptions.ConnectionClosed as exc:
        raise ConnectionError(
            f"Paraformer realtime connection closed before task-started: {exc}"
        ) from None
    raise DashScopeParaformerError(
        "Paraformer connection closed before task-started."
    )


async def send_audio_chunks(
    websocket,
    chunks: AsyncIterator[bytes],
    metrics: LatencyMetrics,
    task_id: str,
) -> None:
    try:
        async for chunk in chunks:
            send_start = metrics.monotonic_now()
            await websocket.send(chunk)
            metrics.record_audio_send(send_start, metrics.monotonic_now())
    except websockets.exceptions.ConnectionClosed as exc:
        raise ConnectionError(
            f"Paraformer realtime connection closed while sending audio: {exc}"
        ) from None
    finally:
        if task_id:
            try:
                await websocket.send(build_finish_task_message(task_id))
            except websockets.exceptions.ConnectionClosed:
                pass
            except asyncio.CancelledError:
                raise


async def receive_transcripts(
    websocket,
    metrics: LatencyMetrics,
    debug_raw_events: bool = False,
) -> AsyncIterator[ProviderEvent]:
    previous_result_received_at: float | None = None
    try:
        async for message in websocket:
            raw_event = _parse_json_message(message)
            event_type = (raw_event.get("header") or {}).get("event")
            received_at = monotonic()
            if debug_raw_events and event_type == "result-generated":
                arrival_gap_ms = (
                    None
                    if previous_result_received_at is None
                    else (received_at - previous_result_received_at) * 1000
                )
                previous_result_received_at = received_at
                gap_text = (
                    "n/a" if arrival_gap_ms is None else f"{arrival_gap_ms:.3f}"
                )
                print(
                    f"[debug][dashscope][paraformer][result-generated] "
                    f"received_at={received_at:.6f} arrival_gap_ms={gap_text} payload="
                    + json.dumps(raw_event.get("payload") or {}, ensure_ascii=False),
                    file=sys.stderr,
                    flush=True,
                )

            event = normalize_paraformer_event(raw_event, now=received_at)
            if isinstance(event, AsrError):
                raise DashScopeParaformerError(event.message)
            if isinstance(event, PartialTranscript):
                metrics.record_transcript_update(
                    TranscriptUpdate(
                        kind="partial", text=event.text, timestamp=event.timestamp
                    )
                )
            elif isinstance(event, StableTranscript):
                metrics.record_transcript_update(
                    TranscriptUpdate(
                        kind="stable", text=event.text, timestamp=event.timestamp
                    )
                )
            yield event
    except websockets.exceptions.ConnectionClosed as exc:
        raise ConnectionError(
            f"Paraformer realtime connection closed while receiving events: {exc}"
        ) from None


def _parse_json_message(message) -> dict[str, Any]:
    if isinstance(message, bytes):
        message = message.decode("utf-8")
    try:
        return json.loads(message)
    except json.JSONDecodeError as exc:
        raise DashScopeParaformerError(
            f"Received malformed Paraformer event: {exc}"
        ) from exc
