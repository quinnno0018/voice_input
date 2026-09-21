from __future__ import annotations

from dataclasses import dataclass
from os import environ
from typing import Mapping


OPENAI_WS_URL = "wss://api.openai.com/v1/realtime?intent=transcription"
DASHSCOPE_FUN_ASR_WS_URL = "wss://dashscope.aliyuncs.com/api-ws/v1/inference"
DASHSCOPE_PARAFORMER_WS_URL = "wss://dashscope.aliyuncs.com/api-ws/v1/inference"
SUPPORTED_PROVIDERS = {
    "openai",
    "dashscope_fun_asr_realtime",
    "dashscope_paraformer_realtime",
}


@dataclass(frozen=True)
class AsrConfig:
    provider: str
    api_key: str
    ws_url: str
    model: str
    languages: tuple[str, ...] = ("zh",)
    sample_rate: int = 16000
    chunk_ms: int = 100
    max_queue_chunks: int = 200
    vad_threshold: float = 0.5
    silence_duration_ms: int = 600
    prefix_padding_ms: int = 300
    transcription_delay: str = "low"
    audio_format: str = "pcm"
    debug_raw_events: bool = False


_ENV = Mapping[str, str]


def _get_int(env: _ENV, name: str, default: int) -> int:
    raw_value = env.get(name)
    if raw_value is None or raw_value == "":
        return default
    return int(raw_value)


def _get_float(env: _ENV, name: str, default: float) -> float:
    raw_value = env.get(name)
    if raw_value is None or raw_value == "":
        return default
    return float(raw_value)


def _get_bool(env: _ENV, name: str, default: bool = False) -> bool:
    raw_value = env.get(name)
    if raw_value is None or raw_value == "":
        return default
    return raw_value.strip().lower() in {"1", "true", "yes", "on"}


def _get_languages(env: _ENV, default: str) -> tuple[str, ...]:
    raw_value = env.get("ASR_LANGUAGES") or env.get("ASR_LANGUAGE") or default
    languages = tuple(part.strip() for part in raw_value.split(",") if part.strip())
    if not languages:
        raise ValueError("ASR_LANGUAGES must contain at least one language code.")
    return languages


def _get_dashscope_languages(env: _ENV) -> tuple[str, ...]:
    first = _get_languages(env, "zh")[0].lower()
    aliases = {"zh-cn": "zh", "zh_cn": "zh", "en-us": "en", "en_us": "en"}
    return (aliases.get(first, first),)


def _get_provider(env: _ENV) -> str:
    provider = env.get("ASR_PROVIDER") or "dashscope_fun_asr_realtime"
    if provider not in SUPPORTED_PROVIDERS:
        supported = ", ".join(sorted(SUPPORTED_PROVIDERS))
        raise ValueError(f"Unsupported ASR_PROVIDER '{provider}'. Supported providers: {supported}.")
    return provider


def _get_api_key(env: _ENV, provider: str) -> str:
    if provider in {"dashscope_fun_asr_realtime", "dashscope_paraformer_realtime"}:
        api_key = env.get("DASHSCOPE_API_KEY") or env.get("ASR_API_KEY")
        if not api_key:
            raise ValueError(
                "Missing DASHSCOPE_API_KEY or ASR_API_KEY. Set one environment variable before running the DashScope realtime prototype."
            )
        return api_key

    api_key = env.get("ASR_API_KEY") or env.get("OPENAI_API_KEY")
    if not api_key:
        raise ValueError(
            "Missing ASR_API_KEY or OPENAI_API_KEY. Set one environment variable before running the OpenAI realtime prototype."
        )
    return api_key


def load_config(env: Mapping[str, str] | None = None) -> AsrConfig:
    source = environ if env is None else env
    provider = _get_provider(source)
    api_key = _get_api_key(source, provider)

    if provider == "dashscope_fun_asr_realtime":
        return AsrConfig(
            provider=provider,
            api_key=api_key,
            ws_url=source.get("DASHSCOPE_FUN_ASR_WS_URL")
            or source.get("ASR_WS_URL")
            or DASHSCOPE_FUN_ASR_WS_URL,
            model=source.get("DASHSCOPE_FUN_ASR_MODEL")
            or source.get("ASR_MODEL")
            or "fun-asr-realtime",
            languages=_get_dashscope_languages(source),
            sample_rate=_get_int(source, "ASR_SAMPLE_RATE", 16000),
            chunk_ms=_get_int(source, "ASR_CHUNK_MS", 100),
            max_queue_chunks=_get_int(source, "ASR_MAX_QUEUE_CHUNKS", 200),
            audio_format=source.get("ASR_AUDIO_FORMAT") or "pcm",
            debug_raw_events=_get_bool(source, "ASR_DEBUG_RAW_EVENTS"),
        )

    if provider == "dashscope_paraformer_realtime":
        chunk_ms = _get_int(source, "ASR_CHUNK_MS", 100)
        if chunk_ms not in {50, 100}:
            raise ValueError("ASR_CHUNK_MS must be one of: 50, 100.")
        return AsrConfig(
            provider=provider,
            api_key=api_key,
            ws_url=source.get("DASHSCOPE_PARAFORMER_WS_URL")
            or source.get("ASR_WS_URL")
            or DASHSCOPE_PARAFORMER_WS_URL,
            model=source.get("DASHSCOPE_PARAFORMER_MODEL")
            or source.get("ASR_MODEL")
            or "paraformer-realtime-v2",
            languages=_get_languages(source, "zh"),
            sample_rate=16000,
            chunk_ms=chunk_ms,
            max_queue_chunks=_get_int(source, "ASR_MAX_QUEUE_CHUNKS", 200),
            audio_format="pcm",
            debug_raw_events=_get_bool(source, "ASR_DEBUG_RAW_EVENTS"),
        )

    return AsrConfig(
        provider=provider,
        api_key=api_key,
        ws_url=source.get("OPENAI_REALTIME_WS_URL") or source.get("ASR_WS_URL") or OPENAI_WS_URL,
        model=source.get("OPENAI_REALTIME_MODEL") or source.get("ASR_MODEL") or "gpt-live-transcribe",
        languages=_get_languages(source, "zh-cn,en"),
        sample_rate=_get_int(source, "ASR_SAMPLE_RATE", 24000),
        chunk_ms=_get_int(source, "ASR_CHUNK_MS", 100),
        max_queue_chunks=_get_int(source, "ASR_MAX_QUEUE_CHUNKS", 200),
        vad_threshold=_get_float(source, "ASR_VAD_THRESHOLD", 0.5),
        silence_duration_ms=_get_int(source, "ASR_SILENCE_DURATION_MS", 600),
        prefix_padding_ms=_get_int(source, "ASR_PREFIX_PADDING_MS", 300),
        transcription_delay=source.get("ASR_TRANSCRIPTION_DELAY") or "low",
        audio_format=source.get("ASR_AUDIO_FORMAT") or "pcm",
        debug_raw_events=_get_bool(source, "ASR_DEBUG_RAW_EVENTS"),
    )
