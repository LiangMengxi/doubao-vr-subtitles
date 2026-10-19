#include <windows.h>
#include "bridge.h"
#include <chrono>
#include <iostream>
#include <stdexcept>
#include <thread>
#include <memory>

using Clock = std::chrono::steady_clock;

class ScopedBridge {
public:
    BridgeId value = bridge_create();
    ~ScopedBridge() { bridge_destroy(value); }
    ScopedBridge() = default;
    ScopedBridge(const ScopedBridge&) = delete;
    ScopedBridge& operator=(const ScopedBridge&) = delete;
};

class ScopedWindow {
public:
    HWND value = CreateWindowExW(0, L"STATIC", L"DoubaoVR capture validation", WS_OVERLAPPEDWINDOW | WS_VISIBLE,
        CW_USEDEFAULT, CW_USEDEFAULT, 400, 180, nullptr, nullptr, GetModuleHandleW(nullptr), nullptr);
    ~ScopedWindow() { if (value) DestroyWindow(value); }
    ScopedWindow() = default;
    ScopedWindow(const ScopedWindow&) = delete;
    ScopedWindow& operator=(const ScopedWindow&) = delete;
};

BridgeStats wait_frame(BridgeId bridge, uint64_t previous) {
    auto deadline = Clock::now() + std::chrono::seconds(8);
    while (Clock::now() < deadline) {
        MSG message{};
        while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) {
            TranslateMessage(&message);
            DispatchMessageW(&message);
        }
        BridgeStats stats{sizeof(BridgeStats)};
        if (bridge_stats(bridge, stats) < 0) throw std::runtime_error("Stats failed.");
        if (stats.state == 3) {
            BridgeError error;
            bridge_error(bridge, error);
            std::wcerr << error.text.data() << L"\n";
            throw std::runtime_error("Capture worker reported an error.");
        }
        if (stats.state == 2 && stats.frames > previous) return stats;
        std::this_thread::sleep_for(std::chrono::milliseconds(10));
    }
    throw std::runtime_error("Timed out waiting for a captured frame.");
}

int main() {
    ScopedWindow window;
    ScopedBridge bridge;
    if (!window.value || !bridge.value) return 1;
    int result = 1;
    try {
        BridgeOptions options{sizeof(BridgeOptions), 1, 1.2f, 0, -.32f, 1, 0, 0, 0, 1, 1, 0, 1};
        if (bridge_start(bridge.value, reinterpret_cast<uintptr_t>(window.value), options) < 0) throw std::runtime_error("Start failed.");
        auto first = wait_frame(bridge.value, 0);
        std::cout << "First frame: " << first.width << "x" << first.height << "\n";
        SetWindowPos(window.value, nullptr, 0, 0, 600, 240, SWP_NOMOVE | SWP_NOZORDER);
        auto resized = wait_frame(bridge.value, first.frames);
        auto deadline = Clock::now() + std::chrono::seconds(3);
        int repaint = 0;
        while (resized.width == first.width && Clock::now() < deadline) {
            auto text = std::wstring(L"Resized capture ") + std::to_wstring(++repaint);
            SetWindowTextW(window.value, text.c_str());
            resized = wait_frame(bridge.value, resized.frames);
        }
        if (resized.width == first.width) throw std::runtime_error("Resize was not propagated.");
        options.crop_top = 44;
        if (bridge_configure(bridge.value, options) < 0) throw std::runtime_error("Configure failed.");
        std::this_thread::sleep_for(std::chrono::milliseconds(200));
        BridgeStats cropped{sizeof(BridgeStats)};
        bridge_stats(bridge.value, cropped);
        if (cropped.height + 44 != resized.height) throw std::runtime_error("Crop changes did not update a static captured frame.");
        auto preview = std::make_unique<BridgePreview>();
        if (bridge_preview(bridge.value, *preview) < 0 || !preview->width || !preview->sequence) throw std::runtime_error("Bounded preview was not delivered.");
        bridge_stop(bridge.value);
        bridge_stop(bridge.value);
        BridgeStats stopped{sizeof(BridgeStats)};
        bridge_stats(bridge.value, stopped);
        if (stopped.state != 0) throw std::runtime_error("Stop failed.");
        for (int count = 0; count < 3; ++count) {
            options.border = static_cast<uint32_t>(count % 2);
            if (bridge_start(bridge.value, reinterpret_cast<uintptr_t>(window.value), options) < 0) throw std::runtime_error("Restart failed.");
            wait_frame(bridge.value, 0);
            bridge_stop(bridge.value);
        }
        std::cout << "PASS: live WGC, resize, crop configuration, bounded preview and repeated capture teardown.\n";
        result = 0;
    } catch (const std::exception& exception) { std::cerr << exception.what() << "\n"; }
    return result;
}
