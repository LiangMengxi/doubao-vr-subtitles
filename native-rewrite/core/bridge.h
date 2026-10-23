#pragma once
#include <stdint.h>
#include <wchar.h>
#include <array>
#include <cstddef>

#ifdef BRIDGE_EXPORTS
#define BRIDGE_API __declspec(dllexport)
#else
#define BRIDGE_API __declspec(dllimport)
#endif

struct BridgeOptions {
    uint32_t size;
    float width;
    float distance;
    float horizontal;
    float vertical;
    float opacity;
    float pitch;
    float yaw;
    uint32_t crop_top;
    uint32_t trim;
    uint32_t border;
    uint32_t vr;
    uint32_t preview;
    float curvature_degrees = 0;
};

struct BridgeStats {
    uint32_t size;
    uint32_t state;
    uint64_t frames;
    uint64_t submitted;
    uint32_t width;
    uint32_t height;
    double processing_ms;
    uint32_t curvature_fallback = 0;
    float curvature_degrees = 0;
};

using BridgeId = uint64_t;
inline constexpr uint32_t bridge_preview_capacity = 640 * 240 * 4;

struct BridgePreview {
    uint32_t size = sizeof(BridgePreview);
    uint32_t width = 0;
    uint32_t height = 0;
    uint32_t reserved = 0;
    uint64_t sequence = 0;
    std::array<uint8_t, bridge_preview_capacity> pixels{};
};

struct BridgeError {
    uint32_t size = sizeof(BridgeError);
    std::array<wchar_t, 2048> text{};
};

struct BridgeCaptionButton {
    uint32_t size = sizeof(BridgeCaptionButton);
    uint32_t process_id = 0;
    uint64_t window = 0;
    uint64_t observed = 0;
    uint64_t process_started = 0;
    uint32_t client_width = 0;
    uint32_t client_height = 0;
    uint32_t source_width = 0;
    uint32_t source_height = 0;
    uint32_t horizontal = 0;
    uint32_t vertical = 0;
    uint32_t dpi = 0;
    uint32_t reserved = 0;
};

inline constexpr uint32_t bridge_audio_capacity = 128;
struct BridgeAudioDevice {
    std::array<wchar_t, 512> id{};
    std::array<wchar_t, 256> name{};
    uint32_t default_roles = 0;
};
struct BridgeAudioDevices {
    uint32_t size = sizeof(BridgeAudioDevices);
    uint32_t count = 0;
    std::array<BridgeAudioDevice, bridge_audio_capacity> devices{};
};
struct BridgeAudioTarget {
    uint32_t size = sizeof(BridgeAudioTarget);
    std::array<wchar_t, 512> playback{};
    std::array<wchar_t, 512> microphone{};
};

struct BridgeMicrophoneStats {
    uint32_t size = sizeof(BridgeMicrophoneStats);
    uint32_t enabled = 0;
    uint32_t state = 0;
    uint32_t reserved = 0;
    uint64_t frames = 0;
    uint64_t packets = 0;
    std::array<wchar_t, 256> device{};
    std::array<wchar_t, 512> error{};
};

struct BridgeSteamVrRegistration {
    uint32_t size = sizeof(BridgeSteamVrRegistration);
    uint32_t enabled = 0;
    uint32_t auto_launch = 0;
    uint32_t reserved = 0;
    std::array<wchar_t, 4096> manifest{};
};

struct BridgeSteamVrState {
    uint32_t size = sizeof(BridgeSteamVrState);
    uint32_t installed = 0;
    uint32_t auto_launch = 0;
    uint32_t dashboard = 0;
    std::array<wchar_t, 4096> executable{};
    std::array<wchar_t, 1024> arguments{};
};

static_assert(sizeof(BridgeOptions) == 56 && offsetof(BridgeOptions, curvature_degrees) == 52);
static_assert(sizeof(BridgeStats) == 48 && offsetof(BridgeStats, curvature_fallback) == 40);
static_assert(sizeof(BridgePreview) == 614424 && offsetof(BridgePreview, pixels) == 24);
static_assert(sizeof(BridgeSteamVrRegistration) == 8208 && offsetof(BridgeSteamVrRegistration, manifest) == 16);
static_assert(sizeof(BridgeSteamVrState) == 10256 && offsetof(BridgeSteamVrState, executable) == 16);

extern "C" {
BRIDGE_API int32_t __cdecl bridge_caption_button(uintptr_t window, uint32_t process_id, BridgeCaptionButton& button, BridgeError& error) noexcept;
BRIDGE_API int32_t __cdecl bridge_caption_close(const BridgeCaptionButton& button, BridgeError& error) noexcept;
BRIDGE_API int32_t __cdecl bridge_audio_devices(uint32_t flow, BridgeAudioDevices& devices, BridgeError& error) noexcept;
BRIDGE_API int32_t __cdecl bridge_audio_restore(const BridgeAudioTarget& target, BridgeError& error) noexcept;
BRIDGE_API int32_t __cdecl bridge_microphone_configure(BridgeId bridge, uint32_t enabled) noexcept;
BRIDGE_API int32_t __cdecl bridge_microphone_stats(BridgeId bridge, BridgeMicrophoneStats& stats) noexcept;
BRIDGE_API int32_t __cdecl bridge_steamvr_register(const BridgeSteamVrRegistration& request, BridgeError& error) noexcept;
BRIDGE_API int32_t __cdecl bridge_steamvr_state(BridgeSteamVrState& state, BridgeError& error) noexcept;
BRIDGE_API uint32_t __cdecl bridge_version() noexcept;
BRIDGE_API BridgeId __cdecl bridge_create() noexcept;
BRIDGE_API void __cdecl bridge_destroy(BridgeId bridge) noexcept;
BRIDGE_API int32_t __cdecl bridge_start(BridgeId bridge, uintptr_t hwnd, const BridgeOptions& options) noexcept;
BRIDGE_API int32_t __cdecl bridge_configure(BridgeId bridge, const BridgeOptions& options) noexcept;
BRIDGE_API void __cdecl bridge_stop(BridgeId bridge) noexcept;
BRIDGE_API int32_t __cdecl bridge_stats(BridgeId bridge, BridgeStats& stats) noexcept;
BRIDGE_API int32_t __cdecl bridge_error(BridgeId bridge, BridgeError& output) noexcept;
BRIDGE_API int32_t __cdecl bridge_preview(BridgeId bridge, uint64_t after_sequence, BridgePreview& output) noexcept;
}
