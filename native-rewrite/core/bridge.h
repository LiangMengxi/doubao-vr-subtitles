#pragma once
#include <stdint.h>
#include <wchar.h>
#include <array>

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
};

struct BridgeStats {
    uint32_t size;
    uint32_t state;
    uint64_t frames;
    uint64_t submitted;
    uint32_t width;
    uint32_t height;
    double processing_ms;
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

extern "C" {
BRIDGE_API int32_t __cdecl bridge_audio_devices(uint32_t flow, BridgeAudioDevices& devices, BridgeError& error) noexcept;
BRIDGE_API int32_t __cdecl bridge_audio_restore(const BridgeAudioTarget& target, BridgeError& error) noexcept;
BRIDGE_API uint32_t __cdecl bridge_version() noexcept;
BRIDGE_API BridgeId __cdecl bridge_create() noexcept;
BRIDGE_API void __cdecl bridge_destroy(BridgeId bridge) noexcept;
BRIDGE_API int32_t __cdecl bridge_start(BridgeId bridge, uintptr_t hwnd, const BridgeOptions& options) noexcept;
BRIDGE_API int32_t __cdecl bridge_configure(BridgeId bridge, const BridgeOptions& options) noexcept;
BRIDGE_API void __cdecl bridge_stop(BridgeId bridge) noexcept;
BRIDGE_API int32_t __cdecl bridge_stats(BridgeId bridge, BridgeStats& stats) noexcept;
BRIDGE_API int32_t __cdecl bridge_error(BridgeId bridge, BridgeError& output) noexcept;
BRIDGE_API int32_t __cdecl bridge_preview(BridgeId bridge, BridgePreview& output) noexcept;
}
