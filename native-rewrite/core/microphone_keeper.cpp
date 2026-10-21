#include "microphone_keeper.h"
#include "platform.h"
#include <algorithm>
#include <chrono>
#include <condition_variable>
#include <string>

struct MicrophoneKeeperState {
    mutable std::mutex mutex;
    std::condition_variable_any wake;
    BridgeMicrophoneStats stats;
};

namespace {
void update(const std::shared_ptr<MicrophoneKeeperState>& state, uint32_t value,
    std::wstring_view device = {}, std::wstring_view error = {}) {
    std::lock_guard lock(state->mutex);
    state->stats.state = value;
    state->stats.device.fill(0);
    state->stats.error.fill(0);
    std::copy_n(device.begin(), std::min(device.size(), state->stats.device.size() - 1), state->stats.device.begin());
    std::copy_n(error.begin(), std::min(error.size(), state->stats.error.size() - 1), state->stats.error.begin());
}

void wait_retry(const std::shared_ptr<MicrophoneKeeperState>& state, std::stop_token stop) {
    std::unique_lock lock(state->mutex);
    state->wake.wait_for(lock, stop, std::chrono::seconds(2), [] { return false; });
}

void consume_microphone(const std::shared_ptr<MicrophoneKeeperState>& state, std::stop_token stop) noexcept {
    try {
        platform::ApartmentScope apartment;
        while (!stop.stop_requested()) {
            try {
                auto devices = platform::audio_devices(1);
                std::ranges::sort(devices, {}, &platform::AudioDevice::id);
                const auto target = std::ranges::find_if(devices, [](const auto& device) { return is_pico_microphone(device.name); });
                if (target == devices.end()) {
                    update(state, 1);
                    wait_retry(state, stop);
                    continue;
                }
                if (stop.stop_requested()) break;
                platform::MicrophoneCapture capture(target->id);
                update(state, 1, target->name);
                while (!stop.stop_requested()) {
                    capture.wait(200);
                    if (stop.stop_requested()) break;
                    const auto progress = capture.consume();
                    if (progress.packets == 0) continue;
                    std::lock_guard lock(state->mutex);
                    state->stats.state = 2;
                    state->stats.frames += progress.frames;
                    state->stats.packets += progress.packets;
                }
            } catch (const winrt::hresult_error& failure) {
                update(state, 3, {}, failure.message());
                wait_retry(state, stop);
            } catch (const std::exception& failure) {
                update(state, 3, {}, winrt::to_hstring(failure.what()));
                wait_retry(state, stop);
            }
        }
    } catch (...) {
        try { update(state, 3, {}, L"无法初始化 PICO 麦克风服务。"); } catch (...) { }
    }
}
}

MicrophoneKeeper::MicrophoneKeeper() : state(std::make_shared<MicrophoneKeeperState>()) {}
MicrophoneKeeper::~MicrophoneKeeper() { try { enable(false); } catch (...) { } }

void MicrophoneKeeper::enable(bool enabled) {
    std::lock_guard control_lock(control);
    if (enabled) {
        if (worker.joinable()) return;
        {
            std::lock_guard lock(state->mutex);
            state->stats = {};
            state->stats.enabled = 1;
            state->stats.state = 1;
        }
        try { worker = std::jthread([shared = state](std::stop_token stop) { consume_microphone(shared, stop); }); }
        catch (...) { std::lock_guard lock(state->mutex); state->stats = {}; throw; }
    } else {
        if (worker.joinable()) { worker.request_stop(); state->wake.notify_all(); worker.join(); }
        std::lock_guard lock(state->mutex);
        state->stats = {};
    }
}

BridgeMicrophoneStats MicrophoneKeeper::snapshot() const {
    std::lock_guard lock(state->mutex);
    return state->stats;
}
