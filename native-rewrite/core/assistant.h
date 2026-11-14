#pragma once
#include "bridge.h"
#include "spatial_selection.h"
#include <openvr.h>
#include <memory>
#include <mutex>
#include <thread>
#include <atomic>
#include <vector>

struct AssistantMailbox {
    std::mutex mutex;
    uint32_t state = 0, width = 0, height = 0;
    uint64_t sequence = 0;
    std::wstring message;
    std::vector<uint8_t> pixels;
    void update(uint32_t next, std::wstring text) {
        std::lock_guard lock(mutex);
        state = next;
        message = std::move(text);
    }
};

struct AssistantState {
    uint64_t overlay = 0;
    BridgeAssistantRequest request{};
    std::shared_ptr<AssistantMailbox> mailbox;
    std::mutex visibility;
    std::atomic<bool> suppress{false};
    std::atomic<uint32_t> image_width{0}, image_height{0};
    std::atomic<float> panel_width{.7f};
    std::atomic<bool> reset_on_dashboard{false};
    std::atomic<bool> reset_requested{false};
};

class AssistantInteraction {
public:
    AssistantInteraction(uint64_t overlay, BridgeAssistantRequest request, std::shared_ptr<AssistantMailbox> mailbox);
    ~AssistantInteraction();
    AssistantInteraction(const AssistantInteraction&) = delete;
    AssistantInteraction& operator=(const AssistantInteraction&) = delete;
    bool suppressed() const { return state->suppress.load(); }
    void dimensions(uint32_t width, uint32_t height) { state->image_width = width; state->image_height = height; }
    void set_width(float width) noexcept { state->panel_width = width; }
    void set_reset_on_dashboard(bool enabled) noexcept { state->reset_on_dashboard.store(enabled); }
    void reset_position() noexcept { state->reset_requested.store(true); }
    std::mutex& visibility_mutex() noexcept { return state->visibility; }
private:
    static void run(std::stop_token stop, std::shared_ptr<AssistantState> state) noexcept;
    std::shared_ptr<AssistantState> state;
    std::jthread worker;
};
