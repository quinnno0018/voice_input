# Testing

## Phase 6 Transport Observation V2 (2026-09-17)

Status: the first cloud comparison is recorded below. It reproduces slow C# send completion, but does not identify the underlying transport cause. The enhanced diagnostic runner is locally verified; its new cloud run remains pending. Production Desktop, providers, microphone, TSF, credentials and system network settings are unchanged.

### First Cloud Comparison: Actual Results

Source: ignored `artifacts/asr-send-ab/20260917-213235-9adfd7f2/`. Four sequential runs use the same verified SHA-256 fixture, 100 chunks / 320000 bytes, same endpoint/run-task parameters and disabled compression. Each fixture contains 10 seconds of generated silence, not microphone speech. Times below are local observations, not server-received-audio timestamps.

| Run | Send phase (s) | Send median / max (ms) | Sends >=100 ms | Terminal outcome |
| --- | --- | --- | --- | --- |
| Python 1 | 10.028 | 0.234 / 1.249 | 0 | TimeoutError after send phase |
| C# 1 | 29.325 | 0.254 / 11535.881 | 28 | TimeoutException waiting for finish |
| C# 2 | 22.451 | 0.376 / 2312.086 | 29 | task-finished received |
| Python 2 | 10.010 | 0.380 / 1.611 | 0 | TimeoutError after send phase |

All four local send loops returned for 100 chunks. This is not four successful cloud tasks: three lacked terminal confirmation in the original five-second observation window. No microphone, WPF or native TSF is required to reproduce the C# stalls. Different completion/buffering semantics and possible transport-path differences prevent attributing them solely to a C# bug, exclusively to the network, or declaring the Python connection healthy. Connecting took approximately 2.70-4.92 seconds; this cold connection measurement is separate from the paced send phase.

### Source-Based Findings and Diagnostic Scope

- [websockets 12.0 flow control](https://websockets.readthedocs.io/en/12.0/topics/design.html#backpressure) and the installed `legacy/protocol.py` show that send writes a frame and waits for flow control when paused; return does not require an empty transport or a remote acknowledgment. Installed CPython 3.12.4 `sslproto.py` maintains a TLS outgoing/backlog buffer and forwards bytes into a separate underlying transport. Its upper `get_write_buffer_size()` excludes that lower transport's pending write. [Python's transport API](https://docs.python.org/3.12/library/asyncio-protocol.html#asyncio.WriteTransport.get_write_buffer_size) describes local buffer observations, not remote receipt.
- The [.NET 10.0.0 WebSocket implementation](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Net.WebSockets/src/System/Net/WebSockets/ManagedWebSocket.cs) awaits stream writes/flushes; its [client connection setup](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Net.WebSockets.Client/src/System/Net/WebSockets/WebSocketHandle.Managed.cs) uses HTTP handlers and a default-proxy path. These are reference implementations, not proof of the installed 10.0.11 patch's exact internals: fetching the installed assembly's reported source commit returned 404. Runtime defaults are therefore also observed locally, without claiming source equality or modifying the transport.
- The original Python diagnostic uses a direct library connection with 20-second ping/timeout; the actual local .NET defaults report HTTP/1.1, 30-second keepalive and an infinite keepalive timeout (`-0.001` seconds). Compression remains disabled on both. These differences are recorded, not claimed as the root cause or changed in this round. Proxy resolution is checked inside the C# child using its inherited environment, only reporting direct/proxy/unavailable, never a proxy URL or credential. This does not detect transparent VPNs/interception or prove the actual route used.
- [Paraformer server events](https://help.aliyun.com/zh/model-studio/paraformer-server-events) distinguish task-finished from task-failed; a closed connection alone is not successful task completion. Heartbeats are counted separately from transcript results. No silence result is used to score accuracy or first-visible latency.

### Enhanced Runner and Verification

- Only `send_probe.py`, its tests and the C# test-console probe change. Version 2 retains the same fixture, request, 100 ms send schedule, provider implementation and four-run order. Output is isolated under the existing ignored artifacts tree. The C# test assembly is built into `bin/send-probe-v2/`; the Desktop source and existing launch builds are not replaced.
- `phase` and `timeline_ms` distinguish connect, audio_send, finish_send, finish_ack and completed. Record finish-call duration/return, wait duration, whether the old five-second checkpoint was exceeded, fixed event categories/counts, last-event time, receiver status/error type, and the existing C# send-completion versus continuation observations. The five-second checkpoint no longer cancels the receiver; observe the same request for at most 20 seconds. No finish-task retry or additional audio is sent. `finish_sent` means the local finish call returned, not remote receipt.
- Python records effective write-water marks, socket send-buffer size, TCP_NODELAY, TLS version/backend and bounded buffer samples after sends and every 250 ms. The optional lower-TLS-transport read is guarded to CPython 3.12 and read-only; missing fields remain null. No monkey patch or limit modification is used. Upper/lower buffer sizes must not be added blindly or converted to audio duration; kernel and remote queues remain unobserved. Samples are capped at 512, with omitted count recorded.
- C# listens only to public System.Net socket-connect, TLS-handshake and HTTP connection-established events. Whitelisted event names, times and numeric fields are retained; request headers, raw traces, exception messages and text/audio content are excluded. Private C# socket buffers are not exposed, so those fields stay null. Network-event time is relative to its listener start, slightly after the run clock; do not subtract it as an identical origin without accounting for that offset. Fresh ClientWebSocket defaults are labeled as such, not falsely reported as live private-socket inspection.
- Bounds: connect 12 seconds, audio-send phase 45 seconds, finish send 5 seconds, finish confirmation 20 seconds, enclosing session 85 seconds and bounded close. The C# subprocess deadline is 95 seconds. This extends diagnostic observation only, not product stop behavior, VAD/endpoint settings or user-visible ASR latency policy.
- Red/green: the old runner fails the new version/phase assertions. Release build passes with zero warnings/errors. C# has 48 passing tests; Python has 65, with four actual cross-runtime loopback cases explicitly enabled: unchanged frames/pacing/request, task failure, a 5.2-second late finish without retransmission, and close-after-finish without a terminal acknowledgment. Short-deadline unit tests cover no reply, cleanup ownership and preserving receiver faults. Python syntax compilation passes. Loopback tests use plain local WebSocket, not the cloud TLS/network path.
- Before/after hashes confirm the C# Paraformer provider, microphone and TSF host, Python Paraformer/Fun-ASR providers and ORIGINAL_PLAN are unchanged. No new dependency, system setting, registration, certificate, credential file or product feature was introduced. The tool process still has neither ASR key variable; no v2 cloud run or real-speech acceptance is claimed.

### Next V2 Cloud Run

Keep the old Desktop tray host exited. In the existing key-bearing PowerShell, run the same command as before; the script now selects the isolated v2 test assembly:

```powershell
Set-Location D:\proj\voice_input
& .\prototypes\streaming_asr\.venv\Scripts\python.exe .\prototypes\streaming_asr\send_probe.py --dotnet "$env:LOCALAPPDATA\Temp\voice-input-dotnet\dotnet.exe"
```

Expect the `Diagnostics v2` line and four automatically paced silence runs; no speaking or input-method activation is needed. Slow/timeout runs may extend the comparison to several minutes. Each run stores its own phase/connection/buffer/event evidence, with a compact summary. If Python returns quickly but lower pending bytes grow, investigate hidden local backlog; if finish arrives after five seconds, the previous observation window was insufficient for that run, not proof the stream was realtime. If C# resolves a proxy while Python is direct, validate that route difference before any separately authorized setting change. Do not disable TLS validation, force a global proxy change, enlarge the product queue, or return from C# sends without awaiting them to make the numbers appear faster. Review this evidence before another microphone or Desktop acceptance test.

## Phase 6 Ready-First Capture and Fixed-Audio Comparison (2026-09-17)

Status: startup repair and diagnostic harness pass local tests. Cloud Python/C# comparison and live acceptance of the new Desktop build remain NOT RUN. This section supersedes the older Next Short Live Test instructions while the send backlog is being investigated.

### Latest Real Failure

- In the user's `diagnostics=4` session `b4511c91a04848ceb2d5c5771d15977c`, Notepad activation at 12:51:37 UTC falls back cold after warm expiry. At provider readiness (12:51:42.867), the audio queue already holds 47 chunks / 4700 ms. After 45 sends, it reaches 50 queued chunks with 2 drops; `session_error` explicitly reports `AudioQueueOverflow`. No transcript/native apply occurs in this session. This is a send-backlog stop, not evidence of failed text injection or an invalid API key.
- Slow sends record completion observation of about 455-1016 ms, while continuation delay is about 0.139-0.821 ms. Large post-completion WPF scheduling delay does not explain this sample. Network, socket/OS behavior and completion-observer scheduling still need isolation; these timers do not prove remote receipt or pure network latency. Warm generated-silence sends also show slow completion, outside microphone and TSF text updates.
- All 45 transmitted chunks precede provider readiness in the FIFO, which already held 47 chunks. Therefore audio spoken only after Speak now had not reached the provider before the stop. Low RMS in transmitted chunks does not establish that the microphone failed to capture later speech or that room noise caused the failure.

### Bounded Repair and Local Evidence

- Only TSF startup changes: acquire the ready provider, recheck cancellation and the original foreground window, then start capture and show Speak now. Connecting explicitly asks the user to wait. New logs identify `diagnostics=5 capture_policy=after_provider_ready` and `capture_started provider_ready=True`. The existing audio-log construction timestamp is still not the hardware recording-start timestamp.
- A failed validation/capture releases the acquired provider. The host retains its existing session cancellation, stop/disposal and target guards. Default HUD/Clipboard startup, 180-second warm retention, microphone implementation, 50-chunk capacity, 100 ms / 3200-byte PCM, provider parameters, native TSF and text application are unchanged. No retry, catch-up discard or automatic replay is added. This removes pre-ready backlog; it does not fix sustained slow sends or guarantee instant cold startup.
- Red/green: five startup cases first failed against capture-before-connect behavior. Release solution now builds with 0 warnings/errors; all 46 C# tests pass, including ready gating, cancellation, connection failure, target loss, capture failure cleanup and the unchanged default-mode startup.
- All 59 Python tests pass, including six new diagnostic tests. The two cross-runtime loopback cases were explicitly enabled, not skipped: actual Python `websockets==12.0` and the actual C# Paraformer provider each sent 100 identical 3200-byte binary frames, paced across approximately 10 seconds, with matching run-task payloads, compression disabled and clean finish-task handling. A simulated provider failure stops both without logging the private error payload. Fixture validation, endpoint restrictions, summary calculations and exported-plan credential exclusion are covered. These are local protocol tests, not real cloud results.
- The existing native `live-asr-trace/TsfProbe.LiveTests.exe` passes 31 checks. The native DLL was not rebuilt or registered. The optional fixed-pipe/native integration and legacy activation harness were not rerun; the previously recorded legacy failure remains open. No microphone test, app restart, system setting or credential change was performed.

Build uses the same project-local reference-pack root described in the older build-environment notes, with isolated `bin/tsf-ready-capture/` and `obj/tsf-ready-capture/` paths. `py_compile` passes for the new Python runner and its tests. A local CLI invocation without a key stops before connecting and prints the expected missing-environment-variable message; no cloud run was attempted.

### Fixed-Audio Cloud Comparison

1. Exit the old Desktop tray host manually to avoid its warm traffic overlapping the comparison. Keep the PowerShell containing the API key. Do not speak, activate TSF or change network settings during this run.
2. Run the following from that PowerShell. This reuses the existing Python environment and local dotnet runtime; it neither installs anything nor reads/persists a credential file.

```powershell
Set-Location D:\proj\voice_input
& .\prototypes\streaming_asr\.venv\Scripts\python.exe .\prototypes\streaming_asr\send_probe.py --dotnet "$env:LOCALAPPDATA\Temp\voice-input-dotnet\dotnet.exe"
```

3. The test-console assembly exports a 10-second zero-valued PCM fixture and the actual desktop run-task request without credentials. The runner alternates Python, C#, C#, Python with fresh ready sessions. Both send 16 kHz mono signed PCM16, 100 ms / 1600 samples / 3200 bytes per chunk, according to absolute 100 ms deadlines. A late send catches up without an extra fixed sleep after each completion. No microphone or product queue is involved. Each run has a 12-second connect deadline and a 45-second session deadline, plus bounded teardown.
4. Both clients disable WebSocket compression for this controlled comparison. This is explicit in the diagnostic Python connection because its normal library default can offer compression, especially significant for generated silence. The existing Python and C# provider implementations are not modified. Python retains its existing 20-second ping configuration. The C# probe has no WPF synchronization context, but uses the production provider's diagnostic send path.
5. Results are saved under ignored `artifacts/asr-send-ab/<unique-run>/`: shared fixture/plan, four result files and `summary.json`. Compare successful chunk count/bytes, per-send duration median/max/count at least 100 ms, send-phase duration, connection duration and schedule lateness. `schedule_lag_ms` is lateness against the fixture schedule, not microphone queue wait. `send_phase_complete` is separate from outcome: silence may cause a final provider error and produces no speech-accuracy or first-partial result. Failed/incomplete rows must not be treated as fast passes.
6. Send completion semantics can differ between libraries and buffering layers; neither return proves server receipt. If only C# repeatedly stalls, investigate its WebSocket/TLS/OS transport path before changing ASR parameters. If both stall, inspect the shared path/provider with further evidence. If neither stalls, revisit real-host workload/context and longer-session conditions. Four sequential runs narrow hypotheses, not prove causation or eliminate intermittent network changes.

The tool process does not have the user's separately configured API key. Cloud comparison is therefore handed off to the existing key-bearing PowerShell; the key must not be pasted into chat. After those results are reviewed, launch `src/desktop/VoiceInput.Desktop/bin/tsf-ready-capture/Release/net10.0-windows/VoiceInput.Desktop.dll --tsf-asr` with the existing runtime. No TSF re-registration is needed. Confirm diagnostics 5, test a short warm Notepad session, then separately test after warm expiry; require no pre-ready capture/backlog, a nonempty partial and no dropped audio. These live cases remain pending, and multi-segment/Chrome acceptance is not closed by this repair.

## Validation Scope After Documentation Alignment (2026-09-16)

This document retains dated test evidence. A recorded pass applies only to its build, mode and scenario; later diagnostics and open issues take precedence over older next-step notes. Current progress is summarized in [ROADMAP](ROADMAP.md). The following product-derived criteria are proposals, not new test results. This documentation update does not run the microphone, cloud ASR, TSF registration or application tests.

Documentation-only checks: local Markdown links resolve, fenced blocks are balanced, ORIGINAL_PLAN is byte-for-byte unchanged, source-file hashes match the pre-edit snapshot, and all earlier testing evidence below is preserved. No build or runtime suite was rerun for this update. Claude peer review is incomplete: the attempted product-spec review returned `ConnectionRefused`; an outside-sandbox retry was rejected by automatic approval because sending the full document to a third-party API lacked explicit user authorization. None of this revision's documents has a verified Claude pass. Obtain authorization before retrying that external review; do not treat the new follow-on proposals as reviewed implementation plans.

### Immediate Verification Gate

First follow the newer **Phase 6 Transport Observation V2** above; the earlier investigations below remain historical evidence. Do not change chunk size, provider, warm retention and input mode together. Distinguish warm-hit and cold-fallback sessions. A warm pass does not close the cold-start failure; a one-final pass does not close multi-segment acceptance. Local tests do not include every native harness or prove real application reliability.

### Experience Metrics and Recording Rules

| Metric | Definition | Evidence required |
| --- | --- | --- |
| Activation to ready | User activation to visible ready/listening signal | Mark warm/cold mode; separate from speech onset |
| First HUD text | First speech audio to first visible partial in HUD | Use a correlated speech timestamp or a labeled manual recording; ASR event receipt is not display |
| First target text | First speech audio to first visible target-field text | Separate from HUD; note default stable-paste versus TSF partial |
| Post-speech wait | End of utterance audio to its stable text visibly applied | Local send/ack alone does not prove rendered content; record unavailable rather than invent a timestamp |
| Continuous lag | Alignment between spoken segments and visible target updates | Record pauses, stalls, repeats, omissions and unexpected replacements |
| Usable-text completion | Start of speech to user acceptance after all corrections | Include recognition, final wait, manual edits and optional cleanup |
| Fidelity / correction cost | Required manual corrections and semantic errors on the same material | Distinguish ASR error from cleanup changes; explicitly check names, numbers and negation |
| Command safety | False actions among negative dictation cases; successful actions among explicit cases | Report numerator, denominator, app and mode; target zero unintended sends in the tested set |

Use matched material, microphone, language, network conditions, target application and mode for comparisons. Include Chinese prose, Chinese/English terms, names, numbers and novel dialogue with intentional repetition. Report sample counts and individual outliers; use median/p95 only when the sample supports useful interpretation, otherwise report the raw range. Do not compare provider-event time with another product's visible-text time or publish a ranking based on marketing claims.

The product's sub-500 ms first-visible-text aspiration is a target, not achieved evidence. Current `chunk_captured` records dequeue, not hardware capture. `completion_observed_ms` is not pure network latency or remote receipt, and `continuation_delay_ms` is only the subsequent scheduling interval. Missing hardware/render/user-acceptance timestamps must stay explicitly unavailable or be measured manually.

### Follow-on Acceptance Cases (Not Executable Product Features Yet)

| Capability | Cases | Required outcome |
| --- | --- | --- |
| Recent-result recovery | Provider failure, unknown write acknowledgment, provisional-only result, new session and exit | Stable/provisional and delivery states are distinct; explicit copy only, no automatic paste/replay; memory bounded and cleared by the defined lifecycle |
| Clipboard behavior | Default automatic insertion versus explicit recovery copy | Automatic insertion preserves/restores prior clipboard; explicit Copy intentionally replaces it with the chosen result and does not claim target insertion |
| Manual vocabulary | Add/edit/delete names, Chinese/English terms, unsupported hotwords, similar-sounding words | No promise of perfect recognition; no unsupported provider fields or unconditional replacements; disclose enabled cloud hotword transfer |
| None / Light | Filler-heavy speech, deliberate literary repetition, names, dates, amounts and negation | None adds no LLM rewrite; Light changes only permitted stable spans and preserves meaning; recover original through Undo |
| Stale cleanup | Type into target, move cursor, switch target or revise source before result arrives | No stale overwrite, no retargeting; all session/target/revision/source checks pass before application |
| Explicit edit | Selected text / exact replacement versus ambiguous reference or quoted instruction | Change only confirmed span; ambiguous or quoted text triggers no destructive edit; test undo |
| Voice Send | Explicit enabled command versus disabled mode, dialogue containing “发送”, unknown app or changed target | Execute only with validated intent and target rule; all negative cases produce no send |
| Privacy / retention | Default dictation, enabled hotwords, optional span rewrite and exit | Only disclosed required content leaves the device; no default audio history or whole-app context collection; verify our behavior separately from provider policy |

Define concrete recovery size limits, dictionary storage rules and UI acceptance before implementing those features. These cases do not authorize adding them during the current backlog investigation. Product research sources and adoption limits are maintained in [PRODUCT_SPEC](PRODUCT_SPEC.md), rather than duplicated as measured competitor results here.

## Phase 6 Send Backlog Investigation (2026-09-16)

Status: diagnostic build and local tests pass. The reported real-session send slowdown is NOT yet attributed to network versus client scheduling, and no live repair is claimed.

### Real Failure Evidence (2026-09-15)

- The `tsf-warm` build logs `diagnostics=3` and cold fallback with `reason=expired`. Session `854b7554101140069ae0bcd5c0e8a9ad` starts at 11:48:23 UTC and stops at 11:48:38 with `IOException`, previously classified only as `Unclassified`. This is not the earlier multi-segment `InvalidDataException`.
- Microphone metadata retains 16 kHz, mono, PCM16 little-endian, 100 ms / 1600 samples / 3200 bytes. Speech RMS reaches approximately 0.01-0.035 versus near-silence approximately 0.00002. Two partial updates (lengths 1 and 3) receive native acknowledgments. No evidence points to absent capture or failed TSF insertion in this run.
- Capture setup to provider readiness is approximately 4032 ms. Sending 90 chunks (9 seconds of audio) takes 10774 ms of wall time; 25 sends exceed 100 ms and the maximum is 1286.931 ms. The summary records 2 dropped chunks. The microphone channel holds 50 chunks (5 seconds); cold-start buffering plus slower sending reduces its remaining headroom. The source aborts immediately after a send when it observes any dropped audio. These facts strongly identify the queue-overflow guard as the immediate failure path, although the old log did not retain the exception's specific reason.
- The previous three diagnostic runs recorded no drops and maximum send durations of 0.582, 0.527 and 0.413 ms. The old send timer surrounds an await on the WPF context, so it includes continuation scheduling. It is not a pure network measurement. The user's later network-problem report is supporting context, not proof of this session's exact cause.

### Diagnostic Changes

- `diagnostics=4` retains the same microphone, 50-chunk capacity, 180-second warm lifetime, Paraformer parameters, cancellation behavior and TSF protocol. No audio is retried, silently discarded for catch-up, or sent to a new target.
- In TSF mode, `websocket_send` records the first send, sends taking at least 100 ms, and failed/cancelled sends. It separates `completion_observed_ms` (invocation through send-task completion observation using the default task scheduler) from `continuation_delay_ms` (observation through resumption on the original context). `total_ms` is their sum. It also records only a locally generated task ID, sequence, byte count and fixed outcome label.
- A high continuation delay implicates client scheduling after observed completion. A high completion time narrows the delay to the send operation or its completion observer; it still does NOT prove pure network latency, remote receipt, or exclude thread-pool/OS stalls. Compare with existing outer send timing and queue entries, not ASR recognition latency.
- `audio_queue` records queued chunks, approximate queued audio duration, sent chunk count and drop count at provider readiness, first send, slow sends and overflow. Counts exclude the in-flight chunk. Existing `chunk_captured` is logged on dequeue, not at the hardware callback, and must not be treated as a hardware capture timestamp.
- Audio and transcript queue overflow now have separate typed reasons and fixed user-facing messages. Audio overflow displays `audio backlog exceeded the buffer`. Do not expose arbitrary provider exception text. No transcript, audio content, API key or endpoint credentials are added to diagnostics.

### Verification and Build Environment

- Red/green: the original 36 C# tests pass; 5 new tests first fail for missing diagnostic behavior. After implementation, all 41 pass. Controlled paused-context test: completion approximately 1.2 ms, continuation approximately 176.2 ms. Controlled pending-send test: completion approximately 165.1 ms, continuation approximately 0 ms. These are synthetic local results, not cloud benchmarks.
- Coverage includes preserving original asynchronous and synchronous failures, cancellation/token identity, successful send despite diagnostic sink failure, no retry, unchanged caller context, and distinguishing audio/text backlog without exposing untrusted error messages.
- Release solution build passes with 0 warnings/errors. The existing native `live-asr-trace/TsfProbe.LiveTests.exe` passes 31 checks, with no microphone or registration used. The native DLL was not changed or rebuilt. The optional live pipe integration was not run; the known legacy native activation-harness failure remains open.
- Local build initially failed because the portable SDK's reference manifest listed three absent files: `Microsoft.VisualBasic.dll`, `System.Drawing.dll`, `WindowsBase.dll`. A complete same-version [Microsoft.NETCore.App.Ref 10.0.11](https://www.nuget.org/packages/Microsoft.NETCore.App.Ref/10.0.11) package was downloaded to ignored `artifacts/send-diagnostics-packs/`, its SHA-512 matched NuGet's official catalog, and its reference manifest was checked for missing files. NuGet signature verification itself was unavailable due to missing files in the portable SDK; it is not claimed as passed. Existing Windows Desktop/host packs were copied into this project-local build-only root. No original SDK, global installation or project dependency was changed.

Build with the existing dotnet executable from the repository root (the pack-root argument is a local verification setting, not a product runtime dependency):

```powershell
dotnet build src/desktop/VoiceInput.sln --no-restore -c Release -p:BaseOutputPath=bin/tsf-send-diagnostics/ -p:IntermediateOutputPath=obj/tsf-send-diagnostics/ "-p:NetCoreTargetingPackRoot=$PWD/artifacts/send-diagnostics-packs/"
dotnet src/desktop/VoiceInput.Desktop.Tests/bin/tsf-send-diagnostics/Release/net10.0-windows/VoiceInput.Desktop.Tests.dll
```

### Next Short Live Test

1. User-managed exit of the old tray host, then launch `src/desktop/VoiceInput.Desktop/bin/tsf-send-diagnostics/Release/net10.0-windows/VoiceInput.Desktop.dll --tsf-asr` with the existing runtime from the key-bearing PowerShell. Do not change the key, audio settings, default input method or TSF registration. Confirm `diagnostics=4` in this build's `logs/desktop-tsf-asr.log`.
2. After roughly 10 seconds, focus disposable Notepad text, select the existing probe, press Ctrl+Alt+J and wait for Speak now. Say: `明天下午四点，我们先检查服务器连接。确认连接正常以后，再继续测试语音输入。` Pause naturally, wait for finalization and stop with the same hotkey. No deliberate network interruption or manual editing in this first check.
3. Correlate `asr_connection`, `websocket_send`, `audio_queue`, typed `session_error` and the audio summary. If the issue returns, stop further repetitions and inspect those records. A passing warm test alone does not close the cold-start problem. After the short test passes, a separately identified cold test after more than 190 seconds can cover the prior startup-buffering condition; long speech validation remains deferred.

No live speech/provider test was performed by this change. The final read-only running-process query was blocked by an automatic-approval connectivity failure, so the currently running build was not reverified. No user app was stopped or restarted.

## Phase 6 Configurable Warm Retention Trial (2026-09-12)

Status: implemented and locally tested after user approval. Live startup improvement and multi-sentence safety remain NOT VERIFIED.

- TSF mode reads `VOICE_INPUT_TSF_WARM_SECONDS` with a default of 180 seconds and an integer range of 30-300. The timer starts when the provider is ready; expiry closes the unused warm task without automatic renewal. Default HUD/Clipboard mode remains at 30 seconds. Sample rate, mono PCM16, 100 ms chunks, Paraformer request parameters, 1200 ms sentence silence, TSF packet format and native DLL are unchanged.
- Startup reports `diagnostics=3`, `warm_retention_seconds` and sentence silence. Warm lifecycle reports `warm_ready`, `warm_claimed`, `warm_expired` with a local warm generation. Acquisition reports `asr_connection mode=warm` or `mode=cold reason=expired|unavailable|not_prepared`. These are lifecycle diagnostics, not transcript content or new latency metrics. Idle audio is generated silence, not microphone capture.
- Two focused regressions confirmed an existing cleanup defect: if the silence sender failed, claim had already marked the provider as transferred, so fallback skipped releasing it; explicit disposal also rethrew the sender error before cleanup. Failed claim now retains cleanup responsibility, and disposal/expiry observe sender failure before completing disposal. The original send failure still reaches acquisition and selects cold fallback. This does NOT identify or fix the earlier InvalidDataException in multi-segment speech.
- Red/green: before implementation the 32-test run had 3 failures (missing config validation, failed-sender provider not disposed, failed-sender disposal throwing). After implementation and added coverage, the 36-test run passes, exit 0. Coverage includes defaults/bounds, unchanged audio/sentence parameters, warm reuse, timer cancellation after claim, expiration fallback, failed-sender fallback/disposal/expiry, diagnostic sink failure and cancelled acquisition without starting a new provider. Expiry tests use shortened local lifetimes, not an actual 180-second cloud connection.
- Fresh build: `dotnet build src/desktop/VoiceInput.sln --no-restore -c Release -p:BaseOutputPath=bin/tsf-warm/` passes, 0 warnings/errors. Run the corresponding test DLL under `VoiceInput.Desktop.Tests/bin/tsf-warm/Release/net10.0-windows/` for the 36 tests. Existing `build/live-asr-trace/TsfProbe.LiveTests.exe` passes 31 checks; native binaries were not rebuilt.
- The optional C#/native integration was not rerun because the diagnostic host PID 40848 still owns the fixed pipe. The prior full native activation-harness failure remains open. No old app was stopped, output overwritten, registration changed, key/config file modified or live microphone/provider run performed by this implementation.

### User-Managed Launch

Exit the old host through its tray menu, retaining the PowerShell that contains the API key. Run the new `VoiceInput.Desktop/bin/tsf-warm/Release/net10.0-windows/VoiceInput.Desktop.dll` with the existing dotnet runtime and `--tsf-asr`. No Notepad restart or input-method re-registration is required. Verify the new output's `logs/desktop-tsf-asr.log` has `diagnostics=3 warm_retention_seconds=180` (fields may have intervening metadata). The previous diagnostic build remains available for rollback.

### Next Manual Tests

1. Start the new host, allow preparation to finish, then use Ctrl+Alt+J in an empty Notepad input. After Speak now, read all eight lines below in ONE session, pausing 2-3 seconds after each sentence. Do not type, select or move the caret during this first run. After the last line, wait for finalization and press Ctrl+Alt+J to stop. Require at least 6 distinct stable segments in the log, no replacement/duplication of previous sentences, and no session error; merely seeing a full paragraph is insufficient.
2. After stopping, wait about 60 seconds and start again. Read the first two lines and stop. Check startup feeling against the previous 2.47-3.25 second waits and confirm `mode=warm` in the log; provider availability still needs live evidence.
3. Separately verify a short restart after about 10 seconds, then an idle interval of at least 190 seconds. The latter should log expiration and cold fallback; a connection wait after expiry is expected, not a failed warm hit. These rows remain NOT RUN on this build.

Read these sentences aloud; do not read punctuation names:

```text
明天下午四点，我们先检查服务器连接。
确认连接正常以后，再打开测试页面。
今天先验证连续语音输入是否稳定。
这一句话结束以后，我会稍微停顿一下。
接下来检查 WebSocket connection 是否正常。
如果英文识别不准确，先保留它的原始结果。
前面已经输入的文字，不应该被下一句话覆盖。
现在这一轮测试结束了。
```

The last sentence is dictation, not a voice-stop command; use Ctrl+Alt+J to stop. Report errors separately from English transcription accuracy. No punctuation A/B or downstream feature work starts from these local test passes.

## Phase 6 Live Findings and Diagnostic Capture (2026-09-12)

Status: real microphone-to-TSF output has been observed in Notepad. Three diagnostic retests completed without a logged session error, but the earlier exception remains unclassified and open. Each retest contained only one finalized segment, so multi-segment stability is not accepted.

### Diagnostic Retest and Startup Delay

- User completed three diagnostic runs and reports segmentation is acceptable for these samples, but starting again appears to wait several seconds. The new log confirms `diagnostics=2 max_sentence_silence_ms=1200`, 69 partial acknowledgments, 3 stable acknowledgments and no session errors. Each run has one stable segment; the earlier error occurred after multiple segments. No exception fix was made or verified.
- Existing log timestamps were correlated by session start/end, provider-ready and first-partial events. Durations below are client session activation to provider readiness, not a separately instrumented physical-key timestamp or pure WebSocket handshake time.

| Run | Since previous warm-ready (s) | Session start -> provider ready (ms) | First audio sent -> first partial (ms) | Session start -> first partial (ms) | Applied partial / stable | Dropped chunks |
| --- | --- | --- | --- | --- | --- | --- |
| 1 | 37.603 | 3253.8 | 2073.2 | 5330.4 | 24 / 1 | 0 |
| 2 | 125.085 | 2576.2 | 179.8 | 2756.2 | 23 / 1 | 0 |
| 3 | 34.850 | 2474.6 | 1579.1 | 4053.8 | 22 / 1 | 0 |

- Source inspection: the TSF host creates one warm task at app startup and after each session, retaining it for 30 seconds after preparation. Expiry disposes that task without creating a replacement. Claiming an expired task falls back to a new provider connection. All three activations occur beyond the 30-second window; the code and timestamps explain repeated cold-start waits. The log does not yet explicitly label warm claim/expiry/cold selection. This is the existing retention policy, not evidence that the diagnostic callback broke warm reuse.
- Audio capture buffers while acquisition is pending, so the 179.8 ms first-send result is not speech-to-partial latency and must not be compared as an unbuffered provider benchmark. Current logs cannot isolate all remaining first-partial delay into speaking, buffering, network and provider processing.
- Suggested next change, not implemented: make warm retention configurable and trial 180 seconds while retaining cold fallback and bounded idle resource use. This can cover these observed gaps, not guarantee instant activation after arbitrary idle time. Keep sentence silence at 1200 ms; a punctuation A/B is not the current priority after this feedback. Continue diagnostic multi-segment coverage for the original exception.
- This follow-up only inspected existing logs/source and recorded results. No business code, parameters, registration, running processes or credentials changed; no build/tests were rerun.

### Observed Evidence

- User reports several Notepad real-speech runs, English word mismatches and frequent periods following pauses. No reference recording/transcript pair was supplied, so no numerical recognition accuracy or attribution to pronunciation is established.
- Notepad PID 45852 was verified loading `build/live-asr-trace/VoiceInput.TsfProbe.dll`. The inspected C# bridge log contains 3 sessions, 43 partial acknowledgments and 11 stable acknowledgments. These do not establish that the complete safety/compatibility matrix passed.
- At `2026-09-12T11:41:10.4483586Z` (19:41:10 China time), session `6befb79614cc47bca94b5c5b2ffd68a0` ends with `InvalidDataException`. Its last recorded partial belongs to local segment 6; no stable for that segment was logged. The old logger omitted the failure message, source identity and rejected event, so the exact guard cannot be reconstructed.
- That session's audio log reports 200 captured/sent chunks, 640000 bytes and 0 dropped chunks, with 16000 Hz, mono, signed PCM16 little-endian, 100 ms, 1600 samples and 3200 bytes per chunk. This rules out recorded queue overflow for that run, not every possible audio or recognition issue. Native traces show successful applies and composition completion on shutdown, not evidence of a specific ASR/pipe failure cause.
- [Paraformer server events](https://help.aliyun.com/zh/model-studio/paraformer-server-events) documents sentence timestamps, final flags and skippable heartbeats, but does not explicitly guarantee immutable begin_time as a segment ID. [Client events](https://help.aliyun.com/zh/model-studio/paraformer-client-events) confirms the existing language hints and VAD silence parameter. No endpoint, model or request-parameter change was made from this review.

### Diagnostic-Only Changes and Verification

- Preserve all event filtering, segment/ack guards, exception types, stop behavior and native packet bytes. Add typed reasons to existing invalid-data failures so the next log distinguishes missing identity, identity change before final, packet validation and acknowledgment mismatch. Unknown exception messages and inner exceptions are never copied into diagnostics.
- Only the TSF host enables the provider diagnostic callback. Before filtering a result, record numeric begin/end timestamps, typed heartbeat/final flags, text kind/length and content-validity status. Include generated task ID and received-frame sequence; arbitrary field strings, transcript text, audio and keys are excluded. Core segment diagnostics record only local sequence and identity-match/closed flags, not provider-specific fields.
- Record effective sentence silence and `diagnostics=2` at host startup, plus session ID and cloud/transport task status on failure. Diagnostic callback failures do not alter parser/segment behavior. No new latency metric or audio instrumentation was added.
- Red/green evidence: the reason test compiled against the old implementation and failed `Missing diagnostic reason: SegmentIdentityMissing` (26 tests, 1 failure). After the diagnostic additions, all 29 C# tests pass, including malformed-field privacy, pre-filter invalid-final visibility, duplicate/rejected events, failing log sinks, and a real local named-pipe mismatched acknowledgment that leaves the queued final unapplied.
- Fresh build: `dotnet build src/desktop/VoiceInput.sln --no-restore -c Release -p:BaseOutputPath=bin/tsf-diagnostics/` passes with 0 warnings/errors. Running `VoiceInput.Desktop.Tests/bin/tsf-diagnostics/Release/net10.0-windows/VoiceInput.Desktop.Tests.dll` passes 29 tests, exit 0.
- Existing `build/live-asr-trace/TsfProbe.LiveTests.exe` passes 31 checks, exit 0; native binaries were not rebuilt. The optional C#/native pipe integration was not rerun because the user's host PID 31980 still owns its fixed pipe. The known legacy native activation harness failure remains open; do not claim the full native suite is green.
- Output is isolated at `src/desktop/VoiceInput.Desktop/bin/tsf-diagnostics/Release/net10.0-windows/`. No running app was closed, registered DLL replaced, key/config persisted or API connection started by this diagnostic implementation.

### Next Live Capture

The three-run capture below is now completed as recorded above. Do not repeat it unchanged to claim coverage of the earlier multi-segment exception.

1. Exit the old C# host through its tray menu, keeping the key-bearing PowerShell open. Launch the isolated Desktop DLL with `--tsf-asr` using the existing dotnet runtime. No re-registration or Notepad restart is required for this C#-only update.
2. Keep the 1200 ms baseline. Confirm the new output's `logs/desktop-tsf-asr.log` starts with `diagnostics=2 max_sentence_silence_ms=1200`.
3. In disposable Notepad text, use Ctrl+Alt+J and wait for Speak now. Repeat three short runs of: `我们先确认服务器连接正常，然后检查 WebSocket connection，如果没有问题，就继续测试下一句话。` Pause naturally between clauses without saying the punctuation names. Stop with the same hotkey. If an error appears, report it and inspect the matching metadata; do not assume the cause from a synthetic fixture.
4. Only after the exception is understood/resolved: compare 1200 and 1600 ms using the same sample, microphone and cadence, recording fragmented sentences, final wait and subjective partial latency. Then compare the intended/recognized English terms separately. These A/B and accuracy checks remain NOT RUN; no default parameter is changed now.

## Phase 6 Registration Switch (2026-09-12)

Status: authorized registration switch completed; real-speech acceptance remains NOT RUN.

- Unregistered the previous `build/host-handoff-trace` experiment and registered `build/live-asr-trace` through the existing manager. The elevated helper completed with exit 0. The helper checked the original DLL path and unchanged user identity before changes, with rollback to the prior experiment on registration failure.
- Independent inspection confirms RegisteredDll matches the requested live-asr-trace DLL; COM, TIP and user-list entries are present. Before/after comparison confirms the entire language/input-method list and default input-method override are unchanged.
- Runtime inspection still finds Chrome PID 12292 loading host-handoff-trace and Notepad PID 15024 loading keyboard-handoff-trace. Registration does not replace an already loaded DLL. No user apps were closed or restarted automatically.
- No Desktop executable or dotnet-hosted Desktop process was found. The existing portable dotnet runtime and Release Desktop DLL both exist. The tool's current process and user-level environment have no ASR key; a key set in the user's separate PowerShell may still be available there. Only presence was inspected; no key value was printed, copied or persisted.
- Next: user-managed save/close/reopen of the target apps, launch the existing Release DLL with `--tsf-asr` from the key-bearing PowerShell, verify the loaded live DLL, then perform the first Notepad real-speech case below. Ctrl+Alt+J requires speaking in this build; it no longer plays the simulated sentence.
- This step changed registration and recorded status only, not business code. No build or unit tests were rerun; registration inspection, settings comparison and runtime-module inspection are the verification for this operation. Earlier automated results below are not live-ASR acceptance.

## Phase 6 Implementation and Local Verification (2026-09-12)

Status: implementation is ready for registration-controlled live testing, not live-accepted. The user waived the unavailable external Claude review. No microphone recording, provider connection, browser typing or registration change was performed by this verification.

### Automated Results

- `dotnet build src/desktop/VoiceInput.sln --no-restore -c Release`: PASS, 0 warnings, 0 errors.
- `dotnet run --project src/desktop/VoiceInput.Desktop.Tests/VoiceInput.Desktop.Tests.csproj --no-restore -c Release -- --native-tsf-test <absolute-path-to-live-asr-trace/TsfProbe.LiveTests.exe>`: 26 tests passed, exit 0. Omitting the optional native test argument runs 25 tests.
- `prototypes/tsf_input/build.ps1 -LiveAsr -Zig <zig.exe>` and the `-HandoffTrace` variant: both x64 DLLs and test executables compiled. Existing interruption regression: 119 normal / 130 diagnostic checks passed. New native live checks: 31 passed per variant.
- Both native build scripts still exit 1 because the legacy direct activation harness fails with `E_INVALIDARG` after 46 passing checks. No test was disabled and the full native suite is not passing. This failure predates the live bridge; Windows-hosted activation is a separate manual acceptance item.
- Actual C# / C++ byte-pipe integration passed with fake text, without a microphone or TSF registration: GUID/target handshake, four updates across two segments, matching acknowledgments, clean pipe close and worker reference release. A second case closes the pipe while native acknowledgment is pending and confirms prompt worker termination.
- Unit coverage includes UTF-16LE layout, fragmented reads, malformed/oversized packets, stale acknowledgments, duplicate finals, late partials, segment ordering, queued-final cancellation, stable retaining the session/anchor, host termination with a 700-character owned snapshot, and stop-chord restart suppression.
- The new stop-chord test first failed: Ctrl cancelled the session, then preserved J tried to start again. It passes after the live-only suppression fix. Existing simulated interruption checks remain passing.
- Read-only `manage.ps1 -Action Inspect -LiveAsr -HandoffTrace` confirms the new DLL exists but actual registration remains `build/host-handoff-trace/VoiceInput.TsfProbe.dll`. COM, TIP and user-list entries are present. No user apps were closed.

### Live Test Setup

1. The authorized registration switch is complete as recorded above. Verify the loaded module after user-managed app restart; existing Chrome/Notepad processes still hold older DLLs. Save work before fully closing the apps.
2. Exit the old Desktop instance, then run `dotnet run --project src/desktop/VoiceInput.Desktop/VoiceInput.Desktop.csproj -c Release --no-build -- --tsf-asr` from a PowerShell that already has `DASHSCOPE_API_KEY`. Do not print or persist the key. The original mode remains available without the argument.
3. Select `Voice Input TSF Probe` with Win+Space in an empty Notepad or Chrome/ChatGPT input. Ctrl+Alt+J now starts real microphone input, not the simulated sentence. Wait for the HUD's `Speak now`; the warm connection may already be ready. Press the same chord to stop.
4. Diagnostics are in the Desktop output's `logs/desktop-tsf-asr.log` (session, packet, length and acknowledgment metadata) and the existing audio diagnostic log (format, device, RMS/peak and send counters). No raw transcript, audio recording or key is logged by the bridge. Native handoff traces remain available in the diagnostic build.

### Required Real-Speech Matrix

This is the original live-ASR acceptance matrix. The later Notepad observations above establish real input and multiple segment acknowledgments, but are not per-row acceptance for duplication, preservation or safety. Other real-speech cases remain NOT RUN unless explicitly recorded. Previous simulated/Clipboard passes do not count as these results.

| Scenario | Sample / action | Required observation |
| --- | --- | --- |
| Notepad progressive input | Say `明天下午四点检查 WebSocket connection。` | Nonempty partial appears in the input before finishing; final does not duplicate it. |
| Two natural sentences | Say the first sentence, pause about two seconds, then `确认连接正常以后，我们再继续测试下一句话。` | First stable commits, recording continues, second sentence appends without replacing the first. |
| Chrome/ChatGPT | Repeat the same sentences in an unsent draft | Progressive inline text and final retention; do not send the draft. |
| Existing content | Type `前缀｜后缀`, place the caret after the separator, then speak | Both manual prefix and suffix survive. |
| Keyboard interruption | While partial is appearing, type `233aaa` | Voice updates stop, visible text remains, typed order stays correct. |
| Caret and selection | Separate runs: move the caret / select text mid-speech | No later writes, replacements or caret capture. |
| Focus loss | With a fixed prefix, click Chrome's address bar mid-partial; separately switch to Notepad | Existing/visible old-target text survives; no result enters the new target. |
| Stop/restart | Repeat Ctrl+Alt+J start/speak/stop three times | Stop does not immediately restart; new sessions do not replay old results. |
| Continuous speech | Speak Chinese with English terms for two minutes | Multiple stable segments, no duplicate insertion or dropped audio chunks. |
| Connection failure | During a separately agreed network interruption test | Local text remains usable, recording stops, no replay after recovery. |

Known limitations: x64 developer experiment only; empty selection required at activation; per-segment text capped at 1024 UTF-16 units; no automatic continuation after manual edits or focus change; cold start may still wait after the 30-second warm window expires; sentence-start identity correlation and actual inline latency await live evidence. There is no Clipboard fallback in live mode, no universal app support and no Beta credential distribution.

## Phase 6 Preparation (2026-09-12)

- Follow-up after document-sharing authorization: the first forced Claude review timed out at 180 seconds. A retry with a process-local 360-second limit returned `ConnectionRefused` and no review. Read-only checks show Claude is logged in but its user-configured `ANTHROPIC_BASE_URL` points to 127.0.0.1:15721, with no listening service. No global settings, credentials, proxy services or registration were modified. There is no pass marker or approved review result.
- A temporary test confirmed the current parser lacks neutral segment identity (22 tests, one expected failure). The test was then removed because bridge implementation is blocked at the review gate; no production code was changed. This observation is preparation, not completed segment handling.

- User requested real-ASR-to-TSF integration after the limited Phase 5 closeout.
- Re-ran `dotnet run --project src/desktop/VoiceInput.Desktop.Tests/VoiceInput.Desktop.Tests.csproj --no-restore`: all 21 tests passed, exit 0. This rebuilt/runs the existing Desktop test baseline; it does not validate a live bridge.
- Re-ran the existing `build/host-handoff/TsfProbe.InterruptionTests.exe`: 119 checks passed, exit 0.
- Re-ran the existing `build/host-handoff-trace/TsfProbe.InterruptionTests.exe`: 130 checks passed. These were existing binaries, not newly rebuilt DLLs.
- The legacy direct activation harness was not rerun; its previously recorded `E_INVALIDARG` failure remains open. Do not report the entire native suite as passing.
- No microphone session, real-ASR-to-TSF run, system registration change or user-app interaction was performed in this preparation. The implementation draft is unreviewed: external Claude review was blocked pending explicit document-sharing permission. Live acceptance remains pending.

## TSF Inline Composition Experiment (2026-09-08)

### Phase 5 Safety Closeout Feedback (2026-09-12)

#### Host-Termination Repair Candidate

- Final requested regression feedback (2026-09-12): user confirms all three follow-up cases pass on the new build: uninterrupted full output occurs once; mid-output `233aaa` retains order and stops automatic output; switching to Notepad retains Chrome text and does not write the old task into Notepad. These are manual user-reported results, not newly inspected per-run traces or automated tests. Together with the prior known-prefix address-bar retest, the requested focused repair regression set and limited Phase 5 safety closeout are accepted. This does not establish broad application compatibility, real-ASR-to-TSF stability or a fully passing automated suite; the known direct activation harness failure remains. No code, registration or build/test execution changed while recording this feedback.

- Live retest passed for the reported loss case (2026-09-12): user reports both fixed prefix and unfinished text retained after the requested mid-output address-bar switch. Read-only inspection confirms Chrome PID 12292 loads `build/host-handoff-trace/VoiceInput.TsfProbe.dll`, with the matching activation marker. The process log shows one simulation, seven partial updates, `host-text-reaffirmed` S_OK, then host termination with no later automatic frames before deactivation. The flushed buffer contains 32 records, dropped=0, including successful host-reaffirm-before/after. Visible preservation is user-reported, not automated; the log corroborates the new path. The subsequently requested regressions are now user-confirmed above. No code, registration or build/test execution changed during this evidence check.

- Implemented after explicit repair authorization: `OnCompositionTerminated` stops playback, captures then detaches the old composition/context, and attempts one same-content SetText using the host-supplied write cookie. It verifies the range's context identity and exact text against the last applied snapshot first. Empty, modified, unreadable, unavailable, foreign-context or reentrant-frame-write cases skip/fail safely; there is no retry, new edit session, caret write, second EndComposition or new-focus replay.
- Why this boundary: observed host termination can precede the posted local finish. Previously the callback only discarded local state. The callback's documented write opportunity permits range-local processing without moving mouse/focus cleanup into an unsupported synchronous request. [Microsoft contract](https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfcompositionsink-oncompositionterminated) and [SampleIME callback](https://raw.githubusercontent.com/microsoft/Windows-classic-samples/main/Samples/IME/cpp/SampleIME/Composition.cpp) were inspected. SampleIME deletes its dummy composition; that deletion behavior is deliberately NOT copied. Whether a same-content write causes this installed Chrome renderer to retain text is still a live-test hypothesis.
- Red/green: the new production-callback regression compiled against the old implementation, then failed `host termination reaffirms the owned text once using the supplied write cookie`. It passes after repair. A range double confines edits to a span between fixed prefix/suffix and checks supplied-cookie use, original context, reentrant/duplicate termination, queued stale frames, unchanged selection, failed reads/writes and reference cleanup. It does not emulate browser commit/cancel or prove visible Chrome behavior.
- Fresh builds: normal DLL compiled and 119 interruption checks passed; diagnostic DLL compiled and 130 interruption/trace checks passed. Both complete build scripts still exit 1 because the existing legacy harness reports 46 passes followed by direct Activate failure `E_INVALIDARG` (0x80070057). This failure was not suppressed. Build/manage PowerShell syntax checks pass.
- New outputs: `build/host-handoff/` and `build/host-handoff-trace/`. The manager selects these with `-HostHandoff` and optional `-HandoffTrace`; old `-KeyboardHandoff` paths remain available for unregistering the current DLL. Combining HostHandoff and KeyboardHandoff is rejected. No existing loaded DLL was overwritten.
- Authorized registration switch (2026-09-12): unregistered `keyboard-handoff-trace` and verified COM/TIP/user-list entries absent, then registered `host-handoff-trace`. Inspection confirms all three entries present and RegisteredDll exactly matches `build/host-handoff-trace/VoiceInput.TsfProbe.dll`. Before/after language lists retain the original Chinese input method and default override remains unset. No user app was closed or DLL overwritten. Chrome PID 30500 still loads the older keyboard-handoff-trace DLL; user-managed full Chrome restart and loaded-module verification are required before interpreting the retest. No build/tests were rerun during this registration-only step; visible preservation remains pending.
- Diagnostic marker: `preserve-key-ctrl-alt-j-host-handoff-trace`. Outcomes are `host-text-reaffirmed`, `host-text-preserve-skipped`, or `host-text-preserve-failed`; numeric trace adds host-reaffirm-before/after without text/key contents. SetText success is not renderer confirmation. If the host already cleared/changed the range or ignores the write on its cancellation path, loss can remain; do not declare Phase 5 complete until the known-prefix mid-output focus-switch test actually preserves text. Regress full completion, keyboard order and cross-app no-miswrite after the candidate passes that test.

- Investigation (read-only, 2026-09-12): Chrome PID 41944 still uses the repaired diagnostic DLL; installed version is 152.0.7977.83. Three recent host-termination traces each follow seven successful partial frames with no stable apply. Two traces record queue-input then host-termination, without request-finish/end-before/end-after between them: QPC 2061546453211 -> 2061546466035 (1.2824 ms) and 2061804115056 -> 2061804126150 (1.1094 ms), frequency 10000000. Both buffers contain 30 records, dropped=0. The third contains host-termination while running=1, without queue-input (29 records, dropped=0). These cycles cannot be uniquely assigned to address-bar versus cross-app actions from existing logs.
- Local ordering: InputMessageHook halts playback and posts completion without editing text. Host termination can arrive before that completion; OnCompositionTerminated then clears local composition/context and invalidates the generation again. The posted old-generation message is ignored, and any late edit rejects the cleared context. Focus callbacks guarded by playback.running may also skip their Stop path once the hook has stopped playback. These guards prevent stale writes; removing them or replaying text into the new target is not a justified fix. The inspected termination/cleanup code contains no explicit text deletion, but that does not prove the host or web editor retained the composition.
- Microsoft documents that [OnCompositionTerminated](https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfcompositionsink-oncompositionterminated) requires no particular text-service action; the notification is not proof of deletion. Upstream [InputMethodWinTSF](https://raw.githubusercontent.com/chromium/chromium/main/ui/base/ime/win/input_method_win_tsf.cc) confirms composition before removing a focused client. [TSFTextStore](https://raw.githubusercontent.com/chromium/chromium/main/ui/base/ime/win/tsf_text_store.cc) has both confirm and cancel paths leading to host termination, so our host-termination marker does not distinguish them. Fetching source for the exact installed Chrome tag failed; upstream code is reference evidence, not a verified installed-browser call stack.
- Existing tests check asynchronous non-keyboard cleanup and guard removal on host termination, but not preservation of live renderer text when termination precedes posted cleanup. No tests were rerun or changed during this investigation. [RequestEditSession](https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfcontext-requesteditsession) limits reliable synchronous requests to documented contexts; the successful keyboard repair is not evidence that synchronous edits inside a mouse hook or focus callback are safe.
- Discriminating test completed by the user on the existing build: simulated text stays underlined until full output completes. Clicking the address bar mid-output removes only the underlined unfinished text; the manually entered fixed prefix survives. Clicking after full output (underline gone) preserves the completed text. This isolates the reported loss to active composition, not general deletion of committed/user-authored content. The exact host/renderer call that removes it remains unconfirmed; no new trace is individually correlated to this comparison. This test need not be repeated unchanged.
- Computer Use was attempted before that manual feedback: initialization failed, then a reset/retry failed with `windows sandbox failed: helper_unknown_error: setup refresh had errors`. No browser input was performed and no automated visual result exists. No business code, registry, dependencies or system settings changed.

- Closeout is BLOCKED on loss of active composition when moving from ChatGPT to Chrome's address bar. The prefix/completed-text comparison above confirms the loss boundary. Do not claim broader committed-text corruption, or interpret loss prevention as permission to write into the newly focused target.
- Cross-application switch to Notepad: user reports output stops, ChatGPT text remains, and no new text appears in Notepad. Return-to-original-field behavior was not separately described.
- Existing-content insertion and interruption/restart scenarios were both reported passed with no issues, following the requested Notepad/ChatGPT test instructions. These are manual results, not automated assertions or individually correlated traces.
- Read-only inspection still finds Chrome PID 41944 loading `build/keyboard-handoff-trace/VoiceInput.TsfProbe.dll`. Its log contains multiple `host-terminated-composition` records. The production callback stops playback and clears local composition/context state; ordinary focus cleanup requests remain asynchronous. No log entry is uniquely matched to the address-bar click, and these records do not establish who removed text or why. Host cancellation versus completion ordering remains an investigation hypothesis, not a confirmed cause.
- Next: isolate same-browser focus-loss text preservation before moving to real ASR integration. Preserve earlier keyboard-handoff regression passes. No business code, system registration or build/tests changed while recording this feedback.

Status: The minimal keyboard-handoff repair's requested manual regressions have passed by user report (2026-09-12): Chrome first-character ordering, repeated typing, left-arrow interruption, uninterrupted output and mouse-selection interruption; Windows Notepad repeated typing, left-arrow interruption and uninterrupted output. The loaded repaired Chrome module and synchronous completion path were verified during the first-key retest. This closes the focused repair validation, not general compatibility or the entire TSF experiment. Do not use this experiment for real content or connect ASR. This is an independent C++ experiment, not a change to the WPF or Python clients.

### Keyboard Handoff Live Retest (2026-09-12)

- User reports `明天下午三233aaa` and confirms correct output after the requested first-key test. Record one user-confirmed character-order pass, not general Chrome compatibility or a separate caret-position observation.
- Read-only inspection confirms Chrome PID 41944 loads `build/keyboard-handoff-trace/VoiceInput.TsfProbe.dll`. Its activation marker is `preserve-key-ctrl-alt-j-keyboard-handoff-trace`; this is not the older cached DLL.
- The process log contains two simulations, each with five partial updates followed by `composition-ended` and `keyboard-handoff-complete`, without subsequent automatic frames in the inspected log. Neither takes the deferred fallback. Only the first simulation's detailed buffer has been flushed: 30 records, dropped=0. The user's reported output is not uniquely timestamp-correlated to either simulation.
- In the flushed trace, `request-keyboard-finish` is followed immediately by edit entry, end-before/after, successful request/session results and then key-exit with composing=0. End-after QPC 2043743633590 precedes key-exit 2043743640366 (frequency 10000000 Hz). This verifies completion-before-key-return on that run, unlike the old trace. It does not directly observe Chrome renderer character dispatch.
- Follow-up user feedback confirms all three requested Chrome regressions passed: two continuous `234dddf` typing interruptions preserve character order and stop automatic output; left-arrow interruption stops output; an uninterrupted run completes `明天下午四点检查 WebSocket connection。`. These are user-reported manual results, not newly inspected per-run traces or automated tests.
- Final follow-up user feedback confirms Chrome mouse-selection interruption (output stops, selection remains, no resumed output) and all three requested Windows Notepad regressions (repeated typing, left-arrow interruption and uninterrupted full output) have no issues. These are manual user-reported passes; no new per-run module inspection or trace verification was performed. Notepad mouse-selection behavior was not separately requested in this final round and is not newly verified.
- The focused repair's requested manual regression set is complete. Broader focus/prefix/reliability checks and the legacy direct activation harness failure remain outside this acceptance. No code, registration or build/test execution changed while recording this feedback; experimental registration is retained, not removed.

### First-Key Live Trace Results (2026-09-12)

- User reports the final text `明天下午三3aaa3` and the same displacement symptom. The exact intended keystroke sequence was not supplied for this run and is not recorded by diagnostics; do not infer it from the output alone.
- Module inspection confirms Chrome PID 26440 loads `build/handoff-trace/VoiceInput.TsfProbe.dll`. Its log contains the diagnostic activation marker and one simulation with five partial updates, a keyboard stop, composition completion, and later deactivation flushing 34 records (`dropped=0`, QPC frequency 10000000 Hz).
- The first keyboard record is `key-enter` (index 20), followed by the `test-enter` generated by our own OnKeyDown delegation. This is evidence of the actual key callback path; it must not be counted as proof of a separate preceding Windows OnTestKeyDown call. No `queue-input` cancellation appears in this captured run.
- Sequence: `request-finish` -> `session-result=0x00040300` -> `test-exit` -> `key-exit` with composition still active and an edit pending -> `edit-enter` -> `end-before` -> `end-after`. There are no additional simulated frames after interruption.
- QPC calculation: `(2030317380350 - 2030317371819) / 10000000 * 1000 = 0.8531 ms` from actual key callback exit to finish edit entry. This confirms the local completion request executes after the key callback has returned, not that Chrome had already inserted the character during that gap.
- Before/after completion snapshots are both `sel=5,0 comp=0,5` with successful reads. The interval between the two samples is 0.009 ms. Thus the recorded TSF selection itself does not move during this sampled EndComposition call. Chrome host/renderer-side character dispatch and later selection application remain outside this trace; a direct EndComposition caret reset is NOT established.
- Investigation result: cancellation works, but the code lacks a completion-before-key-return ordering guarantee. The asynchronous handoff is now observed rather than hypothetical; its causal link to the visible first-character displacement still needs a controlled behavioral test. Next should target only keyboard-triggered completion ordering, including pending edits and denied synchronous requests, while retaining ordinary async frames and pointer behavior. Do not add key replay or use later cursor movement as a workaround.
- No code, DLL or system registration changed in this evidence-reading step; no new build/test pass is claimed. Existing diagnostic registration is retained. The same diagnostic run need not be repeated without a new discriminating test.

### Minimal Keyboard Handoff Repair (2026-09-12)

- Implemented keyboard-only synchronous completion: after invalidating playback, key callbacks request `TF_ES_SYNC | TF_ES_READWRITE` on the matching context and return `eaten=FALSE` after the request completes. Reentrant frame writes and mismatched key targets do not take this synchronous path. No SetText, SetSelection, key swallowing or replay is added to completion.
- Older queued edit callbacks retain ownership of `edit_pending_`; the synchronous request does not clear that slot. This prevents a new simulation starting before the old callback retires. The canceled callback later clears its own slot and cannot update text or complete the old composition again after successful sync cleanup.
- If sync is refused, the outer request fails, or EndComposition fails, playback stays stopped and a single existing/queued async cleanup is retained or requested. `keyboard-handoff-deferred` reports this path. There is no retry loop or key swallowing. IMPORTANT: the deferred fallback retains the ordering risk and must not be reported as a successful first-key fix. A `keyboard-handoff-complete` record alone still needs visible browser validation.
- Ordinary frame requests, non-keyboard Stop and the message hook retain asynchronous behavior. A host path that only delivers the queued-message notification, without a matching TSF key callback before dispatch, is not guaranteed fixed by this change.
- Red/green evidence: a controllable ITfContext scheduler and ITfComposition test double first failed `keyboard composition ends before the actual key callback returns` against the old implementation; that assertion passes with the fix. The test exercises the production callback/request/completion code, but does not emulate Chrome's renderer or prove visible character ordering.
- Fresh validation: normal DLL compiled and 90 interruption checks passed; diagnostic DLL compiled and 101 interruption/diagnostic checks passed. Cases cover actual/test callbacks, an older queued frame, restart blocking until it retires, denied sync with/without a queued frame, outer request and EndComposition failures, no selection writes, different key targets and asynchronous non-keyboard cleanup. PowerShell management syntax passed. The old harness still gives 46 passes then `E_INVALIDARG` on direct activation; both complete build/test commands exit nonzero.
- Build outputs are now `build/keyboard-handoff/` and `build/keyboard-handoff-trace/` (`build.ps1 -HandoffTrace`). No old DLL was overwritten. Management requires `-KeyboardHandoff` to select the repaired build and also `-HandoffTrace` to select its diagnostic variant; omitting `-KeyboardHandoff` retains access to earlier registrations for cleanup.
- After separate user authorization on 2026-09-12, the older diagnostic build was unregistered with `-HandoffTrace`; COM/TIP/user-list entries were verified absent. Registration with `-KeyboardHandoff -HandoffTrace` then succeeded, with all three entries present and `RegisteredDll` exactly matching `build/keyboard-handoff-trace/VoiceInput.TsfProbe.dll`. The original Chinese input method remains listed and default override remains unset. No application was closed or DLL overwritten.
- Chrome PID 26440 still loads `build/handoff-trace/VoiceInput.TsfProbe.dll` after the switch. Registration does not replace modules already loaded in a running process. The user must save work and fully exit/reopen Chrome; verify the new module and `preserve-key-ctrl-alt-j-keyboard-handoff-trace` marker before interpreting results. No build/tests were rerun during this registration-only step.
- Pending live acceptance: after user-managed Chrome restart and module verification, repeat first-character typing in a disposable field, including `233aaa` and `234dddf`; require correct order, preserved prefix and no resumed output. Correlate `request-keyboard-finish`, end-before/after and key-exit so completion precedes key return. Also regress uninterrupted output, caret/selection interruption and Notepad. The repair is registered but not yet live-validated; do not repeat diagnostics on the old loaded DLL or claim the defect fixed.

### Diagnostic Registration Switch (2026-09-11)

- Following explicit user authorization, elevated unregister of `input-guard` completed; COM/TIP/user-list entries were independently verified absent before proceeding.
- Elevated `Register -HandoffTrace` then completed. Inspection confirms all three entries present and `RegisteredDll` exactly matches `build/handoff-trace/VoiceInput.TsfProbe.dll`. The original Chinese input method remains listed and default override remains unset.
- Chrome PID 37604 still loads `build/input-guard/VoiceInput.TsfProbe.dll`. The user must save work and fully exit/reopen Chrome before the single first-key diagnostic run. No user process was closed, default changed, DLL overwritten, or business code edited during this switch.
- Pending: verify the diagnostic module/marker after restart, reproduce the first-character displacement once, then switch to the ordinary input method to flush buffered diagnostics. Registration success is not evidence that Chrome has loaded the new DLL or that the typing defect is fixed.

### Chrome Fixed-Build Live Results (2026-09-11)

- User reports: scenario 1 (uninterrupted sentence), scenario 3 (left-arrow interruption), and scenario 4 (mouse-selection interruption) are normal. Scenario 2 FAILED: entering `233aaa` after `明天` leaves `明天33aaa2`; the caret appears before the first typed character. These are user-observed results, not automated browser tests.
- Read-only module inspection confirms Chrome PID 37604 loads `build/input-guard/VoiceInput.TsfProbe.dll`. Its log includes `preserve-key-ctrl-alt-j-input-guard`; this run is not the cached old build.
- The inspected log contains one full cycle, two `user-keyboard` interruptions, and two `host-terminated-composition` interruptions. No further `partial-applied` or `stable-applied` appears after an interruption within its cycle. Keyboard stops and `composition-ended` are logged at the same tick (122278187 and 122311484 respectively). Exact mapping of each cycle to a manual action is not logged.
- Source inspection: `OnTestKeyDown`/`OnKeyDown` return `eaten=FALSE`; `Stop` invalidates playback and requests asynchronous composition completion. The remaining hypothesis is an ordering/selection handoff problem between the first passed-through key and composition completion, not continued simulated output. Current logs cannot establish first-character dispatch versus completion ordering or the caret position at either boundary, so the root cause is not yet proven.
- Test gap: the 63 interruption checks use a private Win32 EDIT control with no live TSF composition. They verify message order and cancellation but do not exercise Chrome's completion/selection handoff. Add evidence for that boundary before treating those checks as protection against this reported failure.
- This result-recording step changed no business code, DLL, registration or running application. Next investigation is limited to first-key/composition completion ordering; no ASR or broader app support is authorized by these results.

### First-Key Handoff Investigation (2026-09-11)

- Installed Chrome version inspected: `152.0.7977.83`. Upstream Chromium `TSFTextStore` keeps text/selection caches and commits composition after its lock callback returns. Matching source for this exact installed version was not retrieved; upstream behavior is supporting context, not proof of this machine's callback order.
- Confirmed local scheduling choice: both key callbacks return `eaten=FALSE`; cancellation calls `Request(true)` using `TF_ES_ASYNC | TF_ES_READWRITE`. There is no guarantee in this code that composition ends before the passed-through key is handled. This is a concrete race candidate, not a confirmed explanation of the first-character displacement.
- Microsoft documents synchronous, asynchronous and async-dont-care edit requests separately. Its SampleIME invokes an edit handler from some `OnTestKeyDown` paths and uses `TF_ES_ASYNCDONTCARE`, so requesting an edit there is not by itself evidence of invalid API use. Do not blindly replace all requests with synchronous ones; failure and pending-frame ordering would need separate validation.
- Added opt-in `-HandoffTrace` build under `build/handoff-trace/`. The normal build has tracing disabled. Request flags, cancellation, key passthrough, composition completion and text/selection writes are unchanged. No global hook, key replay, provider or app support was added.
- Diagnostic records contain QPC timestamps/frequency, stage, generation, frame, running/pending/composing flags, HRESULTs, and selection/composition ACP start/length around `EndComposition` when supported. They contain no virtual keys, character values or target text. Unknown/unsupported extents remain `-1`; HRESULT on a selection record describes that read, not visible host application completion.
- Storage is bounded to 128 entries per simulation with an explicit dropped count; capture ends at most 500 ms after cancellation/finalization. Extra records are buffered in memory and flushed to the existing process log at the next accepted simulation, service deactivation or destruction. Use recorded QPC values for ordering, not the later file-write tick. New read-only selection queries can affect scheduling slightly; this is a diagnostic build, not latency evidence.
- Diagnostics distinguish `test-enter/exit`, `key-enter/exit`, `queue-input`, request/return/session-result, edit entry, end-before/after and host termination. They cannot directly observe Chrome renderer key/character dispatch or the visible caret after it; a normal TSF snapshot does not rule out a later host-side selection reset.
- Verification: diagnostic DLL and test executables compile with warnings treated as errors; 74 interruption/diagnostic checks pass. Without tracing, 63 interruption checks pass from a separate test output directory. PowerShell build/management syntax checks pass. The unchanged old suite still reports 46 passes and the direct activation `E_INVALIDARG` failure; the full build/test command remains nonzero. These are not live diagnostic Chrome results.
- Registration was NOT changed. Read-only `Inspect -HandoffTrace` reports the diagnostic DLL exists, while `RegisteredDll` remains `build/input-guard/VoiceInput.TsfProbe.dll`. Existing COM/TIP/user-list entries remain present. No loaded DLL was overwritten or user process closed.
- Pending, after explicit system-change approval: unregister the currently registered input-guard build, register the diagnostic build using `manage.ps1 -Action Register -HandoffTrace -ConfirmSystemChange`, and let the user save work/restart Chrome. Verify the loaded module and `preserve-key-ctrl-alt-j-handoff-trace` marker before interpreting logs. Do not use the diagnostic switch to unregister the old build: unregister checks module identity.
- One focused manual run is sufficient initially: trigger Ctrl+Alt+J in a disposable field, type `2` after the first few simulated characters, pause briefly and observe caret position, then type `33aaa`; do not submit. Switch back to the ordinary input method afterward to flush diagnostics. Compare key-exit, session-result and end-before/after ordering, and correlate the snapshot with the user's caret observation. Do not mark the fault fixed from logging alone.
- References: [Microsoft RequestEditSession](https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfcontext-requesteditsession), [SampleIME key callbacks](https://raw.githubusercontent.com/microsoft/Windows-classic-samples/main/Samples/IME/cpp/SampleIME/KeyEventSink.cpp), [SampleIME request scheduling](https://raw.githubusercontent.com/microsoft/Windows-classic-samples/main/Samples/IME/cpp/SampleIME/KeyHandler.cpp), [Chromium TSF implementation](https://raw.githubusercontent.com/chromium/chromium/main/ui/base/ime/win/tsf_text_store.cc).

### Fixed Build Registration for Retest (2026-09-11)

- User explicitly authorized temporary registration of the fixed build. Elevated management exited 0; independent inspection confirmed `ComRegistered=True`, `TipRegistered=True`, `UserListed=True` and the actual COM server path `prototypes/tsf_input/build/input-guard/VoiceInput.TsfProbe.dll`.
- The original Chinese input method remains listed and default-input override remains unset. No source code, default input method or application allowlist was changed in this step.
- Module inspection found Chrome PID 13376 still loading `build/hotkey-j/VoiceInput.TsfProbe.dll`. Registry success does not replace a DLL already loaded by a host. The user must save work and fully exit/reopen Chrome before retesting; a new tab alone is insufficient. No user process was closed automatically.
- Registration is complete, but loading the fixed build in Chrome and live interruption safety remain unverified. Keep tests limited to disposable fields and the existing Ctrl+Alt+J simulation; no microphone is involved.

### Chrome Cancellation Fix, Live Retest Pending (2026-09-11)

- Confirmed code defect: `OnKeyDown` did not cancel playback when invoked without `OnTestKeyDown`. A regression first failed against that implementation and passes after both callbacks share cancellation. This does not establish which callback Chrome delivered in the user's failed run.
- During simulated playback only, a current-thread `WH_GETMESSAGE` hook invalidates the playback generation before a queued keyboard/character or mouse-button/wheel message reaches host dispatch. It leaves the message untouched and always calls the next hook. Peek-only reads, key releases, mouse hover and timer messages do not cancel playback.
- The hook performs no TSF text edits: composition completion is posted back to the service window. Pending edits reject the canceled generation; generation checks immediately before text/selection mutation also cover reentrant cancellation. Finalization, host termination and deactivation remove the hook. A failed hook installation stops the experiment rather than proceeding unguarded.
- No global/low-level keyboard hook, key capture log, key swallowing or replay is added. The process allowlist and simulated frames are unchanged. Current-thread message interception does not prove coverage of every browser input path; live Chrome typing, caret movement and selection remain required.
- Fresh build result with portable Zig 0.14.1: DLL and both test executables compiled with warnings treated as errors. The new interruption executable passed 63 checks. These include actual key callbacks, native message delivery, cancellation before dispatch, stale generations/cleanup, hook cleanup, and `234dddf` retaining order after `prefix:` in a private hidden Win32 EDIT control. This is not a Chrome composition test. Pointer-down classification alone is tested; fabricated pointer messages were not delivered by Windows, so real touch input is unverified.
- Existing executable: 46 checks pass, followed by the unchanged direct activation failure `E_INVALIDARG` at `AdviseKeyEventSink`. The complete build/test script exits nonzero. The failure is not suppressed and is separate from the observed Windows-hosted activation evidence.
- The new build uses `prototypes/tsf_input/build/input-guard/`; `manage.ps1` now targets this path for any later approved registration. Loaded older DLLs were not overwritten and no host applications were closed. A log marker `preserve-key-ctrl-alt-j-input-guard` identifies the new version.
- Authorized cleanup completed before building: `ComRegistered=False`, `TipRegistered=False`, `UserListed=False`; the original input method remained listed and default override remained unset. No re-registration was performed. Cached old DLLs can remain loaded until their hosts release them.
- After separate registration approval, repeat disposable Chrome search-field runs: uninterrupted output; type `234dddf` midway; move the caret midway; select text midway. Each interrupted run must stop further frames, preserve visible/user text and retain input order/caret choice. Repeat the interruption checks in Notepad as regressions. No speech is needed. Do not use real work, passwords or submit the generated text.
- References inspected: [Microsoft GetMsgProc](https://learn.microsoft.com/en-us/windows/win32/winmsg/getmsgproc), [SetWindowsHookExW](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowshookexw), and [Chromium TSF text store](https://raw.githubusercontent.com/chromium/chromium/main/ui/base/ime/win/tsf_text_store.cc). Chromium's cached text/selection and key-dispatch paths motivate pre-dispatch cancellation, but upstream source is not a trace of the installed browser.

### Browser and Chat App Failures Before Fix (2026-09-11)

- User report: the browser search field outputs the full simulated sentence correctly when undisturbed. Typing during output does not stop it; entered `234dddf` appears as `fddd432` after the complete sentence. Moving the caret also fails to stop output. Selecting text does stop further output. These are separate checks: selection stopping does NOT imply general interruption safety.
- Correlated local evidence: PID 13376 is `chrome.exe`. Its log records three complete simulation cycles and a later cycle ending with `host-terminated-composition`. There is no `user-keyboard` or `external-text-or-selection-change` stop event in the inspected cycles. Logs do not include keystrokes/selection snapshots, so they do not establish exact action-to-event timing or explain the character reversal by themselves.
- Source inspection: keyboard cancellation exists only in `OnTestKeyDown`; `OnKeyDown` returns without cancelling. Each frame checks TSF text/selection then calls `SetSelection` to put the caret at the composition end. Callback coverage and host selection/state timing are investigation targets, not yet confirmed root causes. Do not treat a successful `SetText` HRESULT as proof that concurrent user input is safe.
- User report: WeChat cannot retain the selected experiment input method and provides no experiment output. Activation/switching cause remains unknown. WeChat is outside the current Notepad/Chrome/Edge allowlist; its input-method switching issue must not be attributed to the write allowlist without evidence.
- User report: DingTalk permits input-method selection but no output follows the shortcut. PID 18284 is `DingTalk.exe`; logs confirm successful service/key registration followed by `target-not-in-experiment-allowlist` and `preserved-key` returning `S_FALSE`. This attempt was explicitly rejected by the experiment's scope guard. Actual DingTalk text compatibility has NOT been tested.
- Priority: diagnose and fix Chrome cancellation and text-order preservation before any broader application support or ASR integration. Retain the Notepad checks as regressions. No business code or allowlist was modified in this investigation.
- Earlier cleanup attempt: elevated unregister was rejected by action review for lack of fresh authorization. Inspection at that time found COM/TIP/user-list entries present. The later explicit authorization and verified successful cleanup supersede that state, as recorded above.

### Notepad Interruption Feedback (2026-09-11)

- Evidence: user manual test report, not an automated verification or a new log inspection.
- The user first confirmed that the complete output sentence was correct, explicitly separating that observation from process/interruption tests.
- In three subsequent independent runs, the user tested keyboard typing, moving the caret, and selecting text with the mouse. All three interrupted the simulated output and it did not continue automatically.
- All three Notepad scenarios have user evidence for stop-on-interruption behavior. The user additionally clarified that the first keyboard-interruption run already verified both typed characters and previously displayed text were retained; no repeat test is needed for that check. Exact interim revision, dedicated prefix protection, text preservation for the other interruption modes, target/app switching, browser behavior, and repeated-cycle reliability remain unverified.
- On 2026-09-10, user-requested registration was verified with COM/TIP/UserListed all true, the hotkey-J DLL path, original input methods retained and default override unchanged. It was retained for those manual tests and removed after fresh authorization on 2026-09-11.

### Confirmed Notepad Result

- On 2026-09-08 the user reported visible text after using Ctrl+Alt+J in Windows Notepad. Process 29992 was verified as `Notepad.exe`.
- Its `voice-input-tsf-29992.log` records the new hotkey registration, successful service activation, `simulation-start`, 12 `partial-applied` events, one `stable-applied`, and one `composition-ended`. All return `S_OK`. This confirms a complete successful composition-update cycle, combined with the user's visible-output observation.
- Simulation started at monotonic tick 352581875 and ended at 352586187 (4312 ms). This reflects the fixed 350 ms simulated-update schedule, NOT an ASR or microphone latency measurement. No microphone/provider was connected.
- The user did not separately verify the exact three-to-four revision, absence of duplicated text, prefix protection, visual update cadence, or interruptions. Those checks remain pending alongside browser input compatibility and repeated start/stop.
- After this Notepad test, authorized unregister completed. `Inspect` returned `ComRegistered=False`, `TipRegistered=False`, and `UserListed=False`. The original input-method list remained intact and default-input override remained unset. Source/build files and test text were not deleted; no user applications were closed. Already loaded DLLs can remain cached until their host releases them.

### Current Hotkey Retest

- User confirmed the profile is visible, but the F9 key requires Fn and the attempted test produced no text. A host log contains successful activation/key subscription followed by `target-not-in-experiment-allowlist` and `preserved-key` returning `S_FALSE`. The logged live process was `notepad++.exe`, not Windows `notepad.exe`. No composition was started by that attempt. Notepad++ is not added to the experiment's allowlist.
- The trigger is now **Ctrl+Alt+J**. Registration and keyboard interruption detection use the same key definition. The new `preserve-key-ctrl-alt-j` log label distinguishes this version from cached older DLLs.
- Regression evidence: with the old F9 definition the new key test failed; after changing it to J, both key checks pass. The new DLL/test executable compile; 46 checks pass and the existing direct harness activation check still fails. The complete build/test command remains nonzero. Successful Windows-hosted activation is evidenced independently by live logs, not by suppressing that failing assertion.
- Old COM/TIP/user-list registration was removed and inspected as absent. Several apps still cached the old DLL, so the new build lives in `prototypes/tsf_input/build/hotkey-j/`; no loaded DLL was overwritten and no user application was terminated. Registration now points to that directory. COM/TIP presence and user enrollment were verified; original input methods and default override are unchanged.
- Windows Notepad was launched and its `Notepad.exe` process verified. The subsequent basic output test and final unregister succeeded as recorded above; detailed revision/interruption and browser checks remain pending.

### Earlier Registration Checks

- Build tool: portable Zig 0.14.1, x86_64-windows-gnu, C++17, warnings treated as errors. No global compiler/PATH installation was made.
- `prototypes/tsf_input/build.ps1 -Zig <path-to-zig.exe>` compiles the DLL and runs checks without registering it.
- Actual result: DLL and test executable compiled; 44 checks passed and one activation check failed. The overall build-and-test command exits nonzero. It is NOT a passing suite.
- Passing checks cover simulated range ownership/revision, one final commit, cancellation generations, COM exports/identity, and cleanup/unload after activation failure. They do not prove actual Windows text mutation safety.
- Direct activation using a local TSF thread manager fails at `AdviseKeyEventSink` with `E_INVALIDARG` (`0x80070057`). Both the application's client ID and an ID obtained using `ITfClientId::GetClientId` were tried. Manager/thread-focus subscriptions succeed. Root cause remains unconfirmed; normal Windows-hosted activation is still pending.
- User authorized temporary registration, disposable Notepad/browser testing, and subsequent unregister. Registration succeeded via elevated PowerShell on 2026-09-08; `Inspect` reported both COM and TIP registered and the registration log returned `S_OK`.
- Re-running the local test executable after registration still gives 44 passes and one `AdviseKeyEventSink` activation failure (`E_INVALIDARG`). Registration alone did not resolve direct harness activation; actual Windows-hosted activation remains a separate pending test.
- Desktop automation initialization failed twice (`trusted Node process exited unexpectedly`). The user was asked to select the profile manually in blank Notepad and trigger `Ctrl+Alt+F9`. Registration is temporarily retained awaiting this manual result; unregister and post-cleanup inspection are still outstanding. No real-target success is claimed.
- User reported that `Voice Input TSF Probe` was absent from Win+Space. Inspection found the COM/profile entries and HKCU `Enable=1`, but no experiment entry in `Get-WinUserLanguageList.InputMethodTips`. This is a distinct omission from the earlier registration check.
- Management now calls Microsoft's `InstallLayoutOrTip` with flags `0` to add only this experiment to the current user's input methods; `Unregister` removes that user entry with `ILOT_UNINSTALL=1` before unregistering the DLL. Neither default-profile nor clean-install flags are used. `Enable` repairs an already registered experiment without rebuilding a loaded DLL.
- Actual repair verification: PowerShell syntax validation passed, the native API returned success, and `Inspect.UserListed` changed from false to true. Before/after input-method lists show exactly the experiment was added; original profile retained, default-input override unchanged. This is not yet evidence that the Win+Space UI displays it or that target composition works. A repeat manual selection test is pending.
- Official registration reference: [IME installation](https://learn.microsoft.com/en-us/windows/apps/develop/input/input-method-editor-requirements#ime-installation); [InstallLayoutOrTip flags](https://learn.microsoft.com/en-us/windows/win32/tsf/installlayoutortip). A registered/enabled TSF profile is not sufficient evidence of inclusion in the user's input-method list.

### Registration Boundary

`prototypes/tsf_input/manage.ps1 -Action Inspect` is read-only. Register/Unregister require separate user approval, elevated x64 PowerShell, and `-ConfirmSystemChange`.

`Enable -ConfirmSystemChange` performs only current-user enrollment for an existing registered profile and does not require elevation. Run it as the test user. `Inspect` now reports `UserListed` as well as COM/TIP registry presence. The first test's enrollment and cleanup completed on 2026-09-08; user-requested re-registration completed on 2026-09-10 for the ongoing tests.

After approval only:

```powershell
.\prototypes\tsf_input\manage.ps1 -Action Register -ConfirmSystemChange
# After testing, switch back to the original input method before unregistering.
.\prototypes\tsf_input\manage.ps1 -Action Unregister -ConfirmSystemChange
.\prototypes\tsf_input\manage.ps1 -Action Inspect
```

Registration adds the experiment's x64 COM class and Chinese Simplified TSF profile/category. CLSID: `{876623A8-16EF-4834-A525-7AB17EA09C83}`. No default input source is set. Successful registration and explicit unregister have been verified; rollback after partial registration failure remains unverified. Inspect results instead of assuming cleanup succeeded. Do not relocate/rebuild the registered DLL while host applications have loaded it. Native code runs inside target applications and may crash them; use only disposable documents.

### Pending Live Checks

1. Manually select `Voice Input TSF Probe` with the Windows input switcher. Use x64 Notepad or Chrome/Edge and a disposable text field, not real work or password fields.
2. Place an empty selection after an existing prefix. Press `Ctrl+Alt+J`. Fixed snapshots update every 350 ms, revise a simulated time from three to four, and finish with Chinese/English text and final punctuation. Confirm the prefix survives and final text is not duplicated. Use Windows Notepad, not Notepad++.
3. Repeat while typing, moving the caret, selecting text, changing input fields, and switching applications. Verify pending updates stop and existing/user text survives. The interruption policy retains visible composition text; it does not provide Undo.
4. Repeat start/stop and switch back to the original input method. Check that normal input remains usable.
5. Record Notepad and browser results separately before connecting ASR. No microphone or provider is involved, so this test measures neither recognition accuracy nor real ASR latency.

Diagnostics: `%TEMP%\voice-input-tsf-<pid>.log` and `OutputDebugString` contain lifecycle actions, process/thread IDs, monotonic ticks and HRESULTs, not transcript text. Composition styling is host-dependent; a custom display-attribute provider is not implemented. Only `notepad.exe`, `chrome.exe`, and `msedge.exe` are allowed to start this experiment.

Core metrics to reserve for future validation:

- Audio -> first transcript latency
- Audio -> stable transcript latency
- continuous transcription lag
- edit command latency
- cleanup latency
- command false trigger rate
- transcription error rate
- crash / unexpected termination
- foreground app compatibility

## Streaming ASR Prototype Test Record

Date: 2026-09-05

Prototype path: `prototypes/streaming_asr/`

Scope validated in this round:

- Python CLI skeleton for Streaming ASR latency validation
- environment-variable API key loading through `ASR_API_KEY` or `OPENAI_API_KEY`
- realtime WebSocket client boundary
- streaming audio chunk sender
- partial transcript event normalization
- stable/final transcript event normalization
- latency metric aggregation
- safe CLI help path
- clear missing API key error path

Automated validation run:

```text
python -m unittest discover -s tests
.......
----------------------------------------------------------------------
Ran 7 tests in 0.001s

OK
```

CLI validation run:

```text
python main.py --help
usage: main.py [-h] [--duration DURATION] [--device DEVICE]

Streaming ASR latency prototype. Press Ctrl+C to stop safely.
```

Missing API key validation run:

```text
python main.py --duration 1
[config-error] Missing ASR_API_KEY or OPENAI_API_KEY. Set one environment variable before running the prototype.
```

Environment readiness check:

```text
sounddevice=False
websockets=True
ASR_API_KEY=False
OPENAI_API_KEY=False
```

Live microphone -> realtime ASR latency was not measured in this round because the current environment does not have `sounddevice` installed and does not expose `ASR_API_KEY` or `OPENAI_API_KEY`.

Therefore the following metrics are not yet available as real measurements:

- first transcript latency
- stable transcript latency
- continuous lag observation from live speech
- reconnect behavior under real network interruption

Do not use this record as evidence that realtime latency is acceptable. It only proves the local prototype code path and metric collection structure are ready for live validation after dependencies and API credentials are provided.

## Manual Streaming ASR Test Scenarios

Run from `prototypes/streaming_asr/` after installing dependencies and setting `ASR_API_KEY` or `OPENAI_API_KEY`:

```text
pip install -r requirements.txt
python main.py --duration 120
```

Required manual scenarios:

- single Chinese sentence
- continuous 30-second Chinese speech
- continuous 2-minute Chinese speech
- mixed Chinese and English speech
- fast speech
- pause in the middle, then continue
- multiple start / stop runs
- brief network interruption

For each manual run, record:

- recording start
- first audio chunk sent
- first partial transcript
- first stable transcript
- each transcript update count
- session duration
- first transcript latency
- stable transcript latency
- continuous lag max update gap
- continuous lag average update gap
- dropped audio chunks

## Bottleneck Analysis Rules

If measured latency is not acceptable, do not continue to Document Engine, Windows text injection, AI cleanup, Voice Edit, Voice Send, backend, license, or installer work.

Analyze these first:

- audio chunk size: compare `ASR_CHUNK_MS=40`, `ASR_CHUNK_MS=80`, `ASR_CHUNK_MS=100`, and `ASR_CHUNK_MS=160`
- sample rate: confirm the microphone and provider agree on `ASR_SAMPLE_RATE`
- provider/model: compare available realtime ASR models before changing product architecture
- network: inspect connection setup time, packet loss, and reconnect behavior
- client processing: check audio queue depth, dropped chunks, stdout rendering, and transcript event handling
- VAD settings: `ASR_VAD_THRESHOLD`, `ASR_PREFIX_PADDING_MS`, and `ASR_SILENCE_DURATION_MS` affect the OpenAI provider only; they are not sent to Fun-ASR.

## Fun-ASR First Partial Measurement

Updated: 2026-09-05. Scope remains Streaming ASR latency only.

User-reported live baseline (same standard sentence, four runs; exact run dates and sentence text not supplied):

| Run | Recording -> first partial (ms) | First send -> first partial (ms, approximate) | Recording -> first send (ms, derived approximate) |
| --- | ---: | ---: | ---: |
| 1 | 2359 | 1812 | 547 |
| 2 | 875 | 594 | 281 |
| 3 | 1890 | 1578 | 312 |
| 4 | 1859 | 1546 | 313 |

User also reports continuous transcript update intervals of about 750-850 ms and stable live operation. These are user observations, not independently reproduced results of the enhanced measurement code. The four-run recording-to-partial median is 1874.5 ms. This small sample does not establish a provider latency distribution.

### Measurement Definitions

- Existing `recording_start` stays immediately before microphone startup, after connection/task setup. It includes device startup and time before speaking, but excludes WebSocket handshake and `task-started` waiting. The old latency metric retains this baseline.
- `speech_start_timestamp` is a local energy-onset estimate, not linguistic ground truth. PCM16 RMS is checked in approximately 10 ms windows; 30 ms of consecutive above-threshold audio confirms onset, backdated to the first qualifying window. Detection continues across transport chunks and stops after the first confirmed onset.
- PCM16 samples are interpreted as signed little-endian integers and divided by 32768, so the detector threshold and measured RMS use the same normalized full-scale unit. The previous threshold 0.02 equals raw RMS about 655 (-34 dBFS) and can miss speech from a low-gain microphone. The default is now 0.005, raw RMS about 164 (-46 dBFS), with the same 30 ms confirmation. Use `--debug-audio-rms` to print one end-of-session line containing below-threshold (silence candidate) and above-threshold (speech candidate) RMS ranges. If room noise crosses 0.005, raise the threshold rather than accepting false speech starts. Threshold calibration changes timing only; all audio remains unmodified.
- Audio timestamps use `monotonic_at_callback - (currentTime - inputBufferAdcTime)` plus sample offsets. PortAudio clocks are never directly subtracted from Python monotonic timestamps. Mapping still has callback/driver timing uncertainty. Missing or invalid ADC timing falls back to callback time minus block duration and increments `audio_timestamp_fallback_count`.
- The meter runs outside the audio callback. It does not gate, trim, accumulate, or suppress outgoing audio, including silence. The 30 ms confirmation period delays availability of the measurement only, not transmission. No VAD model or additional dependency is introduced.
- `speech_start_to_first_partial_ms`: first partial event timestamp minus local speech onset.
- `first_audio_sent_to_first_partial_ms`: first partial event timestamp minus completion of the first audio `send()` call. Send completion is local transport acceptance, not acknowledgement of server receipt; this interval can still include silence before speaking.
- `recording_start_to_speech_start_ms`: local speech onset minus the existing recording start; includes startup as well as pre-speech waiting.
- `transcript_update_interval_median_ms` / `transcript_update_interval_max_ms`: gaps between consecutive recorded partial and stable updates, including revisions and speaking pauses. Existing `continuous_lag_*` outputs remain for compatibility. These are update intervals, not audio-to-text lag; they do not prove continuous lag of 750-850 ms.
- Missing onset or partial produces `None`, not zero. A partial earlier than detected speech or a noise-triggered onset makes that trial unsuitable for speech-relative comparisons; do not clamp negative values to zero.
- Partial timing is the local transcript event timing before terminal output, not a screen-paint measurement. Keep `ASR_DEBUG_RAW_EVENTS=0` for latency comparisons because synchronous debug output can perturb processing/display.

### Client Buffering Diagnostics

Default PCM is mono, 16-bit, 16000 Hz. The microphone requests `floor(sample_rate * chunk_ms / 1000)` frames per callback. Each block is sent as one binary WebSocket message, with no added sleep, batching or pre-speech gate. Startup prints actual requested frames, bytes and duration. `audio_chunk_actual_ms_*` also measures the delivered bytes at runtime.

| `ASR_CHUNK_MS` | Samples at 16 kHz | PCM bytes | Audio duration | Expected sends/s |
| ---: | ---: | ---: | ---: | ---: |
| 100 | 1600 | 3200 | 100 ms | 10 |
| 80 | 1280 | 2560 | 80 ms | 12.5 |
| 40 | 640 | 1280 | 40 ms | 25 |

- `input_device_latency_ms`: actual latency reported by the opened PortAudio stream. Existing behavior uses sounddevice's device-default high latency. `--input-latency low` requests the low-latency setting; the driver determines the actual value.
- `first_audio_capture_timestamp`, `first_audio_callback_timestamp`, and the existing first-send timestamp separate first-block capture, delivery and sending.
- `capture_to_callback_ms_*`: first sample to callback, including block accumulation and driver buffering. Do not add the whole device-latency figure again to this measured interval.
- `audio_queue_wait_ms_*`: callback to dequeue, including handoff/scheduling. `audio_queue_depth_max` samples the remaining backlog after dequeue; it is not a continuous peak-depth trace.
- `websocket_send_duration_ms_*`: time spent in successful audio `send()` calls. `audio_send_interval_ms_*`: start-to-start send cadence. Both report median, max and sample count. A failed send is not counted as successfully sent audio.
- `ASR_MAX_QUEUE_CHUNKS=200` permits up to 20 seconds at the default 100 ms. Capacity is not a prefill delay. Lowering capacity cannot accelerate a healthy empty queue and can lose speech during congestion; leave it fixed and monitor queue age, `[audio-warning]` and `dropped_audio_chunks`.

The remaining speech-to-partial delay combines audio accumulation, device delivery, client scheduling, transport and network travel, server buffering/VAD/decoding/emission, and local event handling. These measurements can isolate local stages; they cannot separately measure network versus server computation without additional provider-side timing evidence. The current 594-1812 ms first-send-to-partial range alone does not establish a server bottleneck.

### Next Controlled Test: Provider A/B

Compare the unchanged Fun-ASR Realtime provider with Paraformer Realtime v2. Keep the microphone/device, sentence, room and debug setting identical. Both provider configurations are fixed to 16 kHz, mono, signed PCM16 and 100 ms audio chunks; `ASR_SAMPLE_RATE`, `ASR_CHUNK_MS`, and `ASR_AUDIO_FORMAT` do not override the Paraformer A/B format.

Run from `prototypes/streaming_asr/` with `DASHSCOPE_API_KEY` already set:

```powershell
$env:ASR_PROVIDER = "dashscope_fun_asr_realtime"
python main.py --duration 10

$env:ASR_PROVIDER = "dashscope_paraformer_realtime"
python main.py --duration 10
```

Interleave at least 10 runs per provider instead of completing all runs for one provider first. Compare only:

- `first_audio_sent_to_first_partial_ms`
- `transcript_update_interval_median_ms`
- `transcript_update_interval_max_ms`
- `dropped_audio_chunks`
- actual recognition accuracy observed against the same spoken script

Do not use `speech_start_timestamp` or `speech_start_to_first_partial_ms` to rank providers because the current local onset detector can misclassify speech. Do not tune endpointing, semantic punctuation or device latency during provider comparison.

### Paraformer 100 ms / 50 ms Chunk A/B

Paraformer Realtime v2 accepts only `ASR_CHUNK_MS=100` or `ASR_CHUNK_MS=50` for this controlled test. It remains fixed at 16 kHz, mono, signed PCM16; other chunk values fail during configuration loading. Fun-ASR and the Paraformer protocol implementation are unchanged.

Use the same spoken sentence and microphone conditions for five interleaved runs per setting:

```powershell
$env:ASR_PROVIDER = "dashscope_paraformer_realtime"
$env:ASR_CHUNK_MS = "100"
python main.py --duration 10

$env:ASR_CHUNK_MS = "50"
python main.py --duration 10
```

Record these outputs for every run:

- `first_audio_sent_to_first_partial_ms`
- `transcript_update_interval_median_ms`
- `transcript_update_interval_max_ms`
- `dropped_audio_chunks`
- `audio_queue_wait_ms_median` and `audio_queue_wait_ms_max`
- `websocket_send_duration_ms_median` and `websocket_send_duration_ms_max`
- final recognized text and observed recognition accuracy

Interleave settings as 100, 50, 100, 50 until each has five runs. Keep 100 ms as the default unless 50 ms shows a clear first-partial or update-cadence improvement without worse dropped chunks, queue/send behavior, or recognition accuracy.

Live 5 x 2 results are pending. The current agent process has a usable microphone but no `DASHSCOPE_API_KEY` or `ASR_API_KEY`; no synthetic or silent measurements are recorded as provider evidence.

### Fun-ASR Parameter Review

The Fun-ASR request supplies `format`, `sample_rate`, one `language_hints` value, and explicitly sets `semantic_punctuation_enabled=false`. The explicit value matches the documented default and prevents semantic endpointing from becoming an uncontrolled variable; it does not change the existing 100 ms send path.

Official defaults: `semantic_punctuation_enabled=false`, `max_sentence_silence=1300` ms, and `multi_threshold_mode_enabled=false`. These control sentence boundaries; reducing endpoint silence is not evidence of a first-partial improvement and can damage segmentation. Keep them unchanged for A-D. [Client events](https://help.aliyun.com/zh/model-studio/fun-asr-client-events)

The SDK recommends approximately 100 ms packets, 1-16 KB. It lists `punctuation_prediction_enabled=true` as non-configurable, so disabling punctuation is not a supported latency experiment. [Python SDK](https://help.aliyun.com/zh/model-studio/fun-asr-realtime-python-sdk)

`speech_noise_threshold` is an advanced server VAD sensitivity control in [-1, 1]; negative changes accept more noise and positive changes can suppress speech. Only consider small isolated sensitivity trials after A-D if onset evidence points there. It is not the local RMS threshold and is not exposed as a new environment variable here. There is no verified first-partial benefit yet.

Fun-ASR now defaults to `language_hints=["zh"]`. The provider documentation lists `zh`/`en` rather than `zh-cn`/`en-us`, and Fun-ASR uses only the first hint. For compatibility, configured `zh-cn` and `en-us` are normalized to `zh` and `en`, and additional values are not sent. `ASR_LANGUAGES=zh` is the explicit Chinese-preference configuration. This addresses the invalid/ambiguous previous `zh-cn,en` request that could contribute to an English opening such as `This is a`; no transcript post-processing removes English text. Mixed-language recognition must be rechecked because a Chinese hint may bias language selection. [Client events](https://help.aliyun.com/zh/model-studio/fun-asr-client-events)

### Partial Cadence Investigation

Reported live output after the first instrumentation update: `first_audio_sent_to_first_partial_ms=609`, transcript update median 2179.5 ms, and maximum 2250 ms. Source code and regression tests confirm that every valid `result-generated` event with `sentence_end=false` becomes a `PartialTranscript` immediately. The client does not debounce, merge, deduplicate, or wait for punctuation. It only excludes empty, whitespace-only, or punctuation-only invalid text.

Set `ASR_DEBUG_RAW_EVENTS=1` for one diagnostic run. Each raw `result-generated` line now includes local `received_at` and `arrival_gap_ms` before normalization:

```powershell
$env:ASR_DEBUG_RAW_EVENTS = "1"
python main.py --duration 10 --debug-audio-rms
```

Interpret the result as follows:

- Raw `arrival_gap_ms` near 2180 ms with one displayed update per raw event: provider/model emission cadence for that run.
- Raw events arriving more frequently while displayed valid partials remain near 2180 ms: inspect whether the intervening payloads are empty or punctuation-only; those are the only client-side filtered result events.
- Audio send interval near 100 ms, low queue wait, no dropped chunks, but raw result events near 2180 ms: audio capture, buffering and send cadence are not the limiting stage.

No current Fun-ASR parameter is documented as a direct partial-emission interval control. `max_sentence_silence` and semantic punctuation govern sentence end/stable behavior, so they remain unchanged for this investigation. Keep `ASR_CHUNK_MS=100`; the planned 100 ms versus 50 ms A/B starts only after the raw event cadence and RMS output are captured.

Sounddevice documents the default high/optional low latency tradeoff and the callback ADC timestamps used above. [sounddevice 0.4.6 streams](https://python-sounddevice.readthedocs.io/en/0.4.6/api/streams.html)

### Automated Measurement Validation

- Python 3.12, pinned websockets 12.0: 53 unit tests passed. Coverage includes low-gain speech onset, normalized RMS debug ranges, onset across chunk boundaries, silence/impulse rejection, ADC clock conversion/fallback, no audio gating, one-to-one valid partial delivery, raw event arrival gaps, Fun-ASR Chinese language hints, Paraformer v2 request/event normalization and connection lifecycle, Paraformer 50/100 ms chunk validation, successful-send-only metrics, device latency hints and CLI output.
- `py_compile` and `main.py --help` passed. `main.py --duration 10` was attempted but returned a missing `DASHSCOPE_API_KEY` / `ASR_API_KEY` configuration error in the agent's process. No new live first-partial improvement is claimed.
- Offline detector benchmark: 120 seconds of synthetic silence (1200 x 100 ms), median processing 0.063 ms per block, observed maximum 0.3005 ms on this development machine. This is a CPU-cost check, not a two-minute live ASR result or a general performance guarantee.

## Windows Desktop Shell Validation

Date: 2026-09-05

Project path: `src/desktop/VoiceInput.sln`

Implemented validation scope:

- .NET 10 WPF background application startup
- tray-shell lifetime
- configurable global toggle hotkey
- 16 kHz mono PCM16 capture configuration with exact 100 ms / 3200-byte chunks
- provider-neutral Paraformer partial/stable/error/connection events
- non-activating HUD behavior
- foreground-window session target capture
- stable-only Clipboard + Paste injection boundary
- target-change rejection and clipboard restoration on paste failure

Automated test result:

```text
Tests run: 9; failures: 0
```

Build result:

```text
VoiceInput.Desktop -> bin/Release/net10.0-windows/VoiceInput.Desktop.dll
Build succeeded.
0 warnings
0 errors
```

Runtime smoke evidence:

- The generated WinExe started without a main window and remained running in the background.
- Before the configured hotkey, the process had no visible window.
- A synthesized `Ctrl+Alt+Space` created one visible non-taskbar HUD titled `Voice Input`; the process remained running.
- The smoke process was stopped after validation.
- ASR was not started because the agent process had no `DASHSCOPE_API_KEY` or `ASR_API_KEY`.

Local run:

```powershell
$env:DASHSCOPE_API_KEY = "<set locally>"
$env:VOICE_INPUT_HOTKEY = "Ctrl+Alt+Space"
dotnet run --project src/desktop/VoiceInput.Desktop/VoiceInput.Desktop.csproj
```

Manual compatibility status:

| Target | Status | Evidence |
| --- | --- | --- |
| Notepad | Pending | Live stable transcript injection not run |
| Chrome / browser input | Pending | Live stable transcript injection not run |
| ChatGPT input | Pending | Live stable transcript injection not run |
| Cursor or VS Code | Pending | Live stable transcript injection not run |
| Chat application | Pending | Live stable transcript injection not run |

Known validation limits:

- Automated tests cover provider-neutral parsing and injection safety policy, not live cloud recognition.
- Clipboard restoration is best effort because another process may take clipboard ownership during the paste window.
- The target window must remain foreground from session start through stable transcript injection.
- Elevated target applications can reject input from a non-elevated client.
- Current text injection is generic Ctrl+V only; app-specific paste behavior is not handled.
- Reconnect, device selection, installer behavior, and external Beta credential protection are not validated in this stage.

## Windows Desktop Phase 5.6 Validation

Date: 2026-09-08

Implemented scope:

- one Paraformer task is prepared at app startup and after a completed input session
- warm tasks send generated 16 kHz mono PCM16 silence in 100 ms / 3200-byte chunks and expire after 30 seconds
- failed or expired warm acquisition falls back to the existing cold connection path
- `semantic_punctuation_enabled=false` keeps low-latency VAD segmentation
- `VOICE_INPUT_MAX_SENTENCE_SILENCE_MS` accepts only 800, 1200, or 1600; default is 1200
- Paraformer receives `language_hints=["zh","en"]`
- the first HUD partial is immediate; later partials are coalesced within 75 ms
- stable transcript parsing and Clipboard + Paste injection are unchanged

Automated validation:

```text
Tests run: 21; failures: 0

Build succeeded.
0 warnings
0 errors
```

Coverage includes request parameters, warm silence format and cadence ownership, warm expiration, failed-warm cold fallback, partial coalescing, the existing 100 ms microphone baseline, provider-neutral events, and text-injection safety checks.

Earlier user-run evidence confirms that the Desktop App receives recognized text and can insert a stable transcript into Notepad after the paste-input fix. Phase 5.6 user feedback on 2026-09-08 reports that speaking can begin without the previous several-second wait, the HUD updates character by character, Notepad receives text in small blocks, and sentence-ending periods are less frequent and feel more natural. The screenshot corroborates inserted Chinese text and a Listening HUD; timing and update cadence are user observations, not measurements from the static image. The actual sentence-silence setting and activation time relative to warm expiry were not reported. Recognition accuracy cannot be scored without the spoken reference.

Run each sentence-silence setting from the same PowerShell session that contains `DASHSCOPE_API_KEY`:

```powershell
$env:VOICE_INPUT_MAX_SENTENCE_SILENCE_MS = "800"
dotnet run --project src/desktop/VoiceInput.Desktop/VoiceInput.Desktop.csproj -c Release

$env:VOICE_INPUT_MAX_SENTENCE_SILENCE_MS = "1200"
dotnet run --project src/desktop/VoiceInput.Desktop/VoiceInput.Desktop.csproj -c Release

$env:VOICE_INPUT_MAX_SENTENCE_SILENCE_MS = "1600"
dotnet run --project src/desktop/VoiceInput.Desktop/VoiceInput.Desktop.csproj -c Release
```

Close the previous tray process before changing settings. For warm-start comparison, press the hotkey within 30 seconds after app startup or the previous real session finishes.

Manual result matrix:

| Scenario | Status | Record |
| --- | --- | --- |
| Notepad, Chinese dictation | User-observed improvement | No previous several-second startup wait; block insertion works; fewer sentence-ending periods. Continuous duration and numerical latency not measured |
| ChatGPT input, continuous Chinese | Pending | Injection success; target stability; added latency |
| Chinese/English mixed speech | Pending | English word accuracy versus the previous build |
| Natural short pauses | Partially observed | Sentence boundaries reportedly feel more natural; controlled 800/1200/1600 ms comparison remains pending |
| HUD partial display | Partially observed | User reports character-by-character updates; smoothness and added display delay not explicitly assessed |

Known limits:

- A warm task consumes a live provider session and sends silence during its bounded 30-second window. It is not kept alive indefinitely.
- Activation after warm expiry intentionally uses the cold path and can still include WebSocket/task startup latency.
- The current agent process does not inherit the API key from the user's separate PowerShell, so no cloud speech result is claimed in this record.
