# Architecture

This document separates current implementation from target architecture. Sections 1–14 describe design boundaries unless explicitly marked implemented; section 15 records the limited TSF experiment. Acceptance and next steps are owned by [ROADMAP](ROADMAP.md), with evidence in [TESTING](TESTING.md). The historical [ORIGINAL_PLAN](ORIGINAL_PLAN.md) is not a status checklist.

## Current Implementation and Technology (2026-09-16)

| Area | Current implementation / selection | Boundary |
| --- | --- | --- |
| Desktop | C# / .NET 10 / WPF; `src/desktop/VoiceInput.Desktop/` | Tray, hotkey, HUD, microphone and streaming ASR exist |
| Audio | NAudio.WinMM 3.0.1; 16 kHz mono PCM16, 100 ms chunks | Native C# capture; Python is not in the runtime path |
| ASR | Paraformer Realtime v2 is the primary desktop candidate | Cloud provider; live reliability and bilingual accuracy are not fully accepted |
| Default input | `ShellController`: partial to HUD, stable to Clipboard + Paste with restore | Locks foreground HWND; paste submission does not prove target acceptance or control-level synchronization |
| Inline experiment | `TsfAsrHost` with explicit `--tsf-asr`; C++ in `prototypes/tsf_input/` | Notepad / Chrome only; owned compositions and target/context guards; live multi-segment acceptance pending |
| ASR comparisons | Python / sounddevice / websockets in `prototypes/streaming_asr/` | Fun-ASR remains a benchmark/fallback candidate, not automatic desktop failover; other prototype providers are not product selections |
| Document / AI | No full Document Engine, target mirror, Cleanup, Intent Router, Voice Edit or Voice Send | Conceptual modules below are not shipped classes |
| Recovery / vocabulary | No recent-result recovery UI or personal dictionary | Follow-on proposals in section 16 |
| Backend / local ASR | No backend or selected local ASR; no selected cleanup LLM | FastAPI in the original plan is a future candidate, not an adopted dependency |

Current runtime paths:

```text
Default: microphone -> Paraformer -> partial -> HUD
                                 -> stable -> guarded Clipboard + Paste
Experiment: microphone -> Paraformer -> neutral segment events
           -> bounded local pipe -> native TSF owned composition
```

Default mode retains a 30-second warm task. TSF retention defaults to 180 seconds, configurable from 30–300; neither proves low startup latency. TSF fails closed on backlog and never replays uncertain writes. Default stop attempts provider completion with a bounded wait; TSF stop cancels late output and retains already visible text. These modes are not identical.

## 1. System Overview

First prove reliable incremental dictation in validated Windows targets. Later stages add opt-in cleanup, explicit voice editing and guarded actions after document synchronization and undo exist. Faithful dictation is the default; no LLM rewrite is required to show text.

Core principle:

```text
Latency > Controllability > Intelligence
```

The MVP architecture is client-first:

```text
Microphone
  -> Audio Capture
  -> Streaming ASR Connection
  -> Transcript Processor
  -> Document Engine
  -> Input Target Adapter
  -> Foreground Application

Stable semantic chunks
  -> Async Cleanup / Intent Understanding
  -> Structured EditOperation
  -> Document Engine
  -> Input Target Adapter
```

The real-time path must never wait for AI cleanup or complex intent understanding before showing transcript text.

## 2. Module Boundaries

### Windows Client

The formal client is C# + .NET + WPF.

Target module boundaries (not a list of implemented classes):

- `AudioCapture`: captures microphone audio and emits timestamped PCM chunks.
- `AsrStreamingClient`: owns the realtime WebSocket connection to the ASR provider.
- `TranscriptProcessor`: converts ASR events into `Interim`, `Editable`, and `Committed` transcript regions.
- `DocumentEngine`: owns text state, cursor, selection, regions, edit history, undo, and structured mutations.
- `IntentRouter`: classifies voice input into `DICTATION`, `EDIT`, or `ACTION`.
- `EditCommandParser`: converts edit utterances into structured `EditOperation` objects.
- `CleanupService`: asynchronously cleans stable semantic chunks and returns local span replacements.
- `InputTargetManager`: detects foreground app and active text target.
- `InputTarget` / `AppAdapter`: abstracts text injection, selection reading, and app-specific behavior.
- `TargetStateSynchronizer`: observes target-side cursor, selection, and text changes, then converts external changes into local document synchronization events.
- `LatencyTracer`: records timestamps across audio, ASR, cleanup, edit, and document apply stages.
- `UiShell`: shows lightweight status, transcript preview, errors, and control state.

### Python Prototype

Python is allowed only for fast Streaming ASR validation:

```text
Microphone -> Streaming ASR -> partial transcript -> metrics
```

The Python prototype must not become the product architecture and must not own the long-term document engine, Windows text integration, or app adapters.

### Backend

No backend is implemented in the current stage. The architecture leaves room for a later minimal backend for Beta access, config, API credential protection, and telemetry.

## 3. Data Flow

### Realtime Dictation Flow

```text
1. AudioCapture records audio_capture_timestamp.
2. AudioCapture emits small streaming audio chunks.
3. AsrStreamingClient sends chunks and records audio_sent_timestamp.
4. ASR returns partial transcript events.
5. TranscriptProcessor creates or updates Interim regions.
6. UI displays partial text immediately.
7. ASR marks text as stable/final.
8. TranscriptProcessor upgrades regions to Editable.
9. DocumentEngine applies stable text to the local Document State.
10. InputTarget inserts or replaces only the affected target span.
11. If Light cleanup is enabled, CleanupService receives stable semantic chunks asynchronously.
12. CleanupService returns local cleanup replacements.
13. DocumentEngine applies structured span-level replacements if the target span is still valid.
```

### Voice Edit Flow

```text
1. Stable utterance enters IntentRouter.
2. Fast local rules detect simple EDIT / ACTION cases first.
3. Complex or fuzzy cases may call a remote model.
4. EditCommandParser returns a structured EditOperation.
5. DocumentEngine resolves TextReference to a concrete span.
6. DocumentEngine applies the operation locally.
7. InputTarget updates the foreground app.
8. LatencyTracer records edit_command_start/end and document_apply_timestamp.
```

### Action Flow

```text
1. Enabled command handling validates a stable candidate; quoted/dictated command words remain text.
2. Check active session, unchanged target and a verified action mapping; Voice Send is off by default.
3. Execute only after these checks; ambiguity must not trigger sending.
```

## 4. Document Model

The system must not store only one transcript string. It owns a continuously changing `DocumentState`.

Conceptual model:

```text
DocumentState
  full_text
  cursor_position
  selection
  sentence_boundaries
  paragraph_boundaries
  recent_input_spans
  regions
  edit_history
  undo_stack
  active_target
  revision
```

Regions:

```text
DocumentRegion
  id
  span
  state: interim | editable | committed
  source: asr | keyboard | cleanup | voice_edit | app_sync
  created_at
  updated_at
  asr_confidence
  cleanup_status
```

The document engine is local and authoritative for:

- text spans and revisions
- cursor and selection snapshots
- sentence and paragraph segmentation
- recent dictation windows
- undo grouping
- conflict detection
- edit operation execution

The foreground app remains the visible target, but the client keeps a local mirror so voice edits can target previous text without requiring full app document access on every command.

### InputSession

An `InputSession` isolates dictation and editing state for one foreground app/input target.

Conceptual model:

```text
InputSession
  session_id
  active_target
  document_state
  asr_session
  created_at
  last_active_at
  revision
  status: active | suspended | closed | reconnecting
```

Session rules:

- A new session starts when the user triggers voice input for a foreground target that is not already the active session.
- If the app or focused target changes, stop writes and invalidate pending work. Never automatically continue dictation into the new target.
- Suspended sessions keep their `DocumentState`, revision, edit history, and ASR boundary metadata, but they do not receive target writes.
- Future resume requires explicit activation plus validated target identity, revision and context; current TSF requires a fresh activation and has no persisted document sessions.
- Future reconnects remain bound to the original validated target and never replay uncertain writes; seamless reconnect is not currently implemented.
- Voice commands apply only to the active session unless a future explicit cross-session command is designed.

### Target State Synchronization

Because `DocumentState` is a local mirror, the client needs an explicit `TargetStateSynchronizer` / `ExternalEditSynchronizer`.

Responsibilities:

- observe foreground target identity changes
- sample cursor and selection state when the target supports it
- observe or infer target text changes after keyboard, paste, mouse, or app-side edits
- distinguish client-originated mutations from external mutations
- convert external changes into `DocumentEngine` synchronization operations
- prevent stale AI cleanup or voice edits from overwriting user changes

Conceptual interface:

```text
TargetStateSynchronizer
  bind_session(input_session)
  capture_target_snapshot()
  classify_change(previous_snapshot, current_snapshot)
  emit_external_edit(change)
  acknowledge_client_mutation(mutation_id, expected_target_snapshot)
```

Change sources:

```text
client_mutation
  produced by DocumentEngine and applied through InputTarget

user_keyboard_or_mouse
  produced by direct typing, deletion, cursor movement, or selection changes

external_app_mutation
  produced by autocomplete, formatting, app-side replacement, collaboration, or plugin behavior
```

Distinguishing strategy:

- Every client write carries a `mutation_id`, target snapshot, document revision, and expected text delta.
- After the write, `TargetStateSynchronizer` samples the target and acknowledges the mutation if the observed change matches the expected delta.
- If target text changes without a matching pending `mutation_id`, treat it as external.
- If cursor or selection changes without text changes, update `DocumentState.cursor_position` / `DocumentState.selection` and do not rewrite text.
- If the target only exposes weak state, prefer conservative sync: mark affected spans as externally changed and cancel pending cleanup for those spans.

The full synchronizer is not implemented. TSF has scoped focus, edit and user-input guards, including an experiment-local hook; these are not a general document mirror or keyboard/voice synchronization engine.

## 5. Transcript State Machine

Transcript states:

```text
Interim -> Editable -> Committed
```

### Interim

Produced when ASR returns partial transcript events.

Properties:

- low latency
- may change frequently
- should be visually distinguishable in the overlay or local preview
- should not trigger AI cleanup
- should not be used as a stable edit target unless explicitly selected by the user

Upgrade condition:

- ASR returns a stable/final event for the same segment.
- Timer-only stability is not used by the current path. Future heuristics need provider-specific evidence and must not silently promote unconfirmed partials.

### Editable

Produced when transcript text becomes stable enough to insert into the target app and edit locally.

Properties:

- can be targeted by voice edits
- can be cleaned by async AI cleanup
- remains mutable while nearby speech is still ongoing
- tracks its original ASR span and current document span

Upgrade condition:

- the user moves on to later text,
- cleanup is applied or skipped,
- no pending command needs the region,
- the region is no longer in the recent editing window.

### Committed

Produced when text is no longer part of the active dictation/editing window.

Properties:

- still part of `full_text`
- can be edited by explicit references, selection, or fuzzy text
- is not rewritten by background cleanup unless the user asks
- belongs to undo history as stable document content

### Avoiding ASR / Cleanup Conflicts

AI cleanup never mutates `Interim` text. Cleanup receives stable semantic chunks only, and returns span-level replacement proposals tied to:

- source region ids
- source text hash
- document revision
- target span

Apply cleanup only if session, target, region, expected revision and source text hash all remain valid and user activity has not invalidated the operation. A hash match alone cannot authorize a write to another target or revision. Discard results after user edits; a later explicit request may create a new operation. Preserve original text for undo.

### Pending Architecture Decision: Interim Rendering

Default Desktop already uses Option B. Opt-in TSF evaluates Option A in limited targets. Option C is a future alternative. Production integration remains pending real-speech, interruption and compatibility evidence; these are not three implemented modes.

Option A: inject `Interim` directly into the foreground input control.

- Latency: best perceived latency because words appear exactly where the user is typing.
- Text jumping: highest risk because ASR partial text may change repeatedly.
- Cursor stability: highest risk, especially when the user types or moves the cursor during dictation.
- Windows app compatibility: hardest path because frequent replace operations behave differently across apps.

Option B: render `Interim` only in HUD / Overlay; write only Stable / Editable text to the foreground input control.

- Latency: first visible transcript can still be fast, but the target input box updates later.
- Text jumping: much lower because unstable ASR text does not mutate the app field.
- Cursor stability: better because only stable spans are inserted into the target.
- Windows app compatibility: stronger MVP baseline because fewer target writes are needed.

Option C: hybrid rendering.

- Latency: can show immediate HUD text while selectively injecting low-risk interim spans into compatible targets.
- Text jumping: moderate, controlled by target capability and ASR stability.
- Cursor stability: depends on adapter quality and write frequency.
- Windows app compatibility: likely best long-term path, but requires capability detection and careful fallback behavior.

Pending decision:

```text
Interim rendering strategy will be decided after:
1. Streaming ASR prototype measures partial/stable timing.
2. Text Injection prototype measures replacement stability in common Windows targets.
```

## 6. Intent / Edit Model

The intent router must distinguish:

```text
DICTATION
EDIT
ACTION
```

Latency strategy:

- simple deterministic rules run first
- short command grammars cover common edits and actions
- remote LLM calls are reserved for fuzzy reference resolution, rewrite, or ambiguous commands
- no complex Agent Framework is used

Voice command false-positive protection:

- Words such as "发送" or "撤销" appearing inside normal dictation must not directly trigger actions.
- `ACTION` commands require stricter confidence and context checks than ordinary `EDIT` commands.
- `SEND` follows a conservative policy: prefer missing a command over accidentally sending user text.
- Intent routing should not run high-cost semantic judgment over every transcript segment by default.
- Fast local command detection should produce candidates first; only plausible candidates enter stricter command validation.

Add a `CommandCandidateDetector` boundary:

```text
CommandCandidateDetector
  input: stable transcript segment, pause/context metadata, active session state
  output: command candidate or normal dictation
```

Candidate validation may consider:

- whether the segment is short and command-shaped
- whether the command is isolated by pause or trigger context
- whether the active input session is in a listening/editing state
- ASR confidence and transcript stability
- target action risk

Current stage does not introduce a complex command mode. It only defines the boundary needed to keep dangerous actions out of the default dictation path.

### Intent Types

```text
DICTATION
  text

EDIT
  replace
  delete
  insert
  rewrite
  undo

ACTION
  send
  cancel
  undo
```

### TextReference

Unified reference model:

```text
TextReference
  type:
    current_sentence
    previous_sentence
    current_paragraph
    previous_paragraph
    selected_text
    recent_input
    fuzzy_text
  value
  search_window
  confidence
```

Example:

```text
"前面那句服务器成本比较高有点啰嗦"
```

Expected handling:

```text
1. Identify reference type: previous_sentence or fuzzy_text.
2. Resolve candidate span in DocumentState.
3. Confirm confidence threshold.
4. Produce a span-scoped rewrite operation.
5. Apply only that span.
```

### EditOperation

All edits should become structured operations before execution.

Example:

```json
{
  "type": "replace",
  "target": {
    "type": "fuzzy_text",
    "value": "服务器成本"
  },
  "replacement": "交付成本"
}
```

Operation types:

```text
replace
delete
insert
rewrite
undo
```

The Document Engine executes operations. LLMs may help parse or rewrite, but they do not directly own the whole document and should not rewrite the full text by default.

## 7. Windows Integration Strategy

The target design uses layered adapters. Current default uses Clipboard + Paste; TSF is a separate opt-in path. The UI Automation and capability-driven adapter set below are future design, not automatic fallbacks that exist today.

### Foreground Window Detection

Use foreground window detection to identify:

- active process
- active window handle
- focused control when available
- known app profile

This is required for the MVP.

### SendInput

Best for:

- keyboard shortcuts
- action commands such as Enter / Ctrl+Enter / Esc
- broad compatibility

Risks:

- target app behavior varies
- selection and cursor state may be hard to read
- fast input can race with user typing

MVP role: primary low-level tool for shortcuts and actions. For text insertion, it is one capability among several and should be selected by target capability, not treated as the universal first choice.

### Clipboard

Best for:

- inserting longer text quickly
- replacing selected text
- apps that handle paste reliably

Risks:

- modifies user clipboard
- some apps sanitize or block paste
- privacy sensitivity

Current role: default stable-text insertion with clipboard preservation/restoration. Future role: controlled fallback where appropriate. Never use it for every partial update. Synthetic paste submission does not confirm target content.

### UI Automation

Best for:

- reading focused control text
- reading selection
- setting text in compatible controls
- app-specific adapters

Risks:

- inconsistent support across apps
- latency and reliability vary
- some apps expose incomplete text patterns

MVP role: use for target detection and selection/text retrieval when available; not the only write path.

### Windows Accessibility APIs

Best for:

- additional focus and control metadata
- compatibility with apps where UI Automation is insufficient

Risks:

- inconsistent text editing capability
- more app-specific behavior

MVP role: secondary fallback and later compatibility expansion.

### InputTarget / AppAdapter

Abstract all foreground app interaction:

```text
InputTarget
  app_id
  window_handle
  focused_control
  capabilities
  read_selection()
  read_context()
  insert_text(text)
  replace_span(span, text)
  send_action(action)
```

Adapters:

- `UiAutomationAdapter`: preferred when the target reliably supports text patterns, selection, and controlled replacement.
- `ClipboardPasteAdapter`: longer insertions and selected text replacement.
- `UnicodeSendInputAdapter`: text insertion fallback when UI Automation and paste are unsuitable.
- `GenericSendInputAdapter`: shortcuts and action execution fallback.
- future app-specific adapters for browsers, editors, chat apps, and office apps.

Text write strategy is capability-driven:

```text
1. Use an app-specific adapter when one exists and is proven reliable.
2. Use UI Automation when the target exposes compatible text and selection patterns.
3. Use Clipboard + Paste for longer text or selected replacement when clipboard restoration is acceptable.
4. Use Unicode / SendInput when lower-level synthetic input is the most compatible option.
```

SendInput remains important for keyboard shortcuts and `ACTION` execution, but it is not fixed as the first choice for every text insertion.

## 8. Async Architecture

No module may block realtime display.

Target task model (cleanup and document tasks do not exist yet):

```text
AudioCaptureTask
  -> bounded audio chunk channel

AsrConnectionTask
  -> sends audio chunks
  -> receives ASR events

TranscriptProcessingTask
  -> updates transcript regions
  -> emits document mutations

DocumentMutationTask
  -> serializes DocumentState writes
  -> emits UI and InputTarget updates

IntentProcessingTask
  -> handles stable command candidates
  -> emits EditOperation / ActionOperation

CleanupTask
  -> debounced async cleanup for stable semantic chunks

UiUpdateTask
  -> displays state without owning document mutations

TargetSyncTask
  -> samples foreground app, cursor, selection, and externally changed text
```

Concurrency rules:

- DocumentState writes are serialized through one mutation queue.
- Audio capture and ASR send/receive are independent from cleanup and intent calls.
- Cleanup and complex intent parsing may be cancelled when document revision changes.
- UI reads snapshots; it does not mutate document state directly.
- InputTarget writes include revision checks and conflict detection.
- TargetStateSynchronizer classifies target-side changes before they mutate DocumentState.
- User keyboard and mouse changes update the active InputSession before pending AI operations are applied.

## 9. Latency Measurement

Latency measurement is part of the architecture from day one.

Each audio chunk and transcript segment should carry trace ids and timestamps:

```text
audio_capture_timestamp
audio_sent_timestamp
first_partial_timestamp
stable_transcript_timestamp
cleanup_start_timestamp
cleanup_end_timestamp
edit_command_start_timestamp
edit_command_end_timestamp
document_apply_timestamp
target_apply_timestamp
```

Required computed metrics:

- Audio -> first transcript latency
- Audio -> stable transcript latency
- continuous transcription lag
- edit command latency
- cleanup latency
- document apply latency
- target app apply latency

Measure first HUD text separately from first target text, speech-end to final insertion, and total time until user-accepted text. Definitions and proposed cases are in [TESTING](TESTING.md).

Current diagnostics cover only part of this model: `chunk_captured` is recorded on dequeue, not the hardware callback. `completion_observed_ms` includes the send operation and local completion observation; `continuation_delay_ms` measures subsequent resumption delay. Neither is pure network latency or proof of server receipt. Document/cleanup timings above are target instrumentation, not collected metrics.

## 10. Failure Handling

### ASR Reconnect

Future reconnect design should support (current TSF stops the session on failure):

- connection state tracking
- exponential backoff with short initial retry
- audio buffer limits
- bounded buffering; fail visibly on overflow instead of silently dropping speech for catch-up
- user-visible degraded state
- transcript segment boundaries after reconnect

Interim text from a lost connection must not be promoted without a stable event. Preserve visible text, report interruption and require fresh activation. Never retry uncertain target writes or redirect them to another field.

### Interruption Handling

Interruptions include:

- user stops speaking
- user starts speaking while cleanup is pending
- user types during dictation
- foreground app changes
- ASR reconnects
- remote cleanup fails

Handling:

- user keyboard/mouse actions take precedence
- pending cleanup is cancelled or revalidated
- target changes invalidate pending writes; a new target requires explicit activation
- target-side keyboard, mouse, and app mutations are synchronized through TargetStateSynchronizer before AI operations are applied
- local DocumentState must remain valid even when remote services fail

### Conflict Handling

Before applying AI cleanup or voice edit:

```text
1. Check document revision.
2. Check target span still exists.
3. Check expected source text hash if available.
4. Check user activity since operation creation.
5. Apply only if all checks pass; otherwise discard or obtain a new explicit edit request.
```

Discard stale proposals when confidence is low. A new user-requested edit may use a fresh snapshot; an uncertain target write must never be automatically replayed.

## 11. Prototype Boundary

The Python prototype is only for Streaming ASR validation. This restriction does not prohibit the separately scoped C++ TSF experiment in section 15:

```text
Microphone
  -> streaming audio chunks
  -> realtime ASR connection
  -> partial transcript display
  -> stable transcript display
  -> latency metrics
```

Prototype may validate:

- microphone capture
- chunk size
- WebSocket / realtime API behavior
- partial transcript latency
- stable transcript latency
- reconnect behavior
- interruption behavior

Prototype must not implement:

- formal Windows text injection
- WPF UI
- Document Engine
- account system
- payment
- License
- backend
- database

Lessons from the prototype should be converted into decisions or client requirements before being used in the formal Windows client.

## 12. Beta Evolution Path

Current stage has no backend. Future 5-20 user Beta can add a minimal backend:

```text
Windows Client
      |
      v
Minimal Backend
      |-- beta access
      |-- config
      |-- API credential protection
      |-- telemetry
      v
ASR / LLM provider
```

Beta backend responsibilities:

- test code, whitelist, or device binding
- remote config and provider selection
- proxying provider credentials when required
- coarse telemetry for latency, crashes, and compatibility
- feature flags for small test groups

Client responsibilities that should remain local:

- Document Engine
- state management
- edit execution
- simple intent rules
- app detection
- text injection
- local undo and conflict handling

Constraints:

- third-party API keys must not be hard-coded or shipped in the client
- cloud unavailability must not corrupt local document state
- provider failures should degrade ASR/cleanup, not break existing text editing state
- avoid designing a full commercial backend before Beta requirements are proven

## 13. Known Risks

- Foreground application text APIs are inconsistent across Windows apps.
- Cursor and selection tracking may be unreliable in some controls.
- Fast partial transcript updates can cause visible text jump if not staged carefully.
- Clipboard-based insertion can disturb user clipboard state.
- SendInput can race with user keyboard input.
- ASR provider partial/final semantics may differ and affect state transitions.
- Voice edit references may be ambiguous without explicit selection or recent context.
- Cleanup can feel intrusive if it rewrites text while the user is typing.
- Remote ASR or LLM latency can vary under network pressure.
- External Beta requires credential protection before third-party API usage is distributed.

## 14. Alternatives Considered

### Batch-first Transcription

Rejected for MVP. Waiting for recording completion before upload and transcription cannot satisfy realtime input latency or continuous visible transcript requirements.

### Full Windows IME

Deferred. A full IME may be useful later, but it is too broad for the first MVP and not required to validate realtime voice input, document state, and app integration.

### LLM-first Command Handling

Rejected as the default path. Simple commands should use deterministic rules and structured operations. LLM calls are reserved for fuzzy references, semantic rewrite, and ambiguous edits.

### Full-document Rewrite

Rejected as the default edit model. The MVP should apply span-scoped operations through the Document Engine to preserve control and avoid surprising changes.

### Backend-first Architecture

Deferred. The MVP should prove the local Windows client, streaming ASR, and document/edit model first. A minimal backend can be added later for Beta access, credential protection, config, and telemetry.

### Python Product Client

Rejected for the formal client. Python is useful for rapid ASR validation, but the Windows client should be C# + .NET + WPF.

## 15. Phase 6 Limited Live ASR Experiment

Implementation requested on 2026-09-12. After external review was unavailable, the user explicitly waived Claude review for that implementation. The bridge and local diagnostics now exist, but full live acceptance remains open. This section records the experiment's scope, limited to Notepad and Chrome/ChatGPT. It is not a full IME, general application support, or Document Engine implementation. Existing simulated builds, Python benchmarks and the default Desktop HUD/Clipboard mode remain available.

### Boundary and Lifecycle

- Add an explicit Desktop `--tsf-asr` mode. C# owns the existing microphone, warm session, Paraformer connection, HUD and credentials. The native TSF DLL receives only provider-neutral partial/stable text through a local named pipe. No Python, cloud calls or API keys enter the DLL.
- Ctrl+Alt+J starts/stops the live experiment in the focused, allowed TSF context. Audio begins only after a validated start request, using the existing 16 kHz mono signed PCM16, 100 ms path. Buffered audio is sent only after provider task readiness.
- The pipe uses a user-SID-specific name and a single `CurrentUserOnly` server. Validate the pipe client's OS PID against the captured foreground HWND/PID and experiment allowlist before opening the microphone. This is a developer-only, same-user trust boundary, not a sandbox against malicious processes running as the same user.
- Each connection has a fresh session GUID; each packet has a strictly increasing sequence and neutral segment number. Use fixed-size, versioned UTF-16LE frames with bounded text (1024 code units), explicit lengths and validation. Reject malformed, oversized, stale or out-of-order input rather than truncating it.
- Add optional provider-neutral segment identity to existing ASR transcript events. The [official Paraformer server events](https://help.aliyun.com/en/model-studio/paraformer-server-events) document `begin_time` as sentence start time but do not list `sentence_id`. Use sentence start time as a parser-local correlation key, not an invented provider field. Its stability across all partial/final updates remains a live acceptance check; missing identity or conflicting segment order must fail closed. Ignore duplicate final/late partial events for already closed segments. Provider fields must not escape into the TSF/UI boundary.
- One text packet is in flight until a successful TSF apply acknowledgment. A bounded C# queue retains events in order; overflow aborts the session rather than silently losing a stable event. Idle heartbeats detect closed pipes. A five-second acknowledgment deadline prevents indefinite queued input.
- Native pipe I/O uses a cancellable worker and overlapped I/O, never COM or TSF edits. The TSF thread polls a single pending packet on a short timer and uses the existing edit-session/generation checks. Worker lifetime pins the module; cancellation never waits on the host UI thread.
- Partial replaces only the verified owned composition range. Stable commits that segment once, without ending the microphone session; the next segment starts a new owned composition. Owned text snapshots must have stable storage and bounded exact comparisons.
- User keyboard/pointer activity, changed target/context, explicit stop, host composition termination, invalid packet, network error or pipe loss invalidates the session before any further mutation. Close the pipe and cancel microphone/provider tasks. Preserve only already visible owned text through the tested keyboard/host handoff; never replay late finals into another target. Explicit stop does not drain unheard final text into the input control.
- Retain the fixed-prefix, first-key and host-termination safeguards. No Clipboard fallback in live TSF mode, automatic retargeting, retries of uncertain writes, or cross-app continuation. Provider disposal is bounded so a failed network close cannot keep recording or shutdown alive indefinitely.
- Log lifecycle, packet sequence/segment/length, acknowledgment and audio counters only, not transcript text or credentials. Real content still goes to the configured ASR provider as in the existing app; this is not ready for external Beta credential distribution.

### Original Implementation Scope and Remaining Acceptance

The bridge and local diagnostic tests now exist; steps 1–3 below record their scope, not a request to redo implementation. Real multi-segment acceptance remains open as described in ROADMAP/TESTING. The 2026-09-12 review waiver applied to that implementation only.

1. Add failing tests for the wire format, session/sequence isolation, duplicate stable handling and segment transitions; then implement the small C#/native bridge.
2. Connect an isolated Desktop controller to the existing audio/provider classes, and an opt-in native live build to the existing TSF mutation/handoff code. Build to a separate `live-asr` path; do not overwrite the currently loaded DLL or change registration automatically.
3. Run Desktop build/tests, native protocol/integration tests with fake transcripts, and the existing normal/diagnostic interruption tests. Report the legacy direct activation harness failure separately; compilation is not live TSF success.
4. After separate authorization to switch experimental registration, manually validate real speech in Notepad and Chrome/ChatGPT: progressive partials, at least two stable segments without duplication, natural pauses, stop/restart, typed `233aaa`, caret/selection interruption, prefix/suffix preservation and address-bar/cross-app focus loss. No output may reach the new target.
5. Record actual evidence and pending checks in TESTING/ROADMAP. Keep Phase 6 pending until microphone-to-target verification passes. Do not expand to cleanup, Voice Edit or other applications.

## 16. Follow-on Product Boundaries (Not Implemented)

The source-backed product comparison is in [PRODUCT_SPEC](PRODUCT_SPEC.md). It motivates the following constraints without selecting a competitor's model or internal architecture:

- Recovery: keep at most the current/recent session's stable result in bounded memory, with delivery status; replace on new activation and clear on exit. The future implementation must first define an explicit size cap and clear control. Partial text stays labeled provisional. User-requested copy is allowed; no automatic paste, audio replay or retry of uncertain target writes. Long-term audio/history storage is outside this scope.
- Vocabulary: a manually editable local dictionary may map into a provider's supported hotword mechanism only when explicitly enabled. Do not add unsupported provider fields, unconditional text substitution or global keyboard learning. Disclose cloud transfer of enabled hotwords separately from local storage.
- Cleanup: default None; Light is opt-in and requires reliable span identity, target synchronization, stale-result rejection and undo. No cleanup on partials, typed text or committed history without explicit user instruction. Preserve numbers, names, negation and intentional literary repetition.
- Explicit edits: start with selected text or an unambiguous recent span. Complex rewrite sends only that span and instruction to a separately chosen LLM; rules can handle exact replacements locally. Quoted commands and narrative self-correction must not become destructive edits automatically.
- Cloud privacy: audio already goes to the configured ASR service. UI location, inference location, retention, training use and optional cloud history are separate properties; do not promise provider behavior not established by its applicable policy. Broader app context, style learning and cloud sync remain deferred.

These are future design constraints. Current work remains the Phase 6 send-backlog investigation; this documentation update does not authorize implementing these capabilities together.

