"""Diagnostic-only Python/C# transport comparison; no microphone or text injection."""

import argparse
import asyncio
import copy
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import statistics
import socket as socket_api
import ssl
import subprocess
import sys
import time
from urllib.parse import urlsplit
import uuid

import websockets


ROOT = Path(__file__).resolve().parents[2]
DEFAULT_ASSEMBLY = ROOT / "src/desktop/VoiceInput.Desktop.Tests/bin/send-probe-v2/Release/net10.0-windows/VoiceInput.Desktop.Tests.dll"


def load_plan(path):
    path = Path(path)
    plan = json.loads(path.read_text(encoding="utf-8"))
    audio = (path.parent / "fixture.pcm").read_bytes()
    endpoint = urlsplit(plan["endpoint"])
    official = (endpoint.scheme == "wss" and endpoint.hostname == "dashscope.aliyuncs.com"
                and endpoint.port in (None, 443) and endpoint.path == "/api-ws/v1/inference")
    loopback = endpoint.scheme == "ws" and endpoint.hostname == "127.0.0.1"
    if (not (official or loopback) or endpoint.username or endpoint.password
            or endpoint.query or endpoint.fragment):
        raise ValueError("Unsupported probe endpoint")
    if (plan["chunk_bytes"] != 3200 or plan["chunk_ms"] != 100 or len(audio) != 320000
            or any(audio) or hashlib.sha256(audio).hexdigest() != plan["fixture_sha256"]):
        raise ValueError("Invalid fixed PCM fixture")
    request = plan["run_task"]
    parameters = request["payload"]["parameters"]
    if (request["payload"]["model"] != "paraformer-realtime-v2" or parameters["format"] != "pcm"
            or parameters["sample_rate"] != 16000 or parameters["language_hints"] != ["zh", "en"]
            or parameters["max_sentence_silence"] not in (800, 1200, 1600)
            or parameters["semantic_punctuation_enabled"] is not False
            or parameters["disfluency_removal_enabled"] is not False or parameters["heartbeat"] is not True):
        raise ValueError("Invalid baseline parameters")
    return plan, audio


def summarize(values):
    return {"median_ms": statistics.median(values) if values else None,
            "max_ms": max(values) if values else None,
            "over_100ms": sum(value >= 100 for value in values)}


def note_event(result, kind, origin):
    events = result["events"]
    events["counts"][kind] = events["counts"].get(kind, 0) + 1
    events["last_kind"] = kind
    events["last_at_ms"] = (time.perf_counter() - origin) * 1000


async def receive_until_finished(socket, result, origin):
    try:
        async for raw in socket:
            payload = json.loads(raw)
            event = payload.get("header", {}).get("event")
            kind = {"task-failed": "asr_error", "task-finished": "task_finished"}.get(event, "other")
            if event == "result-generated":
                body = payload.get("payload") or {}
                sentence = (body.get("output") or {}).get("sentence") or {}
                text = sentence.get("text")
                has_content = isinstance(text, str) and any(char.isalpha() or char.isdecimal() for char in text)
                kind = ("heartbeat" if sentence.get("heartbeat") is True else
                        "transcript_result" if has_content else "invalid_transcript")
            note_event(result, kind, origin)
            if event in ("task-failed", "task-finished"):
                result["receiver_status"] = kind
                return "asr_error" if event == "task-failed" else "completed"
        result["receiver_status"] = "connection_ended"
        return "connection_ended"
    except asyncio.CancelledError:
        result["receiver_status"] = "cancelled_by_probe"
        raise
    except Exception as error:
        result["receiver_status"] = "failed"
        result["receiver_error_type"] = type(error).__name__
        raise


def buffer_snapshot(transport):
    def size(value):
        try:
            return value.get_write_buffer_size()
        except (AttributeError, OSError, RuntimeError):
            return None
    # Read-only, optional CPython 3.12 diagnostic: the TLS buffer excludes its TCP transport.
    # No monkey patch, changed limit, copied payload, or dependency on this field for sending.
    underlying = None
    if sys.version_info[:2] == (3, 12):
        underlying = getattr(getattr(transport, "_ssl_protocol", None), "_transport", None)
    return {"transport_pending_bytes": size(transport),
            "tls_underlying_pending_bytes": size(underlying)}


def connection_snapshot(socket):
    transport = socket.transport
    tcp = transport.get_extra_info("socket")
    tls = transport.get_extra_info("ssl_object")
    low, high = transport.get_write_buffer_limits()
    return {"proxy_policy": "direct_library_default", "compression": bool(socket.extensions),
            "write_buffer_low": low, "write_buffer_high": high,
            "socket_send_buffer_bytes": tcp.getsockopt(socket_api.SOL_SOCKET, socket_api.SO_SNDBUF) if tcp else None,
            "tcp_nodelay": tcp.getsockopt(socket_api.IPPROTO_TCP, socket_api.TCP_NODELAY) if tcp else None,
            "address_family": int(tcp.family) if tcp else None,
            "tls_version": tls.version() if tls else None, "tls_backend": ssl.OPENSSL_VERSION,
            "ping_interval_seconds": socket.ping_interval, "ping_timeout_seconds": socket.ping_timeout,
            "kernel_pending_bytes": None}


async def wait_for_finish(receiver, result, first_window=5.0, deadline=20.0):
    start = time.perf_counter()
    try:
        try:
            return await asyncio.wait_for(asyncio.shield(receiver), first_window)
        except TimeoutError:
            if receiver.done():
                return await receiver
            result["finish_wait_exceeded_5s"] = True
            return await asyncio.wait_for(asyncio.shield(receiver), max(0, deadline - (time.perf_counter() - start)))
    finally:
        result["finish_ack_wait_ms"] = (time.perf_counter() - start) * 1000


async def run_python(plan_path, key):
    plan, audio = load_plan(plan_path)
    if not key or (urlsplit(plan["endpoint"]).hostname == "127.0.0.1" and key != "loopback"):
        raise ValueError("A key is required; loopback requires the test-only key")
    origin = time.perf_counter()
    result = {"diagnostics_version": 2, "client": "python", "runtime": sys.version.split()[0],
              "websockets": websockets.__version__, "started_utc": datetime.now(timezone.utc).isoformat(),
              "fixture_sha256": plan["fixture_sha256"], "outcome": "not_started",
              "send_phase_complete": False, "connect_ms": None, "sent_chunks": 0,
              "total_audio_bytes": 0, "send_phase_ms": 0, "send_ms": [], "schedule_lag_ms": [],
              "phase": "connect", "timeline_ms": {}, "finish_sent": False, "finish_send_ms": None,
              "finish_ack_wait_ms": None, "finish_wait_exceeded_5s": False,
              "events": {"counts": {}, "last_kind": None, "last_at_ms": None},
              "receiver_status": "not_started", "connection": {}, "buffer_samples": [],
              "buffer_samples_dropped": 0, "close_code": None}
    request = copy.deepcopy(plan["run_task"])
    task_id = uuid.uuid4().hex
    request["header"]["task_id"] = task_id
    receiver = None
    phase = None
    socket = None
    sampler = None

    def sample_buffer():
        if len(result["buffer_samples"]) < 512:
            result["buffer_samples"].append({"at_ms": (time.perf_counter() - origin) * 1000,
                "phase": result["phase"], "sent_chunks": result["sent_chunks"], **buffer_snapshot(socket.transport)})
        else:
            result["buffer_samples_dropped"] += 1

    async def monitor_buffer():
        while True:
            sample_buffer()
            await asyncio.sleep(0.25)

    try:
        async with asyncio.timeout(85):
            connect_started = time.perf_counter()
            async with asyncio.timeout(12):
                # C# ClientWebSocket doesn't request compression. Match it for the silence fixture.
                socket = await websockets.connect(
                    plan["endpoint"], extra_headers={"Authorization": f"Bearer {key}"},
                    compression=None, ping_interval=20, ping_timeout=20,
                    max_size=16 * 1024 * 1024, close_timeout=1)
                await socket.send(json.dumps(request))
                while True:
                    event = json.loads(await socket.recv()).get("header", {}).get("event")
                    if event == "task-started":
                        note_event(result, "task_started", origin)
                        break
                    if event == "task-failed":
                        note_event(result, "asr_error", origin)
                        raise RuntimeError("provider rejected task")
            result["connect_ms"] = (time.perf_counter() - connect_started) * 1000
            result["timeline_ms"]["provider_ready"] = (time.perf_counter() - origin) * 1000
            try:
                result["connection"] = connection_snapshot(socket)
            except (AttributeError, OSError, RuntimeError) as error:
                result["connection"] = {"unavailable": type(error).__name__}
            result["receiver_status"] = "running"
            receiver = asyncio.create_task(receive_until_finished(socket, result, origin))
            sampler = asyncio.create_task(monitor_buffer())
            phase = time.perf_counter()
            result["phase"] = "audio_send"
            async with asyncio.timeout(45):
                for index in range(100):
                    due = (index + 1) * 0.1
                    await asyncio.sleep(max(0, phase + due - time.perf_counter()))
                    if receiver.done():
                        raise RuntimeError("provider ended before all audio was sent")
                    result["schedule_lag_ms"].append(max(0, (time.perf_counter() - phase - due) * 1000))
                    start = time.perf_counter()
                    await socket.send(audio[index * 3200:(index + 1) * 3200])
                    result["send_ms"].append((time.perf_counter() - start) * 1000)
                    result["sent_chunks"] += 1
                    result["total_audio_bytes"] += 3200
                    sample_buffer()
            result["send_phase_ms"] = (time.perf_counter() - phase) * 1000
            result["send_phase_complete"] = True
            result["timeline_ms"]["last_audio_send_completed"] = (time.perf_counter() - origin) * 1000
            result["phase"] = "finish_send"
            start = time.perf_counter()
            try:
                async with asyncio.timeout(5):
                    await socket.send(json.dumps({
                        "header": {"action": "finish-task", "task_id": task_id, "streaming": "duplex"},
                        "payload": {"input": {}},
                    }))
                result["finish_sent"] = True
                result["timeline_ms"]["finish_sent"] = (time.perf_counter() - origin) * 1000
            finally:
                result["finish_send_ms"] = (time.perf_counter() - start) * 1000
            result["phase"] = "finish_ack"
            result["outcome"] = await wait_for_finish(receiver, result)
            if result["outcome"] == "completed":
                result["phase"] = "completed"
    except Exception as error:
        result["outcome"] = type(error).__name__
    finally:
        result["timeline_ms"]["observation_end"] = (time.perf_counter() - origin) * 1000
        if socket is not None:
            sample_buffer()
            result["close_code"] = socket.close_code
        if phase is not None and not result["send_phase_complete"]:
            result["send_phase_ms"] = (time.perf_counter() - phase) * 1000
        if receiver is not None:
            receiver.cancel()
            await asyncio.gather(receiver, return_exceptions=True)
        if sampler is not None:
            sampler.cancel()
            await asyncio.gather(sampler, return_exceptions=True)
        if socket is not None:
            try:
                await asyncio.wait_for(socket.close(), 3)
            except Exception:
                pass
    return result


def write_new(path, value):
    with Path(path).open("x", encoding="utf-8") as file:
        json.dump(value, file, indent=2)


def run_csharp(dotnet, assembly, *arguments):
    # Keep raw provider errors and inherited environment values out of terminal output.
    return subprocess.run([str(dotnet), str(assembly), "--send-probe", *map(str, arguments)],
                          capture_output=True, timeout=95).returncode


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dotnet", required=True, help="Path to an existing dotnet executable")
    parser.add_argument("--assembly", type=Path, default=DEFAULT_ASSEMBLY)
    args = parser.parse_args()
    key = (os.environ.get("DASHSCOPE_API_KEY") or os.environ.get("ASR_API_KEY") or "").strip()
    if not key:
        print("DASHSCOPE_API_KEY or ASR_API_KEY is missing. Set it in this PowerShell; do not share it.")
        return 2
    if websockets.__version__ != "12.0":
        print("Use the prototype virtual environment with the pinned websockets==12.0.")
        return 2
    if not args.assembly.is_file():
        print("Build the C# diagnostic test assembly first.")
        return 2
    path = ROOT / "artifacts/asr-send-ab" / (datetime.now().strftime("%Y%m%d-%H%M%S-") + uuid.uuid4().hex[:8])
    path.mkdir(parents=True)
    if run_csharp(args.dotnet, args.assembly, "prepare", path) != 0:
        print("Cannot prepare comparison. Check the current Paraformer model/endpoint configuration.")
        return 1
    plan_path = path / "plan.json"
    load_plan(plan_path)
    print("Transport only: generated silence; no microphone, no speech-accuracy result.")
    print("Same PCM and run-task; compression disabled on both clients. 4 sequential runs, 10s audio each.")
    print("Diagnostics v2: finish confirmation checked at 5s, observed up to 20s. No buffer settings changed.")
    print(f"Results: {path}", flush=True)
    results = []
    for index, client in enumerate(("python", "csharp", "csharp", "python"), 1):
        print(f"[{index}/4] {client}...", flush=True)
        output = path / f"{index}-{client}.json"
        if client == "python":
            result = asyncio.run(run_python(plan_path, key))
            write_new(output, result)
        else:
            run_csharp(args.dotnet, args.assembly, "run", plan_path, output)
            if not output.exists():
                print("C# probe could not start; no successful run recorded.")
                return 1
            result = json.loads(output.read_text(encoding="utf-8"))
        results.append(result)
        summary = summarize(result["send_ms"])
        print(f"  {result['sent_chunks']}/100 chunks; send ms: {summary}; outcome={result['outcome']}; phase={result['phase']}", flush=True)
    write_new(path / "summary.json", [
        {"diagnostics_version": result["diagnostics_version"], "client": result["client"], "outcome": result["outcome"],
         "phase": result["phase"], "finish_sent": result["finish_sent"],
         "finish_send_ms": result["finish_send_ms"], "finish_ack_wait_ms": result["finish_ack_wait_ms"],
         "finish_wait_exceeded_5s": result["finish_wait_exceeded_5s"], "events": result["events"],
         "send_phase_complete": result["send_phase_complete"], "sent_chunks": result["sent_chunks"],
         "connect_ms": result["connect_ms"], "send_phase_ms": result["send_phase_ms"],
         "send": summarize(result["send_ms"]), "schedule_lag": summarize(result["schedule_lag_ms"])}
        for result in results])
    print("Send completion is a local socket observation, not proof of server receipt.")
    print("Silence may produce a final ASR error; inspect send_phase_complete separately from outcome.")
    return 0 if all(result["outcome"] == "completed" for result in results) else 1


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except KeyboardInterrupt:
        print("Comparison cancelled.")
        raise SystemExit(130)
    except Exception as error:
        print(f"Comparison stopped: {type(error).__name__}")
        raise SystemExit(1)
