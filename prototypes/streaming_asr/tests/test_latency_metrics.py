import unittest

from latency_metrics import LatencyMetrics, TranscriptUpdate


class LatencyMetricsTest(unittest.TestCase):
    def test_speech_and_send_relative_latency_keep_first_timestamps(self):
        metrics = LatencyMetrics()
        metrics.mark_recording_start(10.0)
        metrics.mark_first_audio_chunk_sent(10.2)
        metrics.mark_speech_start(10.5)
        metrics.mark_speech_start(11.0)
        metrics.record_transcript_update(TranscriptUpdate("partial", "hello", 11.3))
        metrics.record_transcript_update(TranscriptUpdate("partial", "hello world", 12.0))
        summary = metrics.summary(13.0)
        self.assertEqual(summary.speech_start_timestamp, 10.5)
        self.assertEqual(summary.speech_start_to_first_partial_ms, 800.0)
        self.assertEqual(summary.first_audio_sent_to_first_partial_ms, 1100.0)
        self.assertEqual(summary.recording_start_to_speech_start_ms, 500.0)

    def test_missing_speech_or_partial_is_not_zero_latency(self):
        summary = LatencyMetrics().summary()
        self.assertIsNone(summary.speech_start_to_first_partial_ms)
        self.assertIsNone(summary.first_audio_sent_to_first_partial_ms)
        self.assertIsNone(summary.recording_start_to_speech_start_ms)
        self.assertIsNone(summary.continuous_lag.median_update_gap_ms)

    def test_interval_median_and_max_include_partial_and_stable(self):
        metrics = LatencyMetrics()
        for kind, timestamp in (("partial", 1), ("partial", 1.8), ("stable", 2.6), ("partial", 5.6)):
            metrics.record_transcript_update(TranscriptUpdate(kind, "text", timestamp))
        observation = metrics.continuous_lag_observation()
        self.assertEqual(observation.median_update_gap_ms, 800.0)
        self.assertEqual(observation.max_update_gap_ms, 3000.0)

    def test_audio_diagnostics_separate_queue_send_and_cadence(self):
        metrics = LatencyMetrics()
        metrics.record_audio_capture(1.0, 1.1, 1.12, 100.0, 2, "portaudio_adc")
        metrics.record_audio_send(1.12, 1.125)
        metrics.record_audio_send(1.22, 1.235)
        diagnostics = metrics.audio_diagnostics()
        self.assertEqual(diagnostics["audio_queue_wait_ms_max"], 20.0)
        self.assertEqual(diagnostics["capture_to_callback_ms_median"], 100.0)
        self.assertEqual(diagnostics["websocket_send_duration_ms_median"], 10.0)
        self.assertEqual(diagnostics["audio_send_interval_ms_median"], 100.0)
        self.assertEqual(diagnostics["audio_chunk_actual_ms_median"], 100.0)
        self.assertEqual(diagnostics["audio_queue_depth_max"], 2)
        self.assertEqual(metrics.first_audio_chunk_sent, 1.125)

    def test_computes_first_partial_and_stable_latency_ms(self):
        metrics = LatencyMetrics(monotonic_now=lambda: 10.0)

        metrics.mark_recording_start(10.0)
        metrics.mark_first_audio_chunk_sent(10.2)
        metrics.record_transcript_update(
            TranscriptUpdate(kind="partial", text="你好", timestamp=10.45)
        )
        metrics.record_transcript_update(
            TranscriptUpdate(kind="stable", text="你好世界", timestamp=11.1)
        )

        summary = metrics.summary(ended_at=12.0)

        self.assertEqual(summary.first_transcript_latency_ms, 450.0)
        self.assertEqual(summary.stable_transcript_latency_ms, 1100.0)
        self.assertEqual(summary.session_duration_ms, 2000.0)
        self.assertEqual(summary.transcript_update_count, 2)

    def test_ignores_invalid_stable_text_for_first_stable_latency(self):
        metrics = LatencyMetrics(monotonic_now=lambda: 0.0)

        metrics.mark_recording_start(10.0)
        metrics.record_transcript_update(
            TranscriptUpdate(kind="stable", text="**。", timestamp=10.5)
        )
        metrics.record_transcript_update(
            TranscriptUpdate(kind="stable", text="你好世界", timestamp=11.2)
        )

        summary = metrics.summary(ended_at=12.0)

        self.assertEqual(summary.first_stable_transcript, 11.2)
        self.assertEqual(summary.stable_transcript_latency_ms, 1200.0)

    def test_continuous_lag_reports_observed_update_gaps(self):
        metrics = LatencyMetrics(monotonic_now=lambda: 0.0)
        metrics.mark_recording_start(1.0)
        metrics.record_transcript_update(
            TranscriptUpdate(kind="partial", text="a", timestamp=1.2)
        )
        metrics.record_transcript_update(
            TranscriptUpdate(kind="partial", text="ab", timestamp=1.5)
        )
        metrics.record_transcript_update(
            TranscriptUpdate(kind="stable", text="abc", timestamp=2.1)
        )

        observation = metrics.continuous_lag_observation()

        self.assertEqual(observation.update_count, 3)
        self.assertEqual(observation.max_update_gap_ms, 600.0)
        self.assertEqual(observation.average_update_gap_ms, 450.0)


if __name__ == "__main__":
    unittest.main()

