import unittest
from contextlib import redirect_stdout
from io import StringIO
from struct import pack
from types import SimpleNamespace
from unittest.mock import patch

from websockets.exceptions import ConnectionClosedError

from latency_metrics import LatencyMetrics
from dashscope_fun_asr_realtime import send_audio_chunks
from main import build_parser, print_summary
from microphone import MicrophoneConfig, MicrophoneStream, SpeechStartDetector


def pcm(value, frames):
    return pack("<h", value) * frames


class SpeechStartTest(unittest.TestCase):
    def test_invalid_thresholds_are_rejected(self):
        for threshold in (0, -0.1, 1, float("nan"), float("inf")):
            with self.subTest(threshold=threshold), self.assertRaises(ValueError):
                SpeechStartDetector(16000, threshold)

    def test_silence_and_short_impulse_do_not_trigger(self):
        detector = SpeechStartDetector(16000, 0.005)
        self.assertIsNone(detector.process(pcm(0, 1600), 1.0))
        self.assertIsNone(detector.process(pcm(2000, 160) + pcm(0, 1440), 1.1))
        self.assertIsNone(detector.process(pcm(100, 1600), 1.2))

    def test_low_gain_speech_is_detected_with_default_threshold(self):
        threshold = MicrophoneConfig(16000, 100, 200).speech_rms_threshold
        self.assertEqual(threshold, 0.005)
        detector = SpeechStartDetector(16000, threshold)
        self.assertIsNone(detector.process(pcm(50, 1600), 1.0))
        self.assertAlmostEqual(detector.process(pcm(300, 1600), 1.1), 1.1)

    def test_rms_debug_distinguishes_below_and_above_threshold_ranges(self):
        detector = SpeechStartDetector(16000, 0.005)
        detector.process(pcm(50, 1600), 1.0)
        detector.process(pcm(300, 1600), 1.1)
        detector.process(pcm(600, 1600), 1.2)
        detector.process(pcm(0, 1600), 1.3)
        debug = detector.rms_debug_summary()
        self.assertEqual(debug["unit"], "normalized_pcm16")
        self.assertEqual(debug["threshold"], 0.005)
        self.assertEqual(debug["threshold_raw_pcm16"], 163.84)
        self.assertLess(debug["below_threshold_max"], 0.005)
        self.assertGreaterEqual(debug["above_threshold_min"], 0.005)
        self.assertGreater(debug["above_threshold_max"], 0.015)
        self.assertEqual(debug["below_threshold_min"], 0.0)

    def test_confirms_across_chunks_and_backdates_to_onset(self):
        detector = SpeechStartDetector(16000, 0.02)
        self.assertIsNone(detector.process(pcm(0, 1280) + pcm(2000, 320), 10.0))
        self.assertAlmostEqual(detector.process(pcm(2000, 1600), 10.1), 10.08)
        self.assertAlmostEqual(detector.process(pcm(2000, 1600), 11.0), 10.08)

    def test_lost_chunk_does_not_confirm_discontinuous_noise(self):
        detector = SpeechStartDetector(16000, 0.02)
        self.assertIsNone(detector.process(pcm(2000, 320), 1.0))
        self.assertIsNone(detector.process(pcm(2000, 160), 1.1))

    def test_onset_is_independent_of_transport_chunk_size(self):
        audio = pcm(0, 1920) + pcm(2000, 2880)
        for chunk_ms in (40, 80, 100):
            with self.subTest(chunk_ms=chunk_ms):
                detector = SpeechStartDetector(16000, 0.02)
                size = chunk_ms * 32
                onset = None
                for offset in range(0, len(audio), size):
                    onset = detector.process(audio[offset:offset + size], 10 + offset / 32000)
                self.assertAlmostEqual(onset, 10.12)


class MicrophoneMetricsTest(unittest.IsolatedAsyncioTestCase):
    async def test_device_latency_hint_and_reported_latency(self):
        opened = []

        class InputStream:
            latency = 0.025

            def __init__(self, **kwargs):
                opened.append(kwargs)

            def start(self):
                pass

            def stop(self):
                pass

            def close(self):
                pass

        for hint in (None, "low"):
            microphone = MicrophoneStream(MicrophoneConfig(16000, 40, 200, input_latency=hint))
            with patch.dict("sys.modules", {"sounddevice": SimpleNamespace(RawInputStream=InputStream)}):
                async with microphone.open():
                    self.assertEqual(microphone.input_latency_ms, 25.0)
            self.assertEqual(opened[-1]["latency"], hint)
            self.assertEqual(opened[-1]["blocksize"], 640)

    async def test_microphone_to_sender_preserves_silence_and_measures_sends(self):
        metrics = LatencyMetrics()
        microphone = MicrophoneStream(MicrophoneConfig(16000, 100, 200), metrics=metrics)
        audio = pcm(0, 1600)
        microphone._on_audio(audio, 1600, None, False)
        microphone._queue.put(None)
        sent = []

        async def send(message):
            sent.append(message)

        await send_audio_chunks(SimpleNamespace(send=send), microphone.chunks(), metrics, "task-1")
        self.assertEqual(sent[0], audio)
        self.assertEqual(len(sent), 2)
        self.assertIsNotNone(metrics.first_audio_chunk_sent)
        self.assertIsNone(metrics.speech_start_timestamp)
        self.assertEqual(metrics.audio_diagnostics()["websocket_send_duration_ms_count"], 1)

    async def test_failed_send_does_not_mark_audio_as_sent(self):
        metrics = LatencyMetrics()

        async def chunks():
            yield pcm(0, 1600)

        async def send(_message):
            raise ConnectionClosedError(None, None)

        with self.assertRaises(ConnectionError):
            await send_audio_chunks(SimpleNamespace(send=send), chunks(), metrics, "")
        self.assertIsNone(metrics.first_audio_chunk_sent)
        self.assertEqual(metrics.audio_diagnostics()["websocket_send_duration_ms_count"], 0)

    async def test_timestamp_mapping_does_not_change_audio_bytes(self):
        metrics = LatencyMetrics(monotonic_now=lambda: 10.15)
        microphone = MicrophoneStream(MicrophoneConfig(16000, 100, 200), metrics=metrics)
        data = pcm(0, 800) + pcm(2000, 800)
        timing = SimpleNamespace(currentTime=500.0, inputBufferAdcTime=499.9)
        with patch("microphone.monotonic", return_value=10.1):
            microphone._on_audio(data, 1600, timing, False)
        chunks = microphone.chunks()
        self.assertEqual(await anext(chunks), data)
        await chunks.aclose()
        self.assertAlmostEqual(metrics.speech_start_timestamp, 10.05)
        self.assertAlmostEqual(metrics.first_audio_capture_timestamp, 10.0)
        self.assertEqual(metrics.audio_diagnostics()["audio_queue_wait_ms_max"], 50.0)
        self.assertEqual(metrics.audio_diagnostics()["audio_chunk_actual_ms_median"], 100.0)

    async def test_missing_adc_timestamp_is_labeled_as_fallback(self):
        metrics = LatencyMetrics(monotonic_now=lambda: 10.2)
        microphone = MicrophoneStream(MicrophoneConfig(16000, 100, 200), metrics=metrics)
        with patch("microphone.monotonic", return_value=10.1):
            microphone._on_audio(pcm(0, 1600), 1600, None, False)
        chunks = microphone.chunks()
        await anext(chunks)
        await chunks.aclose()
        self.assertEqual(metrics.audio_diagnostics()["audio_timestamp_fallback_count"], 1)
        self.assertIsNone(metrics.speech_start_timestamp)


class MeasurementCliTest(unittest.TestCase):
    def test_cli_controls_local_meter_and_device_latency(self):
        args = build_parser().parse_args([
            "--speech-rms-threshold", "0.01", "--input-latency", "low", "--debug-audio-rms"
        ])
        self.assertEqual(args.speech_rms_threshold, 0.01)
        self.assertEqual(args.input_latency, "low")
        self.assertTrue(args.debug_audio_rms)

    def test_summary_exposes_new_measurements(self):
        metrics = LatencyMetrics()
        metrics.mark_recording_start(1.0)
        metrics.mark_first_audio_chunk_sent(1.1)
        metrics.mark_speech_start(1.3)
        output = StringIO()
        with redirect_stdout(output):
            print_summary(metrics, SimpleNamespace(dropped_chunks=0))
        self.assertIn("speech_start_timestamp=1.3", output.getvalue())
        self.assertIn("recording_start_to_speech_start_ms=300.0", output.getvalue())
        self.assertIn("speech_start_to_first_partial_ms=None", output.getvalue())
        self.assertIn("transcript_update_interval_median_ms=None", output.getvalue())
