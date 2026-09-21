from __future__ import annotations

import asyncio
import queue
from collections.abc import AsyncIterator
from contextlib import asynccontextmanager
from dataclasses import dataclass
from math import isfinite, sqrt
from struct import iter_unpack
from time import monotonic

from latency_metrics import LatencyMetrics


@dataclass(frozen=True)
class MicrophoneConfig:
    sample_rate: int
    chunk_ms: int
    max_queue_chunks: int
    device: int | str | None = None
    speech_rms_threshold: float = 0.005
    input_latency: str | None = None
    debug_audio_rms: bool = False


class SpeechStartDetector:
    def __init__(self, sample_rate: int, threshold: float):
        if not isfinite(threshold) or not 0 < threshold < 1:
            raise ValueError("Speech RMS threshold must be between 0 and 1.")
        self.sample_rate = sample_rate
        self.threshold = threshold
        self.timestamp: float | None = None
        self._candidate: float | None = None
        self._voiced_samples = 0
        self._previous_end: float | None = None
        self._below_threshold_min: float | None = None
        self._below_threshold_max: float | None = None
        self._above_threshold_min: float | None = None
        self._above_threshold_max: float | None = None

    def process(self, audio: bytes, captured_at: float) -> float | None:
        detecting = self.timestamp is None
        if detecting and self._previous_end is not None and abs(captured_at - self._previous_end) > 0.002:
            self._candidate = None
            self._voiced_samples = 0
        self._previous_end = captured_at + len(audio) / (2 * self.sample_rate)
        frame_bytes = max(1, self.sample_rate // 100) * 2
        for offset in range(0, len(audio), frame_bytes):
            frame = audio[offset:offset + frame_bytes]
            samples = len(frame) // 2
            rms = sqrt(sum(value * value for (value,) in iter_unpack("<h", frame)) / samples) / 32768
            if rms >= self.threshold:
                self._above_threshold_min = rms if self._above_threshold_min is None else min(self._above_threshold_min, rms)
                self._above_threshold_max = rms if self._above_threshold_max is None else max(self._above_threshold_max, rms)
                if not detecting:
                    continue
                if self._candidate is None:
                    self._candidate = captured_at + offset / (2 * self.sample_rate)
                self._voiced_samples += samples
                # Confirm 30 ms of energy, but retain the first frame's timestamp.
                if self._voiced_samples >= self.sample_rate * 0.03:
                    self.timestamp = self._candidate
                    detecting = False
            else:
                self._below_threshold_min = rms if self._below_threshold_min is None else min(self._below_threshold_min, rms)
                self._below_threshold_max = rms if self._below_threshold_max is None else max(self._below_threshold_max, rms)
                if detecting:
                    self._candidate = None
                    self._voiced_samples = 0
        return self.timestamp

    def rms_debug_summary(self) -> dict[str, str | float | None]:
        return {
            "unit": "normalized_pcm16",
            "threshold": self.threshold,
            "threshold_raw_pcm16": self.threshold * 32768,
            "below_threshold_min": self._below_threshold_min,
            "below_threshold_max": self._below_threshold_max,
            "above_threshold_min": self._above_threshold_min,
            "above_threshold_max": self._above_threshold_max,
        }


@dataclass(frozen=True)
class CapturedAudio:
    data: bytes
    captured_at: float
    callback_at: float
    timestamp_source: str


class MicrophoneStream:
    def __init__(self, config: MicrophoneConfig, metrics: LatencyMetrics | None = None):
        self.config = config
        if config.sample_rate <= 0 or self.blocksize <= 0 or config.max_queue_chunks <= 0:
            raise ValueError("Sample rate, audio block size and queue capacity must be positive.")
        self.metrics = metrics
        self._speech_detector = SpeechStartDetector(config.sample_rate, config.speech_rms_threshold)
        self._queue: queue.Queue[CapturedAudio | None] = queue.Queue(
            maxsize=config.max_queue_chunks
        )
        self.dropped_chunks = 0
        self.input_latency_ms: float | None = None

    @property
    def blocksize(self) -> int:
        return int(self.config.sample_rate * self.config.chunk_ms / 1000)

    @asynccontextmanager
    async def open(self) -> AsyncIterator["MicrophoneStream"]:
        try:
            import sounddevice as sd
        except ModuleNotFoundError as exc:
            raise RuntimeError(
                "Missing dependency: sounddevice. Install prototype dependencies with `pip install -r requirements.txt`."
            ) from exc

        stream = sd.RawInputStream(
            samplerate=self.config.sample_rate,
            channels=1,
            dtype="int16",
            blocksize=self.blocksize,
            callback=self._on_audio,
            device=self.config.device,
            latency=self.config.input_latency,
        )
        self.input_latency_ms = stream.latency * 1000
        stream.start()
        try:
            yield self
        finally:
            stream.stop()
            stream.close()
            self._queue.put(None)

    def _on_audio(self, indata, frames, time_info, status) -> None:
        callback_at = monotonic()
        duration = frames / self.config.sample_rate
        adc_time = getattr(time_info, "inputBufferAdcTime", 0.0)
        current_time = getattr(time_info, "currentTime", 0.0)
        age = current_time - adc_time
        if adc_time > 0 and current_time > 0 and isfinite(age) and age >= 0:
            captured_at = callback_at - age
            timestamp_source = "portaudio_adc"
        else:
            captured_at = callback_at - duration
            timestamp_source = "callback_estimate"
        if status:
            print(f"[audio-warning] {status}", flush=True)
        try:
            self._queue.put_nowait(CapturedAudio(bytes(indata), captured_at, callback_at, timestamp_source))
        except queue.Full:
            self.dropped_chunks += 1

    async def chunks(self) -> AsyncIterator[bytes]:
        while True:
            chunk = await asyncio.to_thread(self._queue.get)
            if chunk is None:
                return
            if self.metrics is not None:
                self.metrics.record_audio_capture(
                    chunk.captured_at, chunk.callback_at, self.metrics.monotonic_now(),
                    len(chunk.data) * 1000 / (2 * self.config.sample_rate),
                    self._queue.qsize(), chunk.timestamp_source,
                )
                speech_start = self._speech_detector.process(chunk.data, chunk.captured_at)
                if speech_start is not None:
                    self.metrics.mark_speech_start(speech_start)
            yield chunk.data

    def rms_debug_summary(self) -> dict[str, str | float | None]:
        return self._speech_detector.rms_debug_summary()
