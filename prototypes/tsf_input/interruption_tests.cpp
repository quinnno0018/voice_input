#define TSF_PROBE_TEST
#include "text_service.cpp"
#include <iostream>
#include <stdexcept>
#include "test_edit_context.h"

static unsigned passed = 0;
static void Check(bool condition, const char* name) {
    if (!condition) throw std::runtime_error(name);
    ++passed;
    std::cout << "[PASS] " << name << '\n';
}

struct InterruptionTests {
    static void HostHandoffChecks() {
        for (bool queued : {false, true}) {
            TestEditContext context;
            TestRange range;
            range.context = &context;
            TestComposition composition;
            composition.range = &range;
            TextService service;
            service.playback_.Start();
            service.context_ = ComPtr<ITfContext>(&context);
            service.composition_ = ComPtr<ITfComposition>(&composition);
            service.expected_ = L"partial";
            if (queued) service.Request(false);
            service.HaltPlayback();
            const auto posted_generation = service.playback_.generation;
            range.on_write = [&] { service.OnCompositionTerminated(73, &composition); };
            service.OnCompositionTerminated(73, &composition);
            Check(range.writes == 1 && range.read_cookie == 73 && range.write_cookie == 73,
                "host termination reaffirms the owned text once using the supplied write cookie");
            Check(range.document == L"PREFIXpartialSUFFIX" && context.selection_writes == 0,
                "host handoff preserves prefix suffix and selection");
            Check(!service.context_ && !service.composition_ && !service.playback_.running &&
                service.playback_.generation != posted_generation && composition.ends == 0,
                "host handoff invalidates old completion without ending composition twice");
            Check(context.requests.size() == (queued ? 1u : 0u) && service.edit_pending_ == queued,
                "host handoff uses no new edit session and leaves queued slot ownership intact");
            context.Drain();
            service.OnCompositionTerminated(73, &composition);
            Check(range.writes == 1 && !service.edit_pending_ && context.selection_writes == 0,
                "late queued frames and duplicate termination cannot write again");
            Check(context.refs == 1 && composition.refs == 1 && range.refs == 1,
                "host handoff releases all old target references");
        }
        for (int scenario = 0; scenario < 8; ++scenario) {
            TestEditContext context, other_context;
            TestRange range;
            range.context = &context;
            TestComposition composition, other_composition;
            composition.range = &range;
            TextService service;
            service.playback_.Start();
            service.context_ = ComPtr<ITfContext>(&context);
            service.composition_ = ComPtr<ITfComposition>(&composition);
            service.expected_ = L"partial";
            if (scenario == 0) range.document = L"PREFIXchangedSUFFIX";
            if (scenario == 1) { range.document = L"PREFIXSUFFIX"; range.length = 0; }
            if (scenario == 2) range.context = &other_context;
            if (scenario == 3) range.read_result = E_FAIL;
            if (scenario == 4) service.expected_ = {};
            if (scenario == 5) service.writing_ = true;
            if (scenario == 6) composition.range = nullptr;
            const auto before = range.document;
            service.OnCompositionTerminated(73, scenario == 7 ? &other_composition : &composition);
            Check(range.writes == 0 && range.document == before && context.selection_writes == 0,
                "unsafe unavailable empty modified or foreign host ranges are never overwritten");
            Check(scenario == 7 ? service.playback_.running : !service.context_,
                "foreign termination is ignored while owned rejected termination still cleans up");
        }
        TestEditContext context;
        TestRange range;
        range.context = &context;
        range.write_result = E_FAIL;
        TestComposition composition;
        composition.range = &range;
        TextService service;
        service.playback_.Start();
        service.context_ = ComPtr<ITfContext>(&context);
        service.composition_ = ComPtr<ITfComposition>(&composition);
        service.expected_ = L"partial";
        service.OnCompositionTerminated(73, &composition);
        Check(range.writes == 1 && !service.context_ && !service.playback_.running && context.requests.empty(),
            "failed host reaffirmation stops safely without retry or new-target replay");
    }
    static void KeyboardHandoffChecks() {
        TestEditContext context;
        TestComposition composition;
        TextService service;
        service.playback_.Start();
        service.context_ = ComPtr<ITfContext>(&context);
        service.composition_ = ComPtr<ITfComposition>(&composition);
        BOOL eaten = TRUE;
        service.OnKeyDown(&context, '2', 0, &eaten);
        const bool ended_before_key_return = composition.ends == 1 && !service.composition_;
        context.Drain();
        Check(ended_before_key_return, "keyboard composition ends before the actual key callback returns");
        Check(!eaten && context.selection_writes == 0, "keyboard handoff neither eats the key nor moves selection");
        Check(context.requests.front() == (TF_ES_SYNC | TF_ES_READWRITE), "only keyboard handoff requests a synchronous edit");

        for (bool actual : {false, true}) {
            TestEditContext queued_context;
            TestComposition queued_composition;
            TextService queued_service;
            queued_service.playback_.Start();
            queued_service.context_ = ComPtr<ITfContext>(&queued_context);
            queued_service.composition_ = ComPtr<ITfComposition>(&queued_composition);
            queued_service.Request(false);
            Check(queued_service.edit_pending_ && queued_context.pending.size() == 1,
                "ordinary frame remains asynchronous before keyboard interruption");
            if (actual) queued_service.OnKeyDown(&queued_context, '3', 0, &eaten);
            else queued_service.OnTestKeyDown(&queued_context, '3', 0, &eaten);
            Check(queued_composition.ends == 1 && !queued_service.context_ && !eaten,
                "test and actual key paths finish despite an older queued frame");
            Check(queued_service.edit_pending_, "sync finish does not steal the queued frame pending slot");
            queued_service.Start(&queued_context);
            Check(!queued_service.playback_.running && !queued_service.context_,
                "restart waits for the previous queued callback to retire");
            queued_context.Drain();
            Check(!queued_service.edit_pending_ && queued_composition.ends == 1 &&
                queued_service.playback_.index == 0 && queued_context.selection_writes == 0,
                "late frame retires without text updates, cursor movement or double completion");
            Check(queued_context.refs == 1 && queued_composition.refs == 1,
                "sync and queued edit references are released");
        }

        for (bool queued : {false, true}) {
            TestEditContext denied_context;
            TestComposition denied_composition;
            TextService denied_service;
            denied_service.playback_.Start();
            denied_service.context_ = ComPtr<ITfContext>(&denied_context);
            denied_service.composition_ = ComPtr<ITfComposition>(&denied_composition);
            if (queued) denied_service.Request(false);
            denied_context.sync_result = TF_E_SYNCHRONOUS;
            denied_service.OnKeyDown(&denied_context, '2', 0, &eaten);
            Check(!eaten && !denied_service.playback_.running && denied_composition.ends == 0,
                "denied sync stops playback but preserves the user's key");
            Check(denied_context.requests.size() == 2 && denied_context.pending.size() == 1,
                "denied sync retains exactly one async cleanup and never loops");
            denied_context.Drain();
            Check(denied_composition.ends == 1 && !denied_service.context_ && !denied_service.edit_pending_,
                "denied sync eventually cleans up without replaying a frame");
        }

        for (bool request_failure : {false, true}) {
            TestEditContext failed_context;
            TestComposition failed_composition;
            TextService failed_service;
            failed_service.playback_.Start();
            failed_service.context_ = ComPtr<ITfContext>(&failed_context);
            failed_service.composition_ = ComPtr<ITfComposition>(&failed_composition);
            if (request_failure) failed_context.request_result = E_FAIL;
            else failed_composition.end_result = E_FAIL;
            failed_service.OnKeyDown(&failed_context, '2', 0, &eaten);
            Check(!eaten && !failed_service.playback_.running && failed_context.requests.size() == 2,
                "request and completion failures use bounded fallback without swallowing input");
            failed_context.request_result = S_OK;
            failed_composition.end_result = S_OK;
            failed_context.Drain();
            if (failed_service.context_) failed_service.Stop(L"test-cleanup");
            failed_context.Drain();
            Check(!failed_service.edit_pending_ && !failed_service.context_, "failure cleanup leaves no queued slot stuck");
        }

        TestEditContext pointer_context;
        TestComposition pointer_composition;
        TextService pointer_service;
        pointer_service.playback_.Start();
        pointer_service.context_ = ComPtr<ITfContext>(&pointer_context);
        pointer_service.composition_ = ComPtr<ITfComposition>(&pointer_composition);
        pointer_service.Stop(L"test-pointer-interruption");
        Check(pointer_context.requests.size() == 1 &&
            pointer_context.requests.front() == (TF_ES_ASYNC | TF_ES_READWRITE) && pointer_composition.ends == 0,
            "non-keyboard cancellation keeps asynchronous completion");
        pointer_context.Drain();

        TestEditContext old_context, other_context;
        TestComposition old_composition;
        TextService switched_service;
        switched_service.playback_.Start();
        switched_service.context_ = ComPtr<ITfContext>(&old_context);
        switched_service.composition_ = ComPtr<ITfComposition>(&old_composition);
        switched_service.OnKeyDown(&other_context, '2', 0, &eaten);
        Check(!eaten && other_context.requests.empty() &&
            old_context.requests.front() == (TF_ES_ASYNC | TF_ES_READWRITE),
            "different key target cannot receive a sync edit from the old session");
        old_context.Drain();
    }
#ifdef TSF_PROBE_HANDOFF_TRACE
    static void HandoffTraceChecks() {
        TextService service;
        service.playback_.Start();
        service.BeginHandoffTrace();
        BOOL eaten = TRUE;
        service.OnKeyDown(nullptr, '2', 0, &eaten);
        Check(!eaten && !service.playback_.running, "diagnostics preserve key passthrough and cancellation");
        const auto& events = service.handoff_;
        Check(events.count >= 4 && events.entries[0].stage == probe::HandoffStage::KeyEnter &&
            events.entries[1].stage == probe::HandoffStage::TestEnter &&
            events.entries[events.count - 2].stage == probe::HandoffStage::TestExit &&
            events.entries[events.count - 1].stage == probe::HandoffStage::KeyExit,
            "handoff trace distinguishes nested actual and test key callbacks");
        Check(events.entries[0].running && !events.entries[events.count - 1].running,
            "handoff trace observes state before and after cancellation");
        for (unsigned i = 1; i < events.count; ++i)
            Check(events.entries[i].qpc >= events.entries[i - 1].qpc, "handoff timestamps are ordered");
        Check(events.deadline > 0, "cancellation bounds diagnostics to a short handoff tail");
        const unsigned before = events.count;
        service.handoff_.deadline = 1;
        service.Handoff(probe::HandoffStage::TestEnter);
        Check(events.count == before, "expired handoff does not retain subsequent input activity");
        service.handoff_.deadline = 0;
        for (unsigned i = 0; i < 300; ++i) service.Handoff(probe::HandoffStage::EditEnter);
        Check(events.count == probe::HandoffTrace::capacity && events.dropped > 0,
            "handoff trace has bounded storage and explicit overflow");
        service.handoff_.Reset();
        Check(events.count == 0 && events.dropped == 0, "new simulation resets diagnostic storage");
        service.handoff_.Add({probe::HandoffStage::HostTermination, 0, UINT64_MAX, UINT_MAX,
            false, false, false, E_FAIL, LONG_MIN, LONG_MAX, LONG_MIN, LONG_MAX});
        service.FlushHandoffTrace();
        Check(!events.active, "maximum-width numeric trace flush completes and closes capture");
    }
#endif
    static void NativeMessages(TextService& service) {
        WNDCLASSW wc{};
        wc.hInstance = probe_module;
        wc.lpfnWndProc = TextService::WindowProc;
        wc.lpszClassName = window_class;
        Check(RegisterClassW(&wc) != 0, "test message window class registers");
        service.timer_window_ = CreateWindowExW(0, window_class, L"", 0,
            0, 0, 0, 0, HWND_MESSAGE, nullptr, probe_module, &service);
        Check(service.timer_window_ != nullptr, "private cleanup window created");
        HWND edit = CreateWindowExW(0, L"EDIT", L"prefix:", ES_AUTOHSCROLL,
            0, 0, 300, 30, nullptr, nullptr, probe_module, nullptr);
        Check(edit != nullptr, "private edit control created without foreground activation");
        SendMessageW(edit, EM_SETSEL, 7, 7);

        service.playback_.Start();
        auto ticket = service.playback_.generation;
        Check(service.ArmInputGuard() == S_OK, "current-thread guard arms");
        Check(service.ArmInputGuard() == E_UNEXPECTED, "duplicate guard is rejected");
        MSG message{};
        Check(PostMessageW(edit, WM_KEYDOWN, '2', 123), "keyboard event queued locally");
        Check(PeekMessageW(&message, edit, WM_KEYDOWN, WM_KEYDOWN, PM_NOREMOVE) &&
            service.playback_.Accepts(ticket), "peek-only does not cancel or consume input");
        Check(PeekMessageW(&message, edit, WM_KEYDOWN, WM_KEYDOWN, PM_REMOVE) &&
            message.message == WM_KEYDOWN && message.wParam == '2' && message.lParam == 123,
            "removed keyboard message is returned unchanged");
        Check(!service.playback_.Accepts(ticket), "queued key cancels before host dispatch");
        Check(!service.input_hook_ && !TextService::guarded_service_, "guard disarms after first input");
        Check(service.Apply(nullptr, 0, ticket, false) == S_OK && service.playback_.index == 0,
            "late output edit after cancellation only finishes, never advances a frame");

        // The old cleanup message must not cancel the next explicitly started run.
        service.playback_.Start();
        ticket = service.playback_.generation;
        Check(service.ArmInputGuard() == S_OK, "new run rearms guard");
        while (PeekMessageW(&message, service.timer_window_, complete_input_stop, complete_input_stop, PM_REMOVE))
            DispatchMessageW(&message);
        Check(service.playback_.Accepts(ticket), "stale cleanup message cannot stop a new run");

        for (wchar_t ch : std::wstring_view(L"234dddf")) {
            Check(PostMessageW(edit, WM_CHAR, ch, 1), "character queued in user order");
        }
        std::wstring delivered;
        while (PeekMessageW(&message, edit, WM_CHAR, WM_CHAR, PM_REMOVE)) {
            Check(!service.playback_.running, "character-only path cancels before dispatch");
            delivered += static_cast<wchar_t>(message.wParam);
            DispatchMessageW(&message);
        }
        wchar_t text[128]{};
        GetWindowTextW(edit, text, 128);
        Check(delivered == L"234dddf" && std::wstring_view(text) == L"prefix:234dddf",
            "characters are neither swallowed nor reversed and existing text survives");

        for (UINT event : {WM_KEYDOWN, WM_SYSKEYDOWN, WM_LBUTTONDOWN, WM_LBUTTONDBLCLK,
                          WM_RBUTTONDOWN, WM_MOUSEWHEEL}) {
            service.playback_.Start();
            ticket = service.playback_.generation;
            Check(service.ArmInputGuard() == S_OK, "independent interruption run arms");
            Check(PostMessageW(edit, event, VK_LEFT, 0) &&
                PeekMessageW(&message, edit, event, event, PM_REMOVE), "input event retrieved");
            Check(!service.playback_.Accepts(ticket) && message.message == event && message.wParam == VK_LEFT,
                "keyboard or pointer event stops output without modifying the message");
        }

        // A fabricated pointer ID is not a deliverable Windows pointer event.
        Check(TextService::IsInputMessage(WM_POINTERDOWN, 0), "pointer-down classification cancels");
        service.playback_.Start();
        ticket = service.playback_.generation;
        Check(service.ArmInputGuard() == S_OK, "non-input checks arm guard");
        for (UINT event : {WM_MOUSEMOVE, WM_KEYUP, WM_SYSKEYUP, WM_TIMER}) {
            Check(PostMessageW(edit, event, 0, 0) && PeekMessageW(&message, edit, event, event, PM_REMOVE) &&
                service.playback_.Accepts(ticket), "hover, releases and timers do not interrupt");
        }
        Check(!TextService::IsInputMessage(WM_UNICHAR, UNICODE_NOCHAR),
            "Unicode capability query is not user input");
        service.Finish(0);
        Check(!service.input_hook_ && !TextService::guarded_service_, "finalization removes guard");
        service.playback_.Start();
        Check(service.ArmInputGuard() == S_OK, "host termination run arms");
        service.OnCompositionTerminated(0, nullptr);
        Check(!service.playback_.running && !service.input_hook_, "host termination removes guard");
        service.playback_.Start();
        Check(service.ArmInputGuard() == S_OK, "deactivation run arms");
        service.Deactivate();
        Check(!service.playback_.running && !service.input_hook_ && !service.timer_window_,
            "deactivation removes guard and cleanup window");
        DestroyWindow(edit);
    }

    static void Run() {
        TextService service;
        service.playback_.Start();
        const auto ticket = service.playback_.generation;
        BOOL eaten = TRUE;
        Check(service.OnKeyDown(nullptr, '2', 0, &eaten) == S_OK && !eaten,
            "actual key callback passes the user's key through");
        Check(!service.playback_.Accepts(ticket),
            "actual key callback cancels queued output without TestKeyDown");
        service.playback_.Start();
        service.OnKeyDown(nullptr, VK_LEFT, 0, &eaten);
        Check(!service.playback_.running && !eaten, "arrow key cancels without being eaten");
        service.playback_.Start();
        service.OnTestKeyDown(nullptr, '2', 0, &eaten);
        Check(!service.playback_.running && !eaten, "existing test-key callback still cancels");
        NativeMessages(service);
    }
};

int main() {
    try {
        probe_module = GetModuleHandleW(nullptr);
        InterruptionTests::KeyboardHandoffChecks();
        InterruptionTests::HostHandoffChecks();
        InterruptionTests::Run();
#ifdef TSF_PROBE_HANDOFF_TRACE
        InterruptionTests::HandoffTraceChecks();
#endif
        Check(probe_objects == 0, "test service references are released");
        std::cout << passed << " interruption checks passed. No system registration performed.\n";
        return 0;
    } catch (const std::exception& error) {
        std::cerr << "[FAIL] " << error.what() << '\n';
        return 1;
    }
}
