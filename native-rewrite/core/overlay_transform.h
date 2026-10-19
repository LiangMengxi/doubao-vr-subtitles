#pragma once
#include "bridge.h"
#include <array>
#include <cmath>
#include <numbers>

inline std::array<std::array<float, 4>, 3> overlay_transform(const BridgeOptions& options) {
    const float pitch = options.pitch * std::numbers::pi_v<float> / 180.f;
    const float yaw = options.yaw * std::numbers::pi_v<float> / 180.f;
    const float pitch_cosine = std::cos(pitch);
    const float pitch_sine = std::sin(pitch);
    const float yaw_cosine = std::cos(yaw);
    const float yaw_sine = std::sin(yaw);
    return {{{yaw_cosine, yaw_sine * pitch_sine, yaw_sine * pitch_cosine, options.horizontal},
        {0, pitch_cosine, -pitch_sine, options.vertical},
        {-yaw_sine, yaw_cosine * pitch_sine, yaw_cosine * pitch_cosine, -options.distance}}};
}
