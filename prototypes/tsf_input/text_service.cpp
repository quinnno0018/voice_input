#include "probe.h"
#include "composition_policy.h"
#include "handoff_trace.h"
#include <new>
#include <cwchar>
#include <string>
#include <utility>

HINSTANCE probe_module = nullptr;
std::atomic<long> probe_objects{0};
#ifdef TSF_PROBE_LIVE_ASR
#include "live_client.h"
static constexpr unsigned owned_text_capacity = live::max_text + 1;
#else
static constexpr unsigned owned_text_capacity = 256;
#endif
static constexpr wchar_t window_class[] = L"VoiceInput.TsfProbe.Timer";
static constexpr UINT complete_input_stop = WM_APP + 1;

class TextService;
class EditSession final : public ITfEditSession {
    LONG refs_ = 1;
    TextService* owner_;
    ComPtr<ITfContext> context_;
    std::uint64_t generation_;
    bool finish_;
    bool consumes_pending_;
public:
    EditSession(TextService* owner, ITfContext* context, std::uint64_t generation, bool finish,
                bool consumes_pending = true);
    ~EditSession();
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** out) override;
    ULONG STDMETHODCALLTYPE AddRef() override { return InterlockedIncrement(&refs_); }
    ULONG STDMETHODCALLTYPE Release() override {
        const auto n = InterlockedDecrement(&refs_); if (!n) delete this; return n;
    }
    HRESULT STDMETHODCALLTYPE DoEditSession(TfEditCookie cookie) override;
};

class TextService final : public ITfTextInputProcessor,
                          public ITfKeyEventSink,
                          public ITfCompositionSink,
                          public ITfThreadMgrEventSink,
                          public ITfThreadFocusSink,
                          public ITfTextEditSink {
#ifdef TSF_PROBE_TEST
    friend struct InterruptionTests;
    unsigned start_attempts_ = 0;
#endif
    LONG refs_ = 1;
    ComPtr<ITfThreadMgr> manager_;
    ComPtr<ITfContext> context_;
    ComPtr<ITfComposition> composition_;
    TfClientId client_ = 0;
    DWORD manager_cookie_ = TF_INVALID_COOKIE;
    DWORD focus_cookie_ = TF_INVALID_COOKIE;
    DWORD edit_cookie_ = TF_INVALID_COOKIE;
    HWND timer_window_ = nullptr;
    HWND target_window_ = nullptr;
    probe::Playback playback_;
    std::wstring expected_;
#ifdef TSF_PROBE_LIVE_ASR
    live::Client live_client_;
    live::Packet live_packet_;
    ComPtr<ITfRange> live_anchor_;
    bool live_pending_ = false;
    bool live_initialized_ = false;
    bool live_stop_chord_ = false;

    static bool IsControlOrAlt(WPARAM key) {
        return key == VK_CONTROL || key == VK_LCONTROL || key == VK_RCONTROL ||
            key == VK_MENU || key == VK_LMENU || key == VK_RMENU;
    }
    void MarkStopChord(WPARAM key) {
        if ((playback_.running || context_) && IsControlOrAlt(key)) live_stop_chord_ = true;
    }

    bool AnchorMatches(TfEditCookie cookie) {
        if (!live_anchor_ || !context_) return false;
        TF_SELECTION selection{};
        ULONG fetched = 0;
        const HRESULT hr = context_->GetSelection(cookie, TF_DEFAULT_SELECTION, 1, &selection, &fetched);
        BOOL empty = FALSE;
        LONG comparison = 1;
        const bool matches = SUCCEEDED(hr) && fetched == 1 && selection.range &&
            SUCCEEDED(selection.range->IsEmpty(cookie, &empty)) && empty &&
            SUCCEEDED(selection.range->CompareStart(cookie, live_anchor_.get(), TF_ANCHOR_START, &comparison)) &&
            comparison == 0;
        if (selection.range) selection.range->Release();
        return matches;
    }

    HRESULT InitializeLive(TfEditCookie cookie) {
        TF_SELECTION selection{};
        ULONG fetched = 0;
        if (InputDisabled(context_.get())) return E_ACCESSDENIED;
        HRESULT hr = context_->GetSelection(cookie, TF_DEFAULT_SELECTION, 1, &selection, &fetched);
        if (FAILED(hr) || fetched != 1 || !selection.range) return E_FAIL;
        ComPtr<ITfRange> range(selection.range);
        selection.range->Release();
        BOOL empty = FALSE;
        if (FAILED(range->IsEmpty(cookie, &empty)) || !empty) return E_ACCESSDENIED;
        hr = range->Clone(live_anchor_.put());
        if (FAILED(hr)) return hr;
        if (!live_client_.Open(target_window_)) return E_FAIL;
        live_initialized_ = true;
        Trace(L"live-asr-start");
        return S_OK;
    }
#endif
    bool edit_pending_ = false;
    bool writing_ = false;
    bool keys_advised_ = false;
    bool preserved_ = false;
    HHOOK input_hook_ = nullptr;
    static thread_local TextService* guarded_service_;
    const wchar_t* deferred_stop_reason_ = L"user-input-message";

#ifdef TSF_PROBE_HANDOFF_TRACE
    probe::HandoffTrace handoff_;
#endif
    void BeginHandoffTrace() {
#ifdef TSF_PROBE_HANDOFF_TRACE
        handoff_.Flush();
        handoff_.Reset();
#endif
    }
    void FlushHandoffTrace() {
#ifdef TSF_PROBE_HANDOFF_TRACE
        handoff_.Flush();
#endif
    }
    void Handoff(probe::HandoffStage stage, HRESULT hr = S_OK,
                 LONG start = -1, LONG length = -1, LONG comp_start = -1, LONG comp_length = -1) {
#ifdef TSF_PROBE_HANDOFF_TRACE
        handoff_.Add({stage, 0, playback_.generation, playback_.index, playback_.running,
            edit_pending_, static_cast<bool>(composition_), hr, start, length, comp_start, comp_length});
#else
        (void)stage; (void)hr; (void)start; (void)length; (void)comp_start; (void)comp_length;
#endif
    }
    void HandoffSelection(probe::HandoffStage stage, TfEditCookie cookie,
                          ITfContext* context, ITfRange* composition_range) {
#ifdef TSF_PROBE_HANDOFF_TRACE
        if (!handoff_.active || !context) return;
        LONG start = -1, length = -1, comp_start = -1, comp_length = -1;
        ComPtr<ITfRangeACP> acp;
        if (composition_range && SUCCEEDED(composition_range->QueryInterface(IID_ITfRangeACP,
                reinterpret_cast<void**>(acp.put())))) acp->GetExtent(&comp_start, &comp_length);
        TF_SELECTION selection{};
        ULONG fetched = 0;
        HRESULT hr = context->GetSelection(cookie, TF_DEFAULT_SELECTION, 1, &selection, &fetched);
        if (SUCCEEDED(hr) && fetched == 1 && selection.range) {
            hr = selection.range->QueryInterface(IID_ITfRangeACP, reinterpret_cast<void**>(acp.put()));
            if (SUCCEEDED(hr)) hr = acp->GetExtent(&start, &length);
        } else if (SUCCEEDED(hr)) hr = E_FAIL;
        if (selection.range) selection.range->Release();
        Handoff(stage, hr, start, length, comp_start, comp_length);
#else
        (void)stage; (void)cookie; (void)context; (void)composition_range;
#endif
    }

    static bool IsInputMessage(UINT message, WPARAM key) {
        switch (message) {
        case WM_KEYDOWN: case WM_SYSKEYDOWN: case WM_CHAR: case WM_SYSCHAR:
        case WM_LBUTTONDOWN: case WM_RBUTTONDOWN: case WM_MBUTTONDOWN: case WM_XBUTTONDOWN:
        case WM_LBUTTONDBLCLK: case WM_RBUTTONDBLCLK: case WM_MBUTTONDBLCLK: case WM_XBUTTONDBLCLK:
        case WM_NCLBUTTONDOWN: case WM_NCRBUTTONDOWN: case WM_NCMBUTTONDOWN: case WM_NCXBUTTONDOWN:
        case WM_MOUSEWHEEL: case WM_MOUSEHWHEEL: case WM_POINTERDOWN:
            return true;
        case WM_UNICHAR:
            return key != UNICODE_NOCHAR;
        default:
            return false;
        }
    }

    void RemoveInputGuard() {
        if (input_hook_) { UnhookWindowsHookEx(input_hook_); input_hook_ = nullptr; }
        if (guarded_service_ == this) guarded_service_ = nullptr;
    }

    void HaltPlayback() {
#ifdef TSF_PROBE_LIVE_ASR
        live_client_.Close();
        live_pending_ = false;
#endif
#ifdef TSF_PROBE_HANDOFF_TRACE
        handoff_.LimitTail();
#endif
        playback_.Stop();
        if (timer_window_) KillTimer(timer_window_, 1);
        RemoveInputGuard();
    }

    static LRESULT CALLBACK InputMessageHook(int code, WPARAM removed, LPARAM data) {
        if (code == HC_ACTION && removed == PM_REMOVE && guarded_service_ && data) {
            const auto& message = *reinterpret_cast<const MSG*>(data);
            if (IsInputMessage(message.message, message.wParam)) {
                auto* self = guarded_service_;
                self->AddRef();
                self->Handoff(probe::HandoffStage::QueueInput);
#ifdef TSF_PROBE_LIVE_ASR
                if (message.message == WM_KEYDOWN || message.message == WM_SYSKEYDOWN)
                    self->MarkStopChord(message.wParam);
#endif
                // Cancel before dispatch, without touching COM, text, or the original message.
                self->HaltPlayback();
                self->deferred_stop_reason_ = message.message >= WM_MOUSEFIRST &&
                    message.message <= WM_MOUSELAST ? L"user-pointer-message" : L"user-input-message";
                if (!PostMessageW(self->timer_window_, complete_input_stop,
                        static_cast<WPARAM>(self->playback_.generation), 0)) {
                    // No more text updates are possible even if cleanup cannot be posted.
                    self->deferred_stop_reason_ = L"input-stop-post-failed";
                    OutputDebugStringW(L"VoiceInput.TsfProbe: input-stop-post-failed\n");
                }
                self->Release();
            }
        }
        return CallNextHookEx(nullptr, code, removed, data);
    }

    HRESULT ArmInputGuard() {
        if (input_hook_ || guarded_service_) return E_UNEXPECTED;
        input_hook_ = SetWindowsHookExW(WH_GETMESSAGE, InputMessageHook, nullptr, GetCurrentThreadId());
        if (!input_hook_) return HRESULT_FROM_WIN32(GetLastError());
        guarded_service_ = this;
        return S_OK;
    }

    bool InputDisabled(ITfContext* context) {
        ComPtr<ITfCompartmentMgr> compartments;
        if (FAILED(context->QueryInterface(IID_ITfCompartmentMgr,
                reinterpret_cast<void**>(compartments.put())))) return true;
        for (const GUID* id : {&GUID_COMPARTMENT_KEYBOARD_DISABLED, &GUID_COMPARTMENT_EMPTYCONTEXT}) {
            ComPtr<ITfCompartment> compartment;
            if (FAILED(compartments->GetCompartment(*id, compartment.put()))) return true;
            VARIANT value{};
            const HRESULT hr = compartment->GetValue(&value);
            const bool disabled = FAILED(hr) || (value.vt == VT_I4 && value.lVal != 0);
            VariantClear(&value);
            if (disabled) return true;
        }
        return false;
    }

    static LRESULT CALLBACK WindowProc(HWND window, UINT message, WPARAM wp, LPARAM lp) {
        auto* self = reinterpret_cast<TextService*>(GetWindowLongPtrW(window, GWLP_USERDATA));
        if (message == WM_NCCREATE) {
            self = static_cast<TextService*>(reinterpret_cast<CREATESTRUCTW*>(lp)->lpCreateParams);
            SetWindowLongPtrW(window, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(self));
        }
        if (self && message == WM_TIMER) {
            self->AddRef();
            self->Tick();
            self->Release();
            return 0;
        }
        if (self && message == complete_input_stop) {
            self->AddRef();
            if (!self->playback_.running && wp == self->playback_.generation) {
                Trace(self->deferred_stop_reason_);
                if (!self->edit_pending_) self->Request(true);
            }
            self->Release();
            return 0;
        }
        return DefWindowProcW(window, message, wp, lp);
    }

    bool SameTarget() {
        if (!manager_ || GetForegroundWindow() != target_window_) return false;
        ComPtr<ITfDocumentMgr> document;
        ComPtr<ITfContext> focused;
        return SUCCEEDED(manager_->GetFocus(document.put())) && document &&
            SUCCEEDED(document->GetTop(focused.put())) &&
            SameObject(focused.get(), context_.get());
    }

    bool OwnRangeUnchanged(TfEditCookie cookie) {
        if (!composition_) return false;
        ComPtr<ITfRange> range;
        if (FAILED(composition_->GetRange(range.put()))) return false;
        wchar_t observed[owned_text_capacity]{};
        ULONG count = 0;
        if (FAILED(range->GetText(cookie, 0, observed, owned_text_capacity, &count))) return false;
        TF_SELECTION selection{};
        ULONG fetched = 0;
        if (FAILED(context_->GetSelection(cookie, TF_DEFAULT_SELECTION, 1, &selection, &fetched)) ||
            fetched != 1 || !selection.range) return false;
        BOOL empty = FALSE;
        LONG end_comparison = 1;
        const bool caret = SUCCEEDED(selection.range->IsEmpty(cookie, &empty)) && empty &&
            SUCCEEDED(selection.range->CompareStart(cookie, range.get(), TF_ANCHOR_END, &end_comparison)) &&
            end_comparison == 0;
        selection.range->Release();
        return probe::CanReplace(SameTarget(), caret, expected_,
            std::wstring_view(observed, count));
    }

    void UnadviseContext() {
        if (context_ && edit_cookie_ != TF_INVALID_COOKIE) {
            ComPtr<ITfSource> source;
            if (SUCCEEDED(context_->QueryInterface(IID_ITfSource,
                    reinterpret_cast<void**>(source.put())))) source->UnadviseSink(edit_cookie_);
        }
        edit_cookie_ = TF_INVALID_COOKIE;
    }

    HRESULT Finish(TfEditCookie cookie, bool end_session = true) {
        if (end_session) { HaltPlayback(); UnadviseContext(); }
        auto composition = composition_;
        HRESULT hr = S_OK;
#ifdef TSF_PROBE_HANDOFF_TRACE
        const auto diagnostic_context = context_;
        ComPtr<ITfRange> diagnostic_range;
        if (composition) composition->GetRange(diagnostic_range.put());
        HandoffSelection(probe::HandoffStage::EndBefore, cookie, diagnostic_context.get(), diagnostic_range.get());
#endif
        if (composition) hr = composition->EndComposition(cookie);
#ifdef TSF_PROBE_HANDOFF_TRACE
        HandoffSelection(probe::HandoffStage::EndAfter, cookie, diagnostic_context.get(), diagnostic_range.get());
#endif
        if (SUCCEEDED(hr)) {
            composition_.reset();
            if (end_session) context_.reset();
            expected_ = {};
#ifdef TSF_PROBE_LIVE_ASR
            if (end_session) live_anchor_.reset();
#endif
        }
        Trace(L"composition-ended", hr);
        return hr;
    }

    void Request(bool finish) {
        if (!context_ || edit_pending_) return;
        auto* edit = new(std::nothrow) EditSession(
            this, context_.get(), playback_.generation, finish);
        if (!edit) {
            HaltPlayback();
            Trace(L"out-of-memory", E_OUTOFMEMORY);
            return;
        }
        edit_pending_ = true;
        HRESULT session_hr = E_FAIL;
        Handoff(finish ? probe::HandoffStage::RequestFinish : probe::HandoffStage::RequestFrame);
        const HRESULT hr = context_->RequestEditSession(
            client_, edit, TF_ES_ASYNC | TF_ES_READWRITE, &session_hr);
        Handoff(probe::HandoffStage::RequestReturn, hr);
        Handoff(probe::HandoffStage::SessionResult, session_hr);
        edit->Release();
        if (FAILED(hr) || FAILED(session_hr)) {
            edit_pending_ = false;
            HaltPlayback();
            Trace(L"edit-request-rejected", FAILED(hr) ? hr : session_hr);
        }
    }

    void Tick() {
        if (!playback_.running) return;
        if (!SameTarget()) { Stop(L"target-changed"); return; }
#ifdef TSF_PROBE_LIVE_ASR
        if (live_client_.Dead()) { Stop(L"live-pipe-ended"); return; }
        if (edit_pending_ || live_pending_ || !live_initialized_) return;
        if (!live_client_.Take(live_packet_)) return;
        live_pending_ = true;
#endif
        Request(false);
    }

    void Stop(const wchar_t* reason) {
        HaltPlayback();
        Trace(reason);
        // Outstanding edit sessions observe the invalidated generation and only end composition.
        if (!edit_pending_) Request(true);
    }

    HRESULT PreserveTerminatedText(TfEditCookie cookie, ITfComposition* composition,
                                   ITfContext* context, std::wstring_view expected) {
        if (writing_ || !composition || !context || expected.empty() || expected.size() >= owned_text_capacity)
            return S_FALSE;
        ComPtr<ITfRange> range;
        HRESULT hr = composition->GetRange(range.put());
        if (FAILED(hr)) return hr;
        if (!range) return E_FAIL;
        ComPtr<ITfContext> range_context;
        hr = range->GetContext(range_context.put());
        if (FAILED(hr)) return hr;
        if (!SameObject(context, range_context.get())) return S_FALSE;
        wchar_t observed[owned_text_capacity]{};
        ULONG count = 0;
        hr = range->GetText(cookie, 0, observed, owned_text_capacity, &count);
        if (FAILED(hr)) return hr;
        if (count >= owned_text_capacity || std::wstring_view(observed, count) != expected) return S_FALSE;
        // The host supplied this write cookie. Reaffirm only unchanged owned text,
        // without requesting a new lock, moving selection, or resurrecting lost text.
        HandoffSelection(probe::HandoffStage::HostReaffirmBefore, cookie, context, range.get());
        hr = range->SetText(cookie, 0, observed, static_cast<LONG>(count));
        Handoff(probe::HandoffStage::HostReaffirmAfter, hr);
        return hr;
    }

    void StopForKeyboard(ITfContext* key_context) {
        HaltPlayback();
        if (!context_) return;
        HRESULT hr = E_ABORT, session_hr = E_ABORT;
        // Sync completion must not consume the slot owned by an older queued frame.
        if (!writing_ && SameObject(key_context, context_.get())) {
            const auto context = context_;
            auto* edit = new(std::nothrow) EditSession(this, context.get(), playback_.generation, true, false);
            if (edit) {
                Handoff(probe::HandoffStage::RequestKeyboardFinish);
                hr = context->RequestEditSession(client_, edit, TF_ES_SYNC | TF_ES_READWRITE, &session_hr);
                Handoff(probe::HandoffStage::RequestReturn, hr);
                Handoff(probe::HandoffStage::SessionResult, session_hr);
                edit->Release();
            } else hr = E_OUTOFMEMORY;
        }
        if (SUCCEEDED(hr) && session_hr == S_OK && !context_) {
            Trace(L"keyboard-handoff-complete");
        } else {
            // Preserve passthrough; denied sync cannot promise ordering. Never retry in a loop.
            Trace(L"keyboard-handoff-deferred", FAILED(hr) ? hr : session_hr);
            if (!edit_pending_) Request(true);
        }
    }

    HRESULT Start(ITfContext* context) {
#ifdef TSF_PROBE_TEST
        ++start_attempts_;
#endif
        if (context_ || edit_pending_) { Stop(L"explicit-stop"); return S_OK; }
        wchar_t path[MAX_PATH]{};
        GetModuleFileNameW(nullptr, path, MAX_PATH);
        const wchar_t* name = wcsrchr(path, L'\\');
        name = name ? name + 1 : path;
        if (_wcsicmp(name, L"notepad.exe") && _wcsicmp(name, L"chrome.exe") &&
            _wcsicmp(name, L"msedge.exe")) {
            Trace(L"target-not-in-experiment-allowlist"); return S_FALSE;
        }
        if (!context) return E_INVALIDARG;
        TF_STATUS status{};
        HRESULT hr = context->GetStatus(&status);
        if (FAILED(hr) || (status.dwDynamicFlags & TS_SD_READONLY) || InputDisabled(context))
            return E_ACCESSDENIED;
        DWORD pid = 0;
        target_window_ = GetForegroundWindow();
        GetWindowThreadProcessId(target_window_, &pid);
        if (pid != GetCurrentProcessId()) return E_ACCESSDENIED;
        context_ = ComPtr<ITfContext>(context);
        ComPtr<ITfSource> source;
        hr = context_->QueryInterface(IID_ITfSource, reinterpret_cast<void**>(source.put()));
        if (SUCCEEDED(hr)) hr = source->AdviseSink(IID_ITfTextEditSink,
            static_cast<ITfTextEditSink*>(this), &edit_cookie_);
        if (FAILED(hr)) { context_.reset(); return hr; }
        playback_.Start();
#ifdef TSF_PROBE_LIVE_ASR
        live_initialized_ = false;
        live_anchor_.reset();
#endif
        BeginHandoffTrace();
        hr = ArmInputGuard();
        if (FAILED(hr)) {
            Trace(L"input-guard-failed", hr);
            Stop(L"input-guard-unavailable");
            return hr;
        }
#ifdef TSF_PROBE_LIVE_ASR
        const unsigned interval = 20;
#else
        const unsigned interval = probe::frame_interval_ms;
#endif
        if (!SetTimer(timer_window_, 1, interval, nullptr)) {
            Stop(L"timer-failed"); return E_FAIL;
        }
#ifndef TSF_PROBE_LIVE_ASR
        Trace(L"simulation-start");
#endif
        Request(false);
        return S_OK;
    }

public:
    TextService() { ++probe_objects; }
    ~TextService() { RemoveInputGuard(); FlushHandoffTrace(); --probe_objects; }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** out) override {
        if (!out) return E_POINTER;
        *out = nullptr;
        if (iid == IID_IUnknown || iid == IID_ITfTextInputProcessor)
            *out = static_cast<ITfTextInputProcessor*>(this);
        else if (iid == IID_ITfKeyEventSink) *out = static_cast<ITfKeyEventSink*>(this);
        else if (iid == IID_ITfCompositionSink) *out = static_cast<ITfCompositionSink*>(this);
        else if (iid == IID_ITfThreadMgrEventSink) *out = static_cast<ITfThreadMgrEventSink*>(this);
        else if (iid == IID_ITfThreadFocusSink) *out = static_cast<ITfThreadFocusSink*>(this);
        else if (iid == IID_ITfTextEditSink) *out = static_cast<ITfTextEditSink*>(this);
        else return E_NOINTERFACE;
        AddRef(); return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return InterlockedIncrement(&refs_); }
    ULONG STDMETHODCALLTYPE Release() override {
        const auto n = InterlockedDecrement(&refs_); if (!n) delete this; return n;
    }
    HRESULT STDMETHODCALLTYPE Activate(ITfThreadMgr* manager, TfClientId client) override {
        if (!manager) return E_INVALIDARG;
        if (manager_) return E_UNEXPECTED;
        manager_ = ComPtr<ITfThreadMgr>(manager);
        client_ = client;
        WNDCLASSW wc{};
        wc.hInstance = probe_module; wc.lpfnWndProc = WindowProc; wc.lpszClassName = window_class;
        if (!RegisterClassW(&wc) && GetLastError() != ERROR_CLASS_ALREADY_EXISTS) {
            Deactivate(); return E_FAIL;
        }
        timer_window_ = CreateWindowExW(0, window_class, L"", 0,
            0,0,0,0, HWND_MESSAGE, nullptr, probe_module, this);
        if (!timer_window_) { Deactivate(); return E_FAIL; }
        ComPtr<ITfSource> source;
        HRESULT hr = manager_->QueryInterface(IID_ITfSource, reinterpret_cast<void**>(source.put()));
        if (SUCCEEDED(hr)) hr = source->AdviseSink(IID_ITfThreadMgrEventSink,
            static_cast<ITfThreadMgrEventSink*>(this), &manager_cookie_);
        Trace(L"advise-manager", hr);
        if (SUCCEEDED(hr)) hr = source->AdviseSink(IID_ITfThreadFocusSink,
            static_cast<ITfThreadFocusSink*>(this), &focus_cookie_);
        Trace(L"advise-thread-focus", hr);
        ComPtr<ITfKeystrokeMgr> keys;
        if (SUCCEEDED(hr)) hr = manager_->QueryInterface(IID_ITfKeystrokeMgr,
            reinterpret_cast<void**>(keys.put()));
        if (SUCCEEDED(hr)) {
            hr = keys->AdviseKeyEventSink(client_, static_cast<ITfKeyEventSink*>(this), TRUE);
            keys_advised_ = SUCCEEDED(hr);
            Trace(L"advise-keys", hr);
        }
        if (SUCCEEDED(hr)) {
            hr = keys->PreserveKey(client_, probe_key, &probe_start_key, probe_name,
                static_cast<ULONG>(wcslen(probe_name)));
            preserved_ = SUCCEEDED(hr);
#ifdef TSF_PROBE_HANDOFF_TRACE
            Trace(L"preserve-key-ctrl-alt-j-host-handoff-trace", hr);
#else
            Trace(L"preserve-key-ctrl-alt-j-host-handoff", hr);
#endif
        }
        if (FAILED(hr)) Deactivate();
        Trace(L"activate", hr);
        return hr;
    }
    HRESULT STDMETHODCALLTYPE Deactivate() override {
        Stop(L"deactivate");
        UnadviseContext();
        if (manager_) {
            ComPtr<ITfKeystrokeMgr> keys;
            if (SUCCEEDED(manager_->QueryInterface(IID_ITfKeystrokeMgr,
                    reinterpret_cast<void**>(keys.put())))) {
                if (preserved_) keys->UnpreserveKey(probe_key, &probe_start_key);
                if (keys_advised_) keys->UnadviseKeyEventSink(client_);
            }
            ComPtr<ITfSource> source;
            if (SUCCEEDED(manager_->QueryInterface(IID_ITfSource,
                    reinterpret_cast<void**>(source.put())))) {
                if (manager_cookie_ != TF_INVALID_COOKIE) source->UnadviseSink(manager_cookie_);
                if (focus_cookie_ != TF_INVALID_COOKIE) source->UnadviseSink(focus_cookie_);
            }
        }
        manager_cookie_ = focus_cookie_ = TF_INVALID_COOKIE;
        keys_advised_ = preserved_ = false;
        if (timer_window_) { DestroyWindow(timer_window_); timer_window_ = nullptr; }
        UnregisterClassW(window_class, probe_module);
        manager_.reset();
        FlushHandoffTrace();
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE OnSetFocus(BOOL foreground) override {
        if (!foreground && playback_.running) Stop(L"key-focus-lost");
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE OnTestKeyDown(ITfContext* context, WPARAM key, LPARAM, BOOL* eaten) override {
        if (!eaten) return E_POINTER;
        Handoff(probe::HandoffStage::TestEnter);
        *eaten = FALSE;
#ifdef TSF_PROBE_LIVE_ASR
        MarkStopChord(key);
#endif
        const bool trigger = key == probe_start_key.uVKey && (GetKeyState(VK_CONTROL) & 0x8000) &&
            (GetKeyState(VK_MENU) & 0x8000);
        if ((playback_.running || context_) && !trigger) StopForKeyboard(context);
        Handoff(probe::HandoffStage::TestExit);
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE OnTestKeyUp(ITfContext*, WPARAM key, LPARAM, BOOL* eaten) override {
#ifdef TSF_PROBE_LIVE_ASR
        if (IsControlOrAlt(key)) live_stop_chord_ = false;
#else
        (void)key;
#endif
        if (!eaten) return E_POINTER; *eaten = FALSE; return S_OK;
    }
    HRESULT STDMETHODCALLTYPE OnKeyDown(ITfContext* context, WPARAM key, LPARAM flags, BOOL* eaten) override {
        Handoff(probe::HandoffStage::KeyEnter);
        const HRESULT hr = OnTestKeyDown(context, key, flags, eaten);
        Handoff(probe::HandoffStage::KeyExit, hr);
        return hr;
    }
    HRESULT STDMETHODCALLTYPE OnKeyUp(ITfContext* context, WPARAM key, LPARAM flags, BOOL* eaten) override {
        return OnTestKeyUp(context, key, flags, eaten);
    }
    HRESULT STDMETHODCALLTYPE OnPreservedKey(ITfContext* context, REFGUID key, BOOL* eaten) override {
        if (!eaten) return E_POINTER;
        *eaten = key == probe_key;
#ifdef TSF_PROBE_LIVE_ASR
        if (*eaten && live_stop_chord_) {
            live_stop_chord_ = false;
            Trace(L"live-stop-chord-completed");
            return S_OK;
        }
#endif
        if (*eaten) { const HRESULT hr = Start(context); Trace(L"preserved-key", hr); }
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE OnCompositionTerminated(TfEditCookie cookie, ITfComposition* composition) override {
        if (SameObject(composition_.get(), composition)) {
            Handoff(probe::HandoffStage::HostTermination);
            HaltPlayback();
            const auto old_composition = composition_;
            const auto old_context = context_;
            const auto old_text = std::move(expected_);
            // Detach before a host write can reenter this callback. Queued edits
            // retain their pending slot but no longer own a mutable target.
            composition_.reset();
            UnadviseContext();
            context_.reset();
            expected_ = {};
#ifdef TSF_PROBE_LIVE_ASR
            live_anchor_.reset();
#endif
            const HRESULT hr = PreserveTerminatedText(cookie, old_composition.get(), old_context.get(), old_text);
            Trace(hr == S_OK ? L"host-text-reaffirmed" :
                (hr == S_FALSE ? L"host-text-preserve-skipped" : L"host-text-preserve-failed"), hr);
            Trace(L"host-terminated-composition");
        }
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE OnInitDocumentMgr(ITfDocumentMgr*) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE OnUninitDocumentMgr(ITfDocumentMgr*) override {
        if (playback_.running && !SameTarget()) Stop(L"document-removed");
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE OnSetFocus(ITfDocumentMgr*, ITfDocumentMgr*) override {
        if (playback_.running && !SameTarget()) Stop(L"document-focus-changed");
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE OnPushContext(ITfContext*) override {
        if (playback_.running && !SameTarget()) Stop(L"context-pushed"); return S_OK;
    }
    HRESULT STDMETHODCALLTYPE OnPopContext(ITfContext* context) override {
        if (SameObject(context, context_.get())) Stop(L"context-popped"); return S_OK;
    }
    HRESULT STDMETHODCALLTYPE OnSetThreadFocus() override { return S_OK; }
    HRESULT STDMETHODCALLTYPE OnKillThreadFocus() override {
#ifdef TSF_PROBE_LIVE_ASR
        live_stop_chord_ = false;
#endif
        if (playback_.running) Stop(L"thread-focus-lost"); return S_OK;
    }
    HRESULT STDMETHODCALLTYPE OnEndEdit(ITfContext*, TfEditCookie cookie, ITfEditRecord*) override {
#ifdef TSF_PROBE_LIVE_ASR
        if (!writing_ && playback_.running && live_initialized_ && !composition_ && !AnchorMatches(cookie))
            Stop(L"between-segment-selection-change");
#endif
        if (!writing_ && playback_.running && composition_ && !OwnRangeUnchanged(cookie))
            Stop(L"external-text-or-selection-change");
        return S_OK;
    }

    HRESULT Apply(ITfContext* context, TfEditCookie cookie,
                  std::uint64_t generation, bool finish, bool consumes_pending = true) {
        Handoff(probe::HandoffStage::EditEnter);
        if (consumes_pending) edit_pending_ = false;
        if (!SameObject(context, context_.get())) return S_FALSE;
#ifdef TSF_PROBE_LIVE_ASR
        if (live_client_.Dead()) return Finish(cookie);
#endif
        if (finish || !playback_.Accepts(generation) || !SameTarget()) return Finish(cookie);
        writing_ = true;
#ifdef TSF_PROBE_LIVE_ASR
        const HRESULT hr = !live_initialized_ ? InitializeLive(cookie) :
            (live_pending_ ? ApplyFrame(cookie) : S_FALSE);
        if (hr == S_OK && live_pending_ && playback_.Accepts(generation)) {
            live_pending_ = false;
            live_client_.Acknowledge();
        }
#else
        const HRESULT hr = ApplyFrame(cookie);
#endif
        writing_ = false;
        if (FAILED(hr)) { Trace(L"frame-rejected", hr); Finish(cookie); }
        return hr;
    }

    HRESULT ApplyFrame(TfEditCookie cookie) {
        const auto generation = playback_.generation;
        const auto context = context_;
        if (!context || InputDisabled(context.get())) return E_ACCESSDENIED;
        ComPtr<ITfRange> range;
        HRESULT hr = S_OK;
        if (!composition_) {
#ifdef TSF_PROBE_LIVE_ASR
            if (!AnchorMatches(cookie)) return E_ABORT;
#endif
            TF_SELECTION selection{};
            ULONG fetched = 0;
            hr = context->GetSelection(cookie, TF_DEFAULT_SELECTION, 1, &selection, &fetched);
            if (FAILED(hr) || fetched != 1 || !selection.range) return E_FAIL;
            range = ComPtr<ITfRange>(selection.range);
            selection.range->Release();
            BOOL empty = FALSE;
            if (FAILED(range->IsEmpty(cookie, &empty)) || !empty) return E_ACCESSDENIED;
            ComPtr<ITfContextComposition> compositions;
            hr = context->QueryInterface(IID_ITfContextComposition,
                reinterpret_cast<void**>(compositions.put()));
            if (FAILED(hr)) return hr;
            ComPtr<IEnumITfCompositionView> active;
            hr = compositions->EnumCompositions(active.put());
            if (FAILED(hr)) return hr;
            ComPtr<ITfCompositionView> existing;
            if (active->Next(1, existing.put(), &fetched) != S_FALSE) return E_ACCESSDENIED;
            hr = compositions->StartComposition(cookie, range.get(),
                static_cast<ITfCompositionSink*>(this), composition_.put());
            if (FAILED(hr) || !composition_) return FAILED(hr) ? hr : E_FAIL;
        } else if (!OwnRangeUnchanged(cookie)) {
            return E_ABORT;
        }
        hr = composition_->GetRange(range.put());
        if (FAILED(hr)) return hr;
        const auto composition = composition_;
#ifdef TSF_PROBE_LIVE_ASR
        const std::wstring_view text(live_packet_.text, live_packet_.length);
#else
        const auto text = probe::frames[playback_.index];
#endif
        if (!playback_.Accepts(generation)) return E_ABORT;
        std::wstring next_expected;
        try { next_expected.assign(text); }
        catch (const std::bad_alloc&) { return E_OUTOFMEMORY; }
        hr = range->SetText(cookie, 0, text.data(), static_cast<LONG>(text.size()));
        if (FAILED(hr)) return hr;
        if (!playback_.running || !SameObject(composition_.get(), composition.get()))
            return E_ABORT;
        expected_ = std::move(next_expected);
        ComPtr<ITfRange> caret;
        hr = range->Clone(caret.put());
        if (FAILED(hr)) return hr;
        hr = caret->Collapse(cookie, TF_ANCHOR_END);
        if (FAILED(hr)) return hr;
        TF_SELECTION selection{caret.get(), {TF_AE_NONE, FALSE}};
        if (!playback_.Accepts(generation)) return E_ABORT;
        hr = context->SetSelection(cookie, 1, &selection);
        if (FAILED(hr)) return hr;
        if (!playback_.running || !SameObject(composition_.get(), composition.get()))
            return E_ABORT;
#ifdef TSF_PROBE_LIVE_ASR
        live_anchor_ = caret;
        const bool stable = live_packet_.kind == live::Kind::Stable;
        Trace(stable ? L"live-stable-applied" : L"live-partial-applied");
        if (stable) return Finish(cookie, false);
#else
        Trace(playback_.Final() ? L"stable-applied" : L"partial-applied");
        if (playback_.Final()) return Finish(cookie);
        playback_.Applied();
#endif
        return S_OK;
    }
};

thread_local TextService* TextService::guarded_service_ = nullptr;

EditSession::EditSession(TextService* owner, ITfContext* context,
    std::uint64_t generation, bool finish, bool consumes_pending)
    : owner_(owner), context_(context), generation_(generation), finish_(finish), consumes_pending_(consumes_pending) {
    owner_->AddRef(); ++probe_objects;
}
EditSession::~EditSession() { owner_->Release(); --probe_objects; }
HRESULT EditSession::QueryInterface(REFIID iid, void** out) {
    if (!out) return E_POINTER;
    *out = nullptr;
    if (iid != IID_IUnknown && iid != IID_ITfEditSession) return E_NOINTERFACE;
    *out = static_cast<ITfEditSession*>(this); AddRef(); return S_OK;
}
HRESULT EditSession::DoEditSession(TfEditCookie cookie) {
    return owner_->Apply(context_.get(), cookie, generation_, finish_, consumes_pending_);
}

class Factory final : public IClassFactory {
    LONG refs_ = 1;
public:
    Factory() { ++probe_objects; }
    ~Factory() { --probe_objects; }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** out) override {
        if (!out) return E_POINTER;
        *out = nullptr;
        if (iid != IID_IUnknown && iid != IID_IClassFactory) return E_NOINTERFACE;
        *out = static_cast<IClassFactory*>(this); AddRef(); return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return InterlockedIncrement(&refs_); }
    ULONG STDMETHODCALLTYPE Release() override {
        const auto n = InterlockedDecrement(&refs_); if (!n) delete this; return n;
    }
    HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* outer, REFIID iid, void** out) override {
        if (!out) return E_POINTER;
        *out = nullptr;
        if (outer) return CLASS_E_NOAGGREGATION;
        auto* service = new(std::nothrow) TextService();
        if (!service) return E_OUTOFMEMORY;
        const HRESULT hr = service->QueryInterface(iid, out);
        service->Release(); return hr;
    }
    HRESULT STDMETHODCALLTYPE LockServer(BOOL lock) override {
        if (lock) ++probe_objects; else --probe_objects; return S_OK;
    }
};

extern "C" HRESULT WINAPI DllGetClassObject(REFCLSID clsid, REFIID iid, void** out) {
    if (!out) return E_POINTER;
    *out = nullptr;
    if (clsid != probe_clsid) return CLASS_E_CLASSNOTAVAILABLE;
    auto* factory = new(std::nothrow) Factory();
    if (!factory) return E_OUTOFMEMORY;
    const HRESULT hr = factory->QueryInterface(iid, out);
    factory->Release(); return hr;
}
extern "C" HRESULT WINAPI DllCanUnloadNow() {
    return probe_objects == 0 ? S_OK : S_FALSE;
}
BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        probe_module = instance;
        DisableThreadLibraryCalls(instance);
    }
    return TRUE;
}
