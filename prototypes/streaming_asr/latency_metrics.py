from __future__ import annotations

from dataclasses import dataclass, field
from statistics import median
from time import monotonic
from typing import Callable, Literal


TranscriptKind = Literal["partial", "stable"]


@dataclass(frozen=True)
class TranscriptUpdate:
    kind: TranscriptKind
    text: str
    timestamp: float


@dataclass(frozen=True)
class ContinuousLagObservation:
    update_count: int
    max_update_gap_ms: float | None
    average_update_gap_ms: float | None
    median_update_gap_ms: float | None = None


@dataclass(frozen=True)
class LatencySummary:
    recording_start: float | None
    first_audio_chunk_sent: float | None
    first_partial_transcript: float | None
    first_stable_transcript: float | None
    session_duration_ms: float | None
    first_transcript_latency_ms: float | None
    stable_transcript_latency_ms: float | None
    transcript_update_count: int
    continuous_lag: ContinuousLagObservation
    speech_start_timestamp: float | None
    speech_start_to_first_partial_ms: float | None
    first_audio_sent_to_first_partial_ms: float | None
    recording_start_to_speech_start_ms: float | None


@dataclass
class LatencyMetrics:
    monotonic_now: Callable[[], float] = monotonic
    recording_start: float | None = None
    first_audio_chunk_sent: float | None = None
    first_partial_transcript: float | None = None
    first_stable_transcript: float | None = None
    transcript_updates: list[TranscriptUpdate] = field(default_factory=list)
    speech_start_timestamp: float | None = None
    first_audio_capture_timestamp: float | None = None
    first_audio_callback_timestamp: float | None = None
    audio_queue_depth_max: int = 0
    audio_timestamp_fallback_count: int = 0
    _last_audio_send_start: float | None = None
    _audio_observations: dict[str, list[float]] = field(default_factory=dict)

    def mark_speech_start(self, timestamp: float) -> None:
        if self.speech_start_timestamp is None:
            self.speech_start_timestamp = timestamp

    def record_audio_capture(
        self, captured_at: float, callback_at: float, dequeued_at: float,
        duration_ms: float, queue_depth: int, timestamp_source: str,
    ) -> None:
        if self.first_audio_capture_timestamp is None:
            self.first_audio_capture_timestamp = captured_at
            self.first_audio_callback_timestamp = callback_at
        self.audio_queue_depth_max = max(self.audio_queue_depth_max, queue_depth)
        self.audio_timestamp_fallback_count += timestamp_source != "portaudio_adc"
        for name, value in (
            ("audio_chunk_actual_ms", duration_ms),
            ("capture_to_callback_ms", (callback_at - captured_at) * 1000),
            ("audio_queue_wait_ms", (dequeued_at - callback_at) * 1000),
        ):
            self._audio_observations.setdefault(name, []).append(value)

    def record_audio_send(self, started_at: float, completed_at: float) -> None:
        self.mark_first_audio_chunk_sent(completed_at)
        self._audio_observations.setdefault("websocket_send_duration_ms", []).append(
            (completed_at - started_at) * 1000
        )
        if self._last_audio_send_start is not None:
            self._audio_observations.setdefault("audio_send_interval_ms", []).append(
                (started_at - self._last_audio_send_start) * 1000
            )
        self._last_audio_send_start = started_at

    def audio_diagnostics(self) -> dict[str, float | int | None]:
        result = {
            "first_audio_capture_timestamp": self.first_audio_capture_timestamp,
            "first_audio_callback_timestamp": self.first_audio_callback_timestamp,
            "audio_queue_depth_max": self.audio_queue_depth_max,
            "audio_timestamp_fallback_count": self.audio_timestamp_fallback_count,
        }
        for name in (
            "audio_chunk_actual_ms", "capture_to_callback_ms", "audio_queue_wait_ms",
            "websocket_send_duration_ms", "audio_send_interval_ms",
        ):
            values = self._audio_observations.get(name, [])
            result[name + "_median"] = round(median(values), 3) if values else None
            result[name + "_max"] = round(max(values), 3) if values else None
            result[name + "_count"] = len(values)
        return result

    def mark_recording_start(self, timestamp: float | None = None) -> None:
        self.recording_start = self.monotonic_now() if timestamp is None else timestamp

    def mark_first_audio_chunk_sent(self, timestamp: float | None = None) -> None:
        if self.first_audio_chunk_sent is None:
            self.first_audio_chunk_sent = self.monotonic_now() if timestamp is None else timestamp

    def record_transcript_update(self, update: TranscriptUpdate) -> None:
        self.transcript_updates.append(update)
        if update.kind == "partial" and self.first_partial_transcript is None:
            self.first_partial_transcript = update.timestamp
        if (
            update.kind == "stable"
            and self.first_stable_transcript is None
            and is_valid_transcript_text(update.text)
        ):
            self.first_stable_transcript = update.timestamp

    def continuous_lag_observation(self) -> ContinuousLagObservation:
        if len(self.transcript_updates) < 2:
            return ContinuousLagObservation(
                update_count=len(self.transcript_updates),
                max_update_gap_ms=None,
                average_update_gap_ms=None,
            )

        gaps_ms = [
            round((current.timestamp - previous.timestamp) * 1000, 3)
            for previous, current in zip(self.transcript_updates, self.transcript_updates[1:])
        ]
        return ContinuousLagObservation(
            update_count=len(self.transcript_updates),
            max_update_gap_ms=max(gaps_ms),
            average_update_gap_ms=round(sum(gaps_ms) / len(gaps_ms), 3),
            median_update_gap_ms=round(median(gaps_ms), 3),
        )

    def summary(self, ended_at: float | None = None) -> LatencySummary:
        end_time = self.monotonic_now() if ended_at is None else ended_at
        first_latency = self._elapsed_ms(self.recording_start, self.first_partial_transcript)
        stable_latency = self._elapsed_ms(self.recording_start, self.first_stable_transcript)
        duration = self._elapsed_ms(self.recording_start, end_time)
        return LatencySummary(
            recording_start=self.recording_start,
            first_audio_chunk_sent=self.first_audio_chunk_sent,
            first_partial_transcript=self.first_partial_transcript,
            first_stable_transcript=self.first_stable_transcript,
            session_duration_ms=duration,
            first_transcript_latency_ms=first_latency,
            stable_transcript_latency_ms=stable_latency,
            transcript_update_count=len(self.transcript_updates),
            continuous_lag=self.continuous_lag_observation(),
            speech_start_timestamp=self.speech_start_timestamp,
            speech_start_to_first_partial_ms=self._elapsed_ms(
                self.speech_start_timestamp, self.first_partial_transcript
            ),
            first_audio_sent_to_first_partial_ms=self._elapsed_ms(
                self.first_audio_chunk_sent, self.first_partial_transcript
            ),
            recording_start_to_speech_start_ms=self._elapsed_ms(
                self.recording_start, self.speech_start_timestamp
            ),
        )

    @staticmethod
    def _elapsed_ms(start: float | None, end: float | None) -> float | None:
        if start is None or end is None:
            return None
        return round((end - start) * 1000, 3)


def is_valid_transcript_text(text: str) -> bool:
    stripped = text.strip()
    if not stripped:
        return False
    return any(char.isalnum() for char in stripped)
