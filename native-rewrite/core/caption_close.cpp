#include "bridge.h"
#include "platform.h"
#include <algorithm>
#include <cstddef>

static_assert(sizeof(BridgeCaptionButton) == 64);
static_assert(offsetof(BridgeCaptionButton, horizontal) == 48);

namespace {
template<typename Action> int32_t caption_call(BridgeError& error, Action action) noexcept {
    if (error.size != sizeof(BridgeError)) return E_INVALIDARG;
    error.text.fill(0);
    try { return action() ? S_OK : S_FALSE; }
    catch (...) {
        const auto code = winrt::to_hresult();
        try {
            const auto message = platform::error_text(L"关闭豆包字幕", code, std::wstring(winrt::to_message()));
            std::copy_n(message.begin(), std::min(message.size(), error.text.size() - 1), error.text.begin());
        } catch (...) { }
        return code;
    }
}
}

int32_t __cdecl bridge_caption_button(uintptr_t window, uint32_t process_id, BridgeCaptionButton& button, BridgeError& error) noexcept {
    if (button.size != sizeof(BridgeCaptionButton)) return E_INVALIDARG;
    button = {};
    return caption_call(error, [&] { return platform::caption_button(window, process_id, button); });
}

int32_t __cdecl bridge_caption_close(const BridgeCaptionButton& button, BridgeError& error) noexcept {
    return caption_call(error, [&] {
        if (button.size != sizeof(BridgeCaptionButton) || button.reserved != 0) throw winrt::hresult_invalid_argument();
        return platform::close_caption(button);
    });
}
