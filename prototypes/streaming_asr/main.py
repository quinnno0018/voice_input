from __future__ import annotations

import argparse
import asyncio
import json
from time import monotonic

from config import load_config
from dashscope_fun_asr_realtime import (
    DashScopeFunAsrError,
    build_run_task_message,
    connect_dashscope,
    receive_transcripts as receive_dashscope_transcripts,
    send_audio_chunks as send_dashscope_audio_chunks,
    wait_for_task_started,
)
from dashscope_paraformer_realtime import (
    DashScopeParaformerError,
    build_run_task_message as build_paraformer_run_task_message,
    connect_dashscope_paraformer,
    receive_transcripts as receive_paraformer_transcripts,
    send_audio_chunks as send_paraformer_audio_chunks,
    wait_for_task_started as wait_for_paraformer_task_started,
)
from latency_metrics import LatencyMetrics
from microphone import MicrophoneConfig, MicrophoneStream
from openai_realtime import (
    RealtimeAsrError,
    build_session_update,
    connect_realtime,
    receive_transcripts as receive_openai_transcripts,
    send_audio_chunks as send_openai_audio_chunks,
)
from provider_events import ConnectionState, PartialTranscript, StableTranscript


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Streaming ASR latency prototype. Press Ctrl+C to stop safely."
    )
    parser.add_argument(
        "--duration",
        type=float,
        default=120.0,
        help="Maximum recording duration in seconds. Default: 120.",
    )
    parser.add_argument(
        "--device",
        default=None,
        help="Optional sounddevice input device id or name.",
    )
    parser.add_argument(
        "--speech-rms-threshold", type=float, default=0.005,
        help="Local measurement-only normalized PCM16 RMS threshold. Default: 0.005.",
    )
    parser.add_argument(
        "--debug-audio-rms", action="store_true",
        help="Print one summary of below/above-threshold normalized PCM16 RMS ranges.",
    )
    parser.add_argument(
        "--input-latency", choices=("low", "high"), default=None,
        help="Optional PortAudio latency hint. Default: sounddevice device default.",
    )
    return parser


async def run_once(
    duration: float, device: int | str | None = None,
    speech_rms_threshold: float = 0.005, input_latency: str | None = None,
    debug_audio_rms: bool = False,
) -> int:
    config = load_config()
    metrics = LatencyMetrics()
    microphone = MicrophoneStream(
        MicrophoneConfig(
            sample_rate=config.sample_rate,
            chunk_ms=config.chunk_ms,
            max_queue_chunks=config.max_queue_chunks,
            device=device,
            speech_rms_threshold=speech_rms_threshold,
            input_latency=input_latency,
            debug_audio_rms=debug_audio_rms,
        ),
        metrics=metrics,
    )

    print(f"[session] provider={config.provider}", flush=True)
    print(
        f"[audio] sample_rate={config.sample_rate} channels=1 dtype=int16 "
        f"frames_per_chunk={microphone.blocksize} bytes_per_chunk={microphone.blocksize * 2} "
        f"chunk_actual_ms={microphone.blocksize * 1000 / config.sample_rate:.3f} "
        f"queue_capacity_ms={config.max_queue_chunks * microphone.blocksize * 1000 / config.sample_rate:.3f} "
        f"speech_rms_threshold={speech_rms_threshold} speech_confirm_ms=30 "
        f"input_latency_hint={input_latency or 'device_default'}",
        flush=True,
    )
    if config.provider == "dashscope_fun_asr_realtime":
        return await _run_dashscope(config, metrics, microphone, duration)
    if config.provider == "dashscope_paraformer_realtime":
        return await _run_paraformer(config, metrics, microphone, duration)
    if config.provider == "openai":
        return await _run_openai(config, metrics, microphone, duration)

    raise ValueError(f"Unsupported ASR_PROVIDER '{config.provider}'.")


async def _run_dashscope(config, metrics: LatencyMetrics, microphone: MicrophoneStream, duration: float) -> int:
    print("[session] connecting DashScope Fun-ASR realtime", flush=True)
    try:
        websocket = await connect_dashscope(config)
        try:
            await websocket.send(build_run_task_message(config))
            task_id = await wait_for_task_started(websocket)
            metrics.mark_recording_start()
            print("[session] recording started", flush=True)

            async with microphone.open():
                sender = asyncio.create_task(
                    send_dashscope_audio_chunks(websocket, microphone.chunks(), metrics, task_id)
                )
                receiver = asyncio.create_task(_print_transcripts(websocket, metrics, "dashscope", config.debug_raw_events))
                await _wait_for_session(sender, receiver, duration)
        finally:
            await websocket.close()

    except KeyboardInterrupt:
        print("\n[session] interrupted by user", flush=True)
    except OSError as exc:
        print(f"\n[network-error] {exc}", flush=True)
        return 2
    except DashScopeFunAsrError as exc:
        print(f"\n[asr-error] {exc}", flush=True)
        return 2
    except Exception as exc:
        print(f"\n[startup-error] {exc}", flush=True)
        return 2
    finally:
        print_summary(metrics, microphone)

    return 0


async def _run_paraformer(
    config, metrics: LatencyMetrics, microphone: MicrophoneStream, duration: float
) -> int:
    print("[session] connecting DashScope Paraformer realtime v2", flush=True)
    try:
        websocket = await connect_dashscope_paraformer(config)
        try:
            await websocket.send(build_paraformer_run_task_message(config))
            task_id = await wait_for_paraformer_task_started(websocket)
            metrics.mark_recording_start()
            print("[session] recording started", flush=True)

            async with microphone.open():
                sender = asyncio.create_task(
                    send_paraformer_audio_chunks(
                        websocket, microphone.chunks(), metrics, task_id
                    )
                )
                receiver = asyncio.create_task(
                    _print_transcripts(
                        websocket, metrics, "paraformer", config.debug_raw_events
                    )
                )
                await _wait_for_session(sender, receiver, duration)
        finally:
            await websocket.close()

    except KeyboardInterrupt:
        print("\n[session] interrupted by user", flush=True)
    except OSError as exc:
        print(f"\n[network-error] {exc}", flush=True)
        return 2
    except DashScopeParaformerError as exc:
        print(f"\n[asr-error] {exc}", flush=True)
        return 2
    except Exception as exc:
        print(f"\n[startup-error] {exc}", flush=True)
        return 2
    finally:
        print_summary(metrics, microphone)

    return 0


async def _run_openai(config, metrics: LatencyMetrics, microphone: MicrophoneStream, duration: float) -> int:
    print("[session] connecting OpenAI realtime ASR", flush=True)
    try:
        websocket = await connect_realtime(config)
        try:
            await websocket.send(json.dumps(build_session_update(config)))
            metrics.mark_recording_start()
            print("[session] recording started", flush=True)

            async with microphone.open():
                sender = asyncio.create_task(
                    send_openai_audio_chunks(websocket, microphone.chunks(), metrics)
                )
                receiver = asyncio.create_task(_print_transcripts(websocket, metrics, "openai"))
                await _wait_for_session(sender, receiver, duration)
        finally:
            await websocket.close()

    except KeyboardInterrupt:
        print("\n[session] interrupted by user", flush=True)
    except OSError as exc:
        print(f"\n[network-error] {exc}", flush=True)
        return 2
    except RealtimeAsrError as exc:
        print(f"\n[asr-error] {exc}", flush=True)
        return 2
    except Exception as exc:
        print(f"\n[startup-error] {exc}", flush=True)
        return 2
    finally:
        print_summary(metrics, microphone)

    return 0


async def _wait_for_session(sender: asyncio.Task, receiver: asyncio.Task, duration: float) -> None:
    tasks = {sender, receiver}
    try:
        done, _pending = await asyncio.wait(
            tasks,
            timeout=duration,
            return_when=asyncio.FIRST_EXCEPTION,
        )
        if not done:
            print("\n[session] duration reached; stopping", flush=True)
            return
        for task in done:
            exception = task.exception()
            if exception is not None:
                raise exception
    finally:
        sender.cancel()
        receiver.cancel()
        await asyncio.gather(sender, receiver, return_exceptions=True)


async def _print_transcripts(websocket, metrics: LatencyMetrics, provider: str, debug_raw_events: bool = False) -> None:
    if provider == "dashscope":
        stream = receive_dashscope_transcripts(websocket, metrics, debug_raw_events=debug_raw_events)
    elif provider == "paraformer":
        stream = receive_paraformer_transcripts(
            websocket, metrics, debug_raw_events=debug_raw_events
        )
    else:
        stream = receive_openai_transcripts(websocket, metrics)

    async for event in stream:
        if isinstance(event, PartialTranscript) and event.text:
            print(f"\r[partial] {event.text}", end="", flush=True)
        elif isinstance(event, StableTranscript):
            print(f"\n[stable] {event.text}", flush=True)
        elif isinstance(event, ConnectionState):
            if event.state not in {"heartbeat", "ignored_invalid_transcript"}:
                print(f"\n[event] {event.state}", flush=True)
        elif getattr(event, "kind", None) == "partial" and getattr(event, "text", ""):
            print(f"\r[partial] {event.text}", end="", flush=True)
        elif getattr(event, "kind", None) == "stable":
            print(f"\n[stable] {event.text}", flush=True)
        elif getattr(event, "kind", None) == "other":
            print(f"\n[event] {event.raw_type}", flush=True)


def print_summary(metrics: LatencyMetrics, microphone: MicrophoneStream) -> None:
    summary = metrics.summary(ended_at=monotonic())
    continuous_lag = summary.continuous_lag
    print("\n[metrics]", flush=True)
    print(f"recording_start={summary.recording_start}", flush=True)
    print(f"first_audio_chunk_sent={summary.first_audio_chunk_sent}", flush=True)
    print(f"first_partial_transcript={summary.first_partial_transcript}", flush=True)
    print(f"first_stable_transcript={summary.first_stable_transcript}", flush=True)
    print(f"speech_start_timestamp={summary.speech_start_timestamp}", flush=True)
    print(f"speech_start_to_first_partial_ms={summary.speech_start_to_first_partial_ms}", flush=True)
    print(f"first_audio_sent_to_first_partial_ms={summary.first_audio_sent_to_first_partial_ms}", flush=True)
    print(f"recording_start_to_speech_start_ms={summary.recording_start_to_speech_start_ms}", flush=True)
    print(f"session_duration_ms={summary.session_duration_ms}", flush=True)
    print(
        f"first_transcript_latency_ms={summary.first_transcript_latency_ms}",
        flush=True,
    )
    print(f"stable_transcript_latency_ms={summary.stable_transcript_latency_ms}", flush=True)
    print(f"transcript_update_count={summary.transcript_update_count}", flush=True)
    print(f"continuous_lag_update_count={continuous_lag.update_count}", flush=True)
    print(f"continuous_lag_max_gap_ms={continuous_lag.max_update_gap_ms}", flush=True)
    print(f"transcript_update_interval_median_ms={continuous_lag.median_update_gap_ms}", flush=True)
    print(f"transcript_update_interval_max_ms={continuous_lag.max_update_gap_ms}", flush=True)
    print(
        f"continuous_lag_average_gap_ms={continuous_lag.average_update_gap_ms}",
        flush=True,
    )
    print(f"dropped_audio_chunks={microphone.dropped_chunks}", flush=True)
    print(f"input_device_latency_ms={getattr(microphone, 'input_latency_ms', None)}", flush=True)
    for name, value in metrics.audio_diagnostics().items():
        print(f"{name}={value}", flush=True)
    if getattr(getattr(microphone, "config", None), "debug_audio_rms", False):
        debug = microphone.rms_debug_summary()
        print(
            "[debug][audio-rms] "
            + " ".join(f"{name}={value}" for name, value in debug.items()),
            flush=True,
        )


def main() -> int:
    parser = build_parser()
    args = parser.parse_args()
    device: int | str | None = args.device
    if isinstance(device, str) and device.isdigit():
        device = int(device)

    try:
        return asyncio.run(run_once(
            duration=args.duration, device=device,
            speech_rms_threshold=args.speech_rms_threshold,
            input_latency=args.input_latency,
            debug_audio_rms=args.debug_audio_rms,
        ))
    except ValueError as exc:
        print(f"[config-error] {exc}", flush=True)
        return 2
    except KeyboardInterrupt:
        print("\n[session] interrupted before startup", flush=True)
        return 130


if __name__ == "__main__":
    raise SystemExit(main())


