import unittest

from config import load_config


class ConfigTest(unittest.TestCase):
    def test_defaults_to_dashscope_fun_asr_and_loads_dashscope_key(self):
        config = load_config({"DASHSCOPE_API_KEY": "dashscope-key"})

        self.assertEqual(config.provider, "dashscope_fun_asr_realtime")
        self.assertEqual(config.api_key, "dashscope-key")
        self.assertEqual(config.model, "fun-asr-realtime")
        self.assertEqual(config.sample_rate, 16000)
        self.assertEqual(config.languages, ("zh",))

    def test_loads_languages_from_environment(self):
        config = load_config(
            {
                "DASHSCOPE_API_KEY": "dashscope-key",
                "ASR_LANGUAGES": "zh-cn,en",
            }
        )

        self.assertEqual(config.languages, ("zh",))

    def test_dashscope_uses_only_first_supported_language_hint(self):
        config = load_config(
            {"DASHSCOPE_API_KEY": "dashscope-key", "ASR_LANGUAGES": "en,zh"}
        )
        self.assertEqual(config.languages, ("en",))

    def test_falls_back_to_legacy_single_language_variable(self):
        config = load_config(
            {
                "ASR_PROVIDER": "openai",
                "OPENAI_API_KEY": "openai-key",
                "ASR_LANGUAGE": "zh-cn",
            }
        )

        self.assertEqual(config.provider, "openai")
        self.assertEqual(config.api_key, "openai-key")
        self.assertEqual(config.languages, ("zh-cn",))

    def test_loads_debug_raw_events_flag(self):
        config = load_config(
            {
                "DASHSCOPE_API_KEY": "dashscope-key",
                "ASR_DEBUG_RAW_EVENTS": "1",
            }
        )

        self.assertTrue(config.debug_raw_events)

    def test_rejects_missing_dashscope_api_key_by_default(self):
        with self.assertRaisesRegex(ValueError, "DASHSCOPE_API_KEY"):
            load_config({})

    def test_rejects_unsupported_provider(self):
        with self.assertRaisesRegex(ValueError, "Unsupported ASR_PROVIDER"):
            load_config({"ASR_PROVIDER": "qwen3_asr_flash", "ASR_API_KEY": "key"})

    def test_loads_paraformer_v2_with_default_ab_audio_settings(self):
        config = load_config({
            "ASR_PROVIDER": "dashscope_paraformer_realtime",
            "DASHSCOPE_API_KEY": "dashscope-key",
            "ASR_SAMPLE_RATE": "8000",
            "ASR_AUDIO_FORMAT": "wav",
        })

        self.assertEqual(config.model, "paraformer-realtime-v2")
        self.assertEqual(config.sample_rate, 16000)
        self.assertEqual(config.chunk_ms, 100)
        self.assertEqual(config.audio_format, "pcm")
        self.assertEqual(config.languages, ("zh",))

    def test_paraformer_allows_50_ms_audio_chunks(self):
        config = load_config({
            "ASR_PROVIDER": "dashscope_paraformer_realtime",
            "DASHSCOPE_API_KEY": "dashscope-key",
            "ASR_CHUNK_MS": "50",
        })

        self.assertEqual(config.chunk_ms, 50)

    def test_paraformer_rejects_chunk_sizes_outside_ab_values(self):
        for chunk_ms in ("40", "80", "150"):
            with self.subTest(chunk_ms=chunk_ms):
                with self.assertRaisesRegex(
                    ValueError, "ASR_CHUNK_MS must be one of: 50, 100"
                ):
                    load_config({
                        "ASR_PROVIDER": "dashscope_paraformer_realtime",
                        "DASHSCOPE_API_KEY": "dashscope-key",
                        "ASR_CHUNK_MS": chunk_ms,
                    })


if __name__ == "__main__":
    unittest.main()


