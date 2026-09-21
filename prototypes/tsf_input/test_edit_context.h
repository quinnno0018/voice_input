#pragma once
#include "probe.h"
#include <vector>
#include <string>
#include <functional>

// A controllable TSF scheduler, not an emulation of Chrome's renderer.
struct TestEditContext final : ITfContext {
    ULONG refs = 1;
    HRESULT sync_result = S_OK, request_result = S_OK;
    std::vector<DWORD> requests;
    std::vector<ComPtr<ITfEditSession>> pending;
    unsigned selection_writes = 0;
    HRESULT QueryInterface(REFIID iid, void** out) override {
        *out = nullptr;
        if (iid != IID_IUnknown && iid != IID_ITfContext) return E_NOINTERFACE;
        *out = static_cast<ITfContext*>(this); AddRef(); return S_OK;
    }
    ULONG AddRef() override { return ++refs; }
    ULONG Release() override { return --refs; }
    HRESULT RequestEditSession(TfClientId, ITfEditSession* edit, DWORD flags, HRESULT* result) override {
        requests.push_back(flags);
        if (FAILED(request_result)) return request_result;
        if (flags & TF_ES_SYNC) {
            *result = FAILED(sync_result) ? sync_result : edit->DoEditSession(1);
        } else {
            pending.emplace_back(edit);
            *result = TF_S_ASYNC;
        }
        return S_OK;
    }
    void Drain() {
        auto queue = std::move(pending);
        pending.clear();
        for (auto& edit : queue) edit->DoEditSession(1);
    }
    HRESULT InWriteSession(TfClientId, BOOL*) override { return E_NOTIMPL; }
    HRESULT GetSelection(TfEditCookie, ULONG, ULONG, TF_SELECTION*, ULONG*) override { return E_NOTIMPL; }
    HRESULT SetSelection(TfEditCookie, ULONG, const TF_SELECTION*) override { ++selection_writes; return E_NOTIMPL; }
    HRESULT GetStart(TfEditCookie, ITfRange**) override { return E_NOTIMPL; }
    HRESULT GetEnd(TfEditCookie, ITfRange**) override { return E_NOTIMPL; }
    HRESULT GetActiveView(ITfContextView**) override { return E_NOTIMPL; }
    HRESULT EnumViews(IEnumTfContextViews**) override { return E_NOTIMPL; }
    HRESULT GetStatus(TF_STATUS*) override { return E_NOTIMPL; }
    HRESULT GetProperty(REFGUID, ITfProperty**) override { return E_NOTIMPL; }
    HRESULT GetAppProperty(REFGUID, ITfReadOnlyProperty**) override { return E_NOTIMPL; }
    HRESULT TrackProperties(const GUID**, ULONG, const GUID**, ULONG, ITfReadOnlyProperty**) override { return E_NOTIMPL; }
    HRESULT EnumProperties(IEnumTfProperties**) override { return E_NOTIMPL; }
    HRESULT GetDocumentMgr(ITfDocumentMgr**) override { return E_NOTIMPL; }
    HRESULT CreateRangeBackup(TfEditCookie, ITfRange*, ITfRangeBackup**) override { return E_NOTIMPL; }
};

// Only the owned span is writable; this double does not model renderer commit.
struct TestRange final : ITfRange {
    ULONG refs = 1;
    ITfContext* context = nullptr;
    std::wstring document = L"PREFIXpartialSUFFIX";
    ULONG start = 6, length = 7;
    unsigned writes = 0;
    TfEditCookie read_cookie = 0, write_cookie = 0;
    HRESULT read_result = S_OK, write_result = S_OK;
    std::function<void()> on_write;
    HRESULT QueryInterface(REFIID iid, void** out) override {
        *out = nullptr;
        if (iid != IID_IUnknown && iid != IID_ITfRange) return E_NOINTERFACE;
        *out = static_cast<ITfRange*>(this); AddRef(); return S_OK;
    }
    ULONG AddRef() override { return ++refs; }
    ULONG Release() override { return --refs; }
    HRESULT GetText(TfEditCookie cookie, DWORD, WCHAR* out, ULONG capacity, ULONG* count) override {
        read_cookie = cookie;
        if (FAILED(read_result)) return read_result;
        *count = length < capacity ? length : capacity;
        document.copy(out, *count, start);
        return S_OK;
    }
    HRESULT SetText(TfEditCookie cookie, DWORD, const WCHAR* text, LONG count) override {
        ++writes; write_cookie = cookie;
        if (FAILED(write_result)) return write_result;
        document.replace(start, length, text, static_cast<size_t>(count));
        length = static_cast<ULONG>(count);
        if (on_write) on_write();
        return S_OK;
    }
    HRESULT GetContext(ITfContext** out) override {
        *out = context; if (!context) return E_FAIL; context->AddRef(); return S_OK;
    }
    HRESULT GetFormattedText(TfEditCookie, IDataObject**) override { return E_NOTIMPL; }
    HRESULT GetEmbedded(TfEditCookie, REFGUID, REFIID, IUnknown**) override { return E_NOTIMPL; }
    HRESULT InsertEmbedded(TfEditCookie, DWORD, IDataObject*) override { return E_NOTIMPL; }
    HRESULT ShiftStart(TfEditCookie, LONG, LONG*, const TF_HALTCOND*) override { return E_NOTIMPL; }
    HRESULT ShiftEnd(TfEditCookie, LONG, LONG*, const TF_HALTCOND*) override { return E_NOTIMPL; }
    HRESULT ShiftStartToRange(TfEditCookie, ITfRange*, TfAnchor) override { return E_NOTIMPL; }
    HRESULT ShiftEndToRange(TfEditCookie, ITfRange*, TfAnchor) override { return E_NOTIMPL; }
    HRESULT ShiftStartRegion(TfEditCookie, TfShiftDir, BOOL*) override { return E_NOTIMPL; }
    HRESULT ShiftEndRegion(TfEditCookie, TfShiftDir, BOOL*) override { return E_NOTIMPL; }
    HRESULT IsEmpty(TfEditCookie, BOOL* empty) override { *empty = length == 0; return S_OK; }
    HRESULT Collapse(TfEditCookie, TfAnchor) override { return E_NOTIMPL; }
    HRESULT IsEqualStart(TfEditCookie, ITfRange*, TfAnchor, BOOL*) override { return E_NOTIMPL; }
    HRESULT IsEqualEnd(TfEditCookie, ITfRange*, TfAnchor, BOOL*) override { return E_NOTIMPL; }
    HRESULT CompareStart(TfEditCookie, ITfRange*, TfAnchor, LONG*) override { return E_NOTIMPL; }
    HRESULT CompareEnd(TfEditCookie, ITfRange*, TfAnchor, LONG*) override { return E_NOTIMPL; }
    HRESULT AdjustForInsert(TfEditCookie, ULONG, BOOL*) override { return E_NOTIMPL; }
    HRESULT GetGravity(TfGravity*, TfGravity*) override { return E_NOTIMPL; }
    HRESULT SetGravity(TfEditCookie, TfGravity, TfGravity) override { return E_NOTIMPL; }
    HRESULT Clone(ITfRange**) override { return E_NOTIMPL; }
};

struct TestComposition final : ITfComposition {
    ULONG refs = 1;
    unsigned ends = 0;
    HRESULT end_result = S_OK;
    ITfRange* range = nullptr;
    HRESULT QueryInterface(REFIID iid, void** out) override {
        *out = nullptr;
        if (iid != IID_IUnknown && iid != IID_ITfComposition) return E_NOINTERFACE;
        *out = static_cast<ITfComposition*>(this); AddRef(); return S_OK;
    }
    ULONG AddRef() override { return ++refs; }
    ULONG Release() override { return --refs; }
    HRESULT GetRange(ITfRange** out) override {
        *out = range; if (!range) return E_NOTIMPL; range->AddRef(); return S_OK;
    }
    HRESULT ShiftStart(TfEditCookie, ITfRange*) override { return E_NOTIMPL; }
    HRESULT ShiftEnd(TfEditCookie, ITfRange*) override { return E_NOTIMPL; }
    HRESULT EndComposition(TfEditCookie) override { ++ends; return end_result; }
};
