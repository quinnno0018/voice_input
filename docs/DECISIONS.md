# Decisions

## TSF Ready-Before-Capture Repair (2026-09-17)

- After the observed cold-start queue overflow and user approval, TSF mode must acquire a ready ASR provider before opening the microphone. Show Connecting during acquisition and Speak now only after capture starts. Warm retention is unchanged; this does not promise instant readiness after expiry.
- Revalidate cancellation and the original foreground target after connection acquisition. Release the acquired provider if validation or capture fails. Do not enlarge the audio queue, drop old audio to catch up, or replay uncertain text. Default HUD/Clipboard startup remains unchanged.
- Compare Python and the C# provider using the same generated PCM silence fixture, matching requests and paced 100 ms sends. This is a transport diagnostic, not recognition accuracy or live microphone acceptance. No new dependency, product module or plaintext credential file is required. Actual cloud comparison and real-speech acceptance remain pending.

## Documentation and Product Research Alignment (2026-09-16)

- Preserve `ORIGINAL_PLAN.md` unchanged as historical input. Keep existing document paths: README introduces the product; PRODUCT_SPEC defines behavior; ARCHITECTURE covers architecture and technology; this file records choices; ROADMAP owns current progress; TESTING retains evidence. Do not create three parallel product/architecture/stack documents.
- Actual code contains default HUD/Clipboard dictation and opt-in ASR-to-TSF integration, not only a skeleton. Document Engine, general target synchronization, Cleanup, vocabulary, recovery UI, Voice Edit and Voice Send remain unimplemented. Historical design bullets below are requirements, not a shipped feature list; historical pending test statements are resolved only by later TESTING evidence.
- Keep the immediate Phase 6 priority: diagnose send backlog, then validate multi-segment and interruption safety. The latest local test results do not close real-session failures. This update changes documentation, not runtime behavior or development scope.
- Borrow observable interaction from WeChat input and Typeless, with sources and limitations in [PRODUCT_SPEC](PRODUCT_SPEC.md). No evidence selects their proprietary ASR models for this project, proves all WeChat speech is local, or equates Typeless stop-and-insert flow with inline streaming. Retain Paraformer as the current candidate and preserve Latency > Controllability > Intelligence.
- Revise the old default-Light product proposal: default None preserves expression without extra LLM rewrite. Light becomes opt-in only after span validation, target synchronization and undo exist. Protect names, numbers, negation and intentional repetition; exact corrections are explicit edits, not default cleanup.
- Prioritize bounded recent-result recovery and a manually maintained vocabulary after core stability. Recovery is user-requested copy with explicit stable/provisional and delivery states; never replay uncertain writes. No audio history by default. Define the memory cap and clear control before implementation. Vocabulary is local by default; enabled provider hotwords may leave the device and require supported provider mapping. No unconditional homophone replacement or global keyboard learning.
- A target or focus change invalidates pending writes. Future resume requires explicit activation and validation; current TSF does not automatically resume into another app. All applicable session/target/revision/source checks must pass before AI replacement; a source hash alone is insufficient.
- Commands are opt-in and validated separately from dictation. Quoted commands and novel dialogue remain text. Voice Send defaults off and requires both unambiguous intent and a verified target action rule.
- Separate local UI/storage, cloud inference, provider retention, training use and optional cloud history. Do not infer zero retention from local history or promise unspecified provider behavior. Broader app context, auto style, local ASR, cloud sync and Beta/commercial systems remain deferred.
- Measure first HUD text, first target text, final insertion after speech, total usable-text completion and manual corrections separately. These are future acceptance requirements where instrumentation is absent; no competitive latency ranking is claimed.

## Phase 6 Limited ASR-to-TSF Experiment (2026-09-12)

- Authorized warm-retention trial: the TSF mode reads `VOICE_INPUT_TSF_WARM_SECONDS`, defaults to 180 seconds and accepts integer values from 30 through 300. Lifetime starts when the provider is ready. Expired/failed warm tasks use the existing cold fallback; no automatic perpetual renewal or microphone capture while idle. The default HUD/Clipboard mode retains its original 30-second lifetime. Log warm hit/expiry/cold selection without content or credentials. Live improvement remains to be verified.
- Implement an opt-in `--tsf-asr` Desktop mode, not a replacement of the verified default HUD/Clipboard mode. Keep the simulated TSF builds and Python benchmark.
- C# retains microphone capture, the existing warm session, Paraformer and credentials. A native `live-asr` TSF build owns only target-local composition updates. The live experiment accepts Notepad and Chrome; this does not claim support for other apps or a full IME.
- Add optional neutral `SegmentId` metadata to partial/stable events. Paraformer alone maps the documented sentence `begin_time` into this identity. Missing/changing identity before final stops the experiment; already closed segment events are ignored. Confirm correlation against actual speech before accepting this path.
- Use a versioned, fixed 2104-byte local pipe frame, bounded to 1024 UTF-16 code units. Session GUID, packet sequence, neutral segment sequence and target HWND isolate updates. Enforce same-user ACL, client PID/foreground validation and native server-user verification.
- Only one text update waits for native acknowledgment at a time. Limit the C# queue to 32 updates; abort on overflow or a five-second acknowledgment timeout. Do not retry an uncertain write. Network loss cannot guarantee whether the last unacknowledged update was applied, so freeze the target instead of replaying it.
- Native overlapped pipe I/O runs in a cancellable worker, with module lifetime protection; the TSF thread polls at 20 ms and applies through its existing guarded edit sessions. No cloud networking or credentials enter the DLL.
- A stable ends only its owned composition, retaining the input session and caret anchor for the next segment. Keyboard/pointer activity, focus change, pipe loss or explicit stop cancels further updates. Manual input takes control; continuing speech after manual editing requires a fresh activation.
- Ctrl+Alt+J controls the live TSF experiment. Suppress reactivation by the J portion of a stop chord when Ctrl/Alt has already cancelled the session. Stop retains currently visible text and discards late results; it does not wait for an unseen final to arrive.
- Bound provider WebSocket disposal to two seconds. Retain the tested keyboard/host handoff, and store owned text snapshots independently of incoming packet buffers.
- Keep native outputs in separate live-asr/live-asr-trace directories. Registration changes and rollback require explicit authorization; do not overwrite the currently loaded experiment DLL or modify the default input method.
- Per user instruction, external Claude review was waived for the 2026-09-12 implementation. That historical waiver does not cover later documentation revisions or establish completed live acceptance.

This file records confirmed technical decisions.

## TSF Feasibility Experiment (2026-09-08)

- A separate C++ Windows TSF experiment in `prototypes/tsf_input/` is authorized to validate inline partial composition, revision, and stable commit using simulated transcripts. This does not select TSF as the final product integration or replace the C# desktop client.
- The experiment owns only its composition range. Keyboard activity, external edits, selection changes, or target changes stop pending simulated updates; interruption preserves visible text instead of deleting user content.
- Initial targets are disposable Notepad and browser input fields on x64 Windows. No ASR, microphone, AI cleanup, Voice Edit, or backend is added to this experiment.
- Building and local tests do not register a text service. Cross-application testing requires separately approved Windows COM/TSF registration, with an explicit unregister entry point. No default input source is changed automatically.
- The Chrome cancellation fix (2026-09-11) supplements TSF callbacks with a playback-only, current-thread `WH_GETMESSAGE` guard. Input invalidates pending output before host dispatch; messages are neither changed, swallowed, logged as keystrokes nor replayed. Composition completion is deferred outside the hook. This is an experiment-local safety mechanism, not a global keyboard hook or a selection of the production input architecture; actual Chrome interruption safety remains pending live retest.
- The first-key repair (2026-09-12) attempts synchronous composition completion only from TSF key callbacks for the matching context, before returning the uneaten key. Ordinary frames and non-keyboard cleanup remain asynchronous. A synchronous finish does not consume an older asynchronous request's pending slot. Denied/failed sync preserves key passthrough and uses bounded existing async cleanup, explicitly logged as `keyboard-handoff-deferred`; this fallback does not guarantee character ordering and is not a passing compatibility result. No key replay or cursor correction is introduced. Live Chrome validation is pending.

- The host-termination repair candidate (2026-09-12) uses the write cookie supplied to `OnCompositionTerminated` to reaffirm only unchanged, nonempty text in that composition's original context. It does not acquire a new edit lock, end composition a second time, move selection, replay into the current focus, or restore a range already cleared/changed by the host or user. Local state is detached before the write for reentrancy safety; queued callbacks keep their pending-slot ownership and cannot replay. Keyboard synchronous completion and pointer-hook behavior remain unchanged. `host-text-reaffirmed` means SetText accepted the operation, not that renderer preservation is proven. This remains an isolated TSF experiment pending live validation, not a selection of the production input architecture.

## Initial Decisions

- Windows-first.
- Official client prioritizes C# + .NET + WPF.
- Prototypes may use Python.
- Do not adopt batch-first transcription.
- Do not prioritize building a full Windows IME in the first stage.
- The current highest priority is real-time input latency.
- Do not implement a full commercialization system in the current stage.
- Architecture must preserve extension capability for later small-scale external Beta testing.

## Windows MVP Architecture Decisions

- The Windows MVP is a client-first architecture.
- The realtime path must be incremental: microphone audio is streamed in chunks to a realtime ASR connection, which returns partial and stable/final transcript events.
- The system must not use this flow as the primary path: audio recording ends -> upload complete file -> ASR -> return transcript.
- AI cleanup is decoupled from realtime ASR. Partial transcript display must not wait for cleanup.
- Transcript state has at least three levels: `Interim`, `Editable`, and `Committed`.
- `Interim` text is generated by ASR partial events and is not cleaned by AI.
- `Editable` text is stable enough to insert, target, clean, and revise within the recent editing window.
- `Committed` text remains editable by explicit reference, selection, or fuzzy match, but is not rewritten by background cleanup by default.
- The client must maintain a local `DocumentState`, not a single transcript string.
- `DocumentState` must track full text, cursor position, selection, boundaries, recent input, source regions, edit history, undo, and interim/editable/committed regions.
- All document mutations should flow through a local `DocumentEngine`.
- Keyboard and mouse user actions take precedence over AI operations.
- AI cleanup or voice edits must validate document revision and target span before applying changes.
- Voice input must be classified into `DICTATION`, `EDIT`, or `ACTION`.
- Simple intent routing and simple edit/action commands should be handled locally with deterministic rules before using a remote model.
- Target edit operations include `REPLACE`, `DELETE`, `INSERT`, `REWRITE`, and `UNDO`; these are not implemented product capabilities.
- Target action operations include `SEND`, `CANCEL`, and `UNDO`; these are not implemented voice commands.
- Voice edits should become structured `EditOperation` objects before execution.
- The system should use a unified `TextReference` model for references such as current sentence, previous sentence, selected text, recent input, and fuzzy text.
- The default edit strategy is span-scoped mutation, not whole-document rewrite.
- Windows integration uses an `InputTarget` / `AppAdapter` abstraction.
- MVP Windows integration should combine foreground window detection, UI Automation, Clipboard + Paste, Unicode / SendInput, and app-specific adapters according to target capability.
- SendInput remains the primary low-level tool for shortcuts and actions, but text insertion must use a capability-driven strategy instead of always preferring SendInput.
- Clipboard is a controlled fallback for longer insertions and selected text replacement, with clipboard restoration where feasible.
- UI Automation is preferred for selection/context reading when supported, but must not be the only write path.
- Windows Accessibility APIs are reserved as compatibility expansion and fallback.
- Document state management, edit execution, simple intent rules, app detection, and text injection should remain local where possible.
- ASR, semantic rewrite, and complex intent understanding may be remote.
- Latency tracing is required from the first architecture stage and must include audio capture, audio send, first partial, stable transcript, cleanup, edit command, document apply, and target apply timestamps.
- The Python Streaming ASR prototype is limited to microphone -> streaming ASR -> partial/stable transcript -> latency metrics.
- The Python prototype must not evolve into the formal product client architecture.
- No backend is implemented now.
- A future minimal backend may be added for 5-20 user Beta access, config, API credential protection, and telemetry.
- Third-party API keys must not be hard-coded into or shipped directly inside the formal client.
- Cloud service failure must not corrupt local document state.
- `InputSession` is the boundary for isolating one foreground app/input target, its `DocumentState`, ASR session, timestamps, revision, and session status.
- Foreground app or input target changes invalidate pending writes. Another target requires explicit activation; future resume must validate identity and document state.
- The architecture includes a `TargetStateSynchronizer` / `ExternalEditSynchronizer` boundary to observe cursor, selection, and target-side text changes.
- Target synchronization must distinguish `DocumentEngine`-originated changes, user keyboard/mouse changes, and external app mutations.
- User keyboard and mouse changes take precedence over pending cleanup or AI edit operations.
- Default Desktop uses HUD-only partials; opt-in TSF tests direct target composition. Hybrid remains a design option.
- Final production integration awaits real ASR, interruption and compatibility evidence; the experiment does not settle it.
- `ACTION` commands require stricter false-positive protection than dictation or ordinary edit commands.
- A `CommandCandidateDetector` should detect plausible command candidates before stricter validation, avoiding high-cost semantic checks over every transcript segment.
- Words such as "发送" and "撤销" inside normal dictation must not directly trigger actions.
- `SEND` follows a conservative policy: missing a send command is preferable to accidental sending.

## Windows Desktop Shell Decisions

- The first formal desktop shell targets .NET 10 LTS and WPF.
- Paraformer Realtime v2 is the primary desktop ASR provider candidate. Fun-ASR Realtime remains a Python benchmark/fallback and is not coupled to the desktop UI.
- The desktop ASR boundary exposes only `PartialTranscript`, `StableTranscript`, `AsrError`, and `ConnectionState`; provider payload fields stay inside the provider implementation.
- The accepted desktop audio baseline is 16 kHz, mono, PCM16, and 100 ms chunks. The 50 ms prototype A/B showed no clear benefit.
- The desktop client captures microphone audio natively in C# through `NAudio.WinMM`; it does not invoke or package the Python prototype.
- The MVP global hotkey uses toggle behavior: one press starts listening and the next press stops. Its default is `Ctrl+Alt+Space`, configurable through `VOICE_INPUT_HOTKEY`.
- In default Desktop mode, interim/partial transcript is HUD-only and only non-empty stable text is eligible for target insertion. The separate TSF experiment allows owned inline partial compositions.
- The HUD is a non-activating WPF tool window and must not replace the user's foreground input target.
- Each minimal input session captures its foreground window at activation. Stable text is rejected if that window is no longer foreground.
- Generic MVP text insertion uses Clipboard + Paste. The client must preserve the clipboard before changing it, restore it after paste, and abort before paste if preservation or target validation fails.
- The current desktop build reads provider credentials from `DASHSCOPE_API_KEY` or `ASR_API_KEY`; no credential is embedded in source or project files.
- The desktop shell prepares one protocol-compliant Paraformer task at app startup and after each completed input session. The warm task sends generated 100 ms silent PCM chunks with `heartbeat=true`; microphone capture still starts only after explicit user activation.
- The default HUD/Clipboard mode retains warm tasks for 30 seconds. The separately authorized Phase 6 TSF trial uses the configurable lifetime described above. Expiry/failure still falls back to a cold connection rather than replacing an active recognition session.
- Paraformer Realtime v2 keeps low-latency VAD segmentation with `semantic_punctuation_enabled=false`. `max_sentence_silence` defaults to 1200 ms and the current experience test permits only 800, 1200, or 1600 ms through `VOICE_INPUT_MAX_SENTENCE_SILENCE_MS`.
- Paraformer uses `language_hints=["zh","en"]` for the current Chinese/English mixed-speech test. No AI or transcript post-processing is added.
- Provider partial events remain unchanged. The HUD renders the first partial immediately, then coalesces later partials within a 75 ms display window; stable transcript injection remains unchanged.

