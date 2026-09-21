from __future__ import annotations

from dataclasses import dataclass
from time import monotonic
from typing import Any, Literal


TranscriptEventKind = Literal["partial", "stable", "other", "error"]


@dataclass(frozen=True)
class NormalizedTranscriptEvent:
    kind: TranscriptEventKind
    text: str
    raw_type: str
    timestamp: float
    item_id: str | None = None
    message: str | None = None


def normalize_server_event(
    event: dict[str, Any], now: float | None = None
) -> NormalizedTranscriptEvent:
    timestamp = monotonic() if now is None else now
    event_type = str(event.get("type") or "")

    if event_type == "conversation.item.input_audio_transcription.delta":
        return NormalizedTranscriptEvent(
            kind="partial",
            text=str(event.get("delta") or ""),
            raw_type=event_type,
            timestamp=timestamp,
            item_id=event.get("item_id"),
        )

    if event_type == "conversation.item.input_audio_transcription.completed":
        return NormalizedTranscriptEvent(
            kind="stable",
            text=str(event.get("transcript") or ""),
            raw_type=event_type,
            timestamp=timestamp,
            item_id=event.get("item_id"),
        )

    if event_type == "error":
        error = event.get("error") or {}
        return NormalizedTranscriptEvent(
            kind="error",
            text="",
            raw_type=event_type,
            timestamp=timestamp,
            message=str(error.get("message") or event),
        )

    return NormalizedTranscriptEvent(
        kind="other",
        text="",
        raw_type=event_type,
        timestamp=timestamp,
        item_id=event.get("item_id"),
    )
