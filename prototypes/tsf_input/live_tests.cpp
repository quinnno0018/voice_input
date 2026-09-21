#define TSF_PROBE_TEST
#define TSF_PROBE_LIVE_ASR
#include "text_service.cpp"
#include "test_edit_context.h"
#include <iostream>
#include <cstdlib>

static unsigned checks = 0;
static void Check(bool value, const char* label) {
    if (!value) { std::cerr << "[FAIL] " << label << '\n'; std::exit(1); }
    ++checks;
}

struct InterruptionTests {
    static void LiveLifecycle() {
        TestEditContext context;
        TestComposition composition;
        TestRange range;
        range.context = &context;
        composition.range = &range;
        auto* service = new TextService();
        service->context_ = ComPtr<ITfContext>(&context);
        service->composition_ = ComPtr<ITfComposition>(&composition);
        service->live_anchor_ = ComPtr<ITfRange>(&range);
        service->expected_ = L"partial";
        service->playback_.Start();
        const auto generation = service->playback_.generation;
        Check(service->Finish(1, false) == S_OK, "stable commit succeeds");
        Check(composition.ends == 1 && !service->composition_, "stable ends its composition once");
        Check(service->context_ && service->live_anchor_, "stable retains session target and caret anchor");
        Check(service->playback_.Accepts(generation), "stable leaves microphone input generation live");
        Check(service->expected_.empty(), "next segment never rewrites previous committed text");
        service->Request(false);
        Check(service->edit_pending_ && context.pending.size() == 1, "old update queued before keyboard stop");
        service->StopForKeyboard(&context);
        Check(!service->playback_.running && !service->context_, "keyboard ends live session before passthrough");
        context.Drain();
        Check(!service->edit_pending_ && range.writes == 0, "late live callback cannot write after cancellation");
        Check(!service->live_anchor_ && composition.ends == 1, "cleanup releases anchor without duplicate end");
        service->Release();

        service = new TextService();
        service->context_ = ComPtr<ITfContext>(&context);
        service->composition_ = ComPtr<ITfComposition>(&composition);
        range.document = L"PREFIX" + std::wstring(700, L'x') + L"SUFFIX";
        range.length = 700;
        service->expected_ = std::wstring(700, L'x');
        service->playback_.Start();
        service->OnCompositionTerminated(7, &composition);
        Check(range.writes == 1 && range.write_cookie == 7, "long live partial uses host supplied write cookie");
        Check(range.document == L"PREFIX" + std::wstring(700, L'x') + L"SUFFIX", "owned snapshot survives detach");
        Check(!service->context_ && !service->playback_.running, "host handoff cancels live generation");
        service->Release();
        Check(probe_objects == 0, "live service and edit references released");

        service = new TextService();
        service->context_ = ComPtr<ITfContext>(&context);
        service->playback_.Start();
        BOOL eaten = FALSE;
        service->OnTestKeyDown(&context, VK_CONTROL, 0, &eaten);
        const auto before = service->start_attempts_;
        service->OnPreservedKey(&context, probe_key, &eaten);
        Check(service->start_attempts_ == before && eaten, "stop chord must not restart after its Ctrl key cancelled input");
        service->live_stop_chord_ = true;
        service->OnKeyUp(&context, VK_CONTROL, 0, &eaten);
        Check(!service->live_stop_chord_ && !eaten, "modifier release permits the next intentional activation");
        service->Release();
    }
};

static void Protocol() {
    live::Packet p;
    Check(SUCCEEDED(CoCreateGuid(&p.session)), "session id created");
    p.window = 44;
    Check(live::Valid(p), "valid hello");
    live::Sequence order{p.session, p.window};
    p.kind = live::Kind::Partial; p.sequence = 1; p.segment = 1;
    p.length = 4; memcpy(p.text, L"test", 8);
    Check(order.Accept(p), "first partial accepted");
    Check(!order.Accept(p), "duplicate sequence rejected");
    auto bad = p; bad.sequence = 2; CoCreateGuid(&bad.session);
    Check(!order.Accept(bad), "stale session rejected");
    bad = p; bad.sequence = 2; bad.window = 45;
    Check(!order.Accept(bad), "other HWND rejected");
    bad = p; bad.sequence = 2; bad.segment = 2;
    Check(!order.Accept(bad), "new segment before final rejected");
    p.sequence = 2; p.kind = live::Kind::Stable;
    Check(order.Accept(p), "final accepted");
    order.Applied(p);
    p.sequence = 3;
    Check(!order.Accept(p), "duplicate final with new sequence rejected");
    p.segment = 2;
    Check(order.Accept(p), "next stable-only segment accepted");
    bad = p; bad.length = live::max_text + 1;
    Check(!live::Valid(bad), "oversized text rejected");
    bad = p; bad.version = 2;
    Check(!live::Valid(bad), "unknown wire version rejected");
    bad = p; bad.length = 1; bad.text[0] = 0xd800;
    Check(!live::Valid(bad), "unpaired surrogate rejected");
    bad.text[0] = 0;
    Check(!live::Valid(bad), "embedded NUL rejected");
    bad.text[0] = 0xd83d; bad.text[1] = 0xde00; bad.length = 2;
    Check(live::Valid(bad), "surrogate pair accepted");
}

static void Pipe(bool drop) {
    live::Client client;
    Check(client.Open(reinterpret_cast<HWND>(44)), "native pipe worker created");
    const auto deadline = GetTickCount64() + 8000;
    unsigned count = 0;
    std::wstring committed, interim;
    while (!client.Dead() && GetTickCount64() < deadline) {
        live::Packet p;
        if (client.Take(p)) {
            interim.assign(p.text, p.length);
            if (p.kind == live::Kind::Stable) { committed += interim; interim.clear(); }
            ++count;
            if (!drop) client.Acknowledge();
        }
        Sleep(1);
    }
    const bool ended = client.Dead();
    client.Close();
    const auto cleanup = GetTickCount64() + 2000;
    while (probe_objects && GetTickCount64() < cleanup) Sleep(1);
    Check(ended, "server close detected");
    if (drop) {
        Check(count == 1 && GetTickCount64() < deadline - 4000, "pipe loss detected while native acknowledgment is pending");
    } else {
        Check(count == 4 && committed == L"final.next." && interim.empty(), "two segments arrive without duplicate text");
    }
    Check(probe_objects == 0, "worker cancelled and references released");
}

int main(int argc, char** argv) {
    Check(SUCCEEDED(CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED)), "test COM initialized");
    if (argc == 2 && std::string_view(argv[1]) == "--pipe") Pipe(false);
    else if (argc == 2 && std::string_view(argv[1]) == "--pipe-drop") Pipe(true);
    else { Protocol(); InterruptionTests::LiveLifecycle(); }
    CoUninitialize();
    std::cout << checks << " live bridge checks passed. No registration or microphone used.\n";
}
