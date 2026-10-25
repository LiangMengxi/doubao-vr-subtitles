#pragma once
#include "spatial_selection.h"
#include <span>
#include <stop_token>
#include <stdexcept>
#include <vector>
#include <cstdint>

namespace spatial {
struct Image {
    uint32_t width = 0, height = 0;
    std::vector<uint8_t> pixels;
};
inline Image rectify(const Plane& selected, const Projection& projection, std::span<const uint8_t> source,
    uint32_t source_width, uint32_t source_height, std::stop_token stop = {}) {
    if (source_width == 0 || source_height == 0 || source_width > 8192 || source_height > 8192
        || static_cast<uint64_t>(source_width) * source_height > 8192ull * 8192ull
        || source.size() != static_cast<size_t>(source_width) * source_height * 4
        || !std::isfinite(selected.width) || !std::isfinite(selected.height) || selected.width <= 0 || selected.height <= 0) {
        throw std::invalid_argument("Invalid screenshot dimensions.");
    }
    for (const auto horizontal : {0.f, 1.f}) for (const auto vertical : {0.f, 1.f}) {
        if (!projection.project_raw(selected.point(horizontal, vertical))) throw std::invalid_argument("Selection is outside the left-eye view.");
    }
    uint32_t visible_samples = 0;
    for (uint32_t row = 0; row < 9; ++row) {
        for (uint32_t column = 0; column < 9; ++column) {
            if (projection.project(selected.point(static_cast<float>(column) / 8.f, static_cast<float>(row) / 8.f))) ++visible_samples;
        }
    }
    if (visible_samples < 20) throw std::invalid_argument("Selection is outside the left-eye view.");
    const float depth = -(selected.center - projection.eye).dot(projection.back);
    const float density = std::max(static_cast<float>(source_width) / projection.width,
        static_cast<float>(source_height) / projection.height) / depth;
    const float pixel_width = selected.width * density, pixel_height = selected.height * density;
    if (!std::isfinite(pixel_width) || !std::isfinite(pixel_height)) throw std::invalid_argument("Invalid screenshot projection.");
    const float scale = std::min({1.f, 2048.f / pixel_width, 1536.f / pixel_height});
    Image result;
    result.width = std::max(1u, static_cast<uint32_t>(pixel_width * scale));
    result.height = std::max(1u, static_cast<uint32_t>(pixel_height * scale));
    if (result.width < 16 || result.height < 16) throw std::invalid_argument("Screenshot selection is too small.");
    result.pixels.resize(static_cast<size_t>(result.width) * result.height * 4);
    for (uint32_t row = 0; row < result.height; ++row) {
        if (stop.stop_requested()) throw std::runtime_error("Screenshot cancelled.");
        for (uint32_t column = 0; column < result.width; ++column) {
            const auto uv = projection.project_raw(selected.point((static_cast<float>(column) + .5f) / static_cast<float>(result.width),
                (static_cast<float>(row) + .5f) / static_cast<float>(result.height)));
            if (!uv) throw std::invalid_argument("Invalid screenshot projection.");
            const float horizontal = std::clamp((*uv)[0], 0.f, 1.f) * static_cast<float>(source_width - 1);
            const float vertical = std::clamp((*uv)[1], 0.f, 1.f) * static_cast<float>(source_height - 1);
            const auto left = static_cast<uint32_t>(horizontal), top = static_cast<uint32_t>(vertical);
            const auto right = std::min(left + 1, source_width - 1), bottom = std::min(top + 1, source_height - 1);
            const float fraction_horizontal = horizontal - static_cast<float>(left), fraction_vertical = vertical - static_cast<float>(top);
            for (size_t channel = 0; channel < 4; ++channel) {
                const auto sample = [&](uint32_t sample_horizontal, uint32_t sample_vertical) {
                    return static_cast<float>(source[(static_cast<size_t>(sample_vertical) * source_width + sample_horizontal) * 4 + channel]);
                };
                const float upper = std::lerp(sample(left, top), sample(right, top), fraction_horizontal);
                const float lower = std::lerp(sample(left, bottom), sample(right, bottom), fraction_horizontal);
                result.pixels[(static_cast<size_t>(row) * result.width + column) * 4 + channel] = static_cast<uint8_t>(std::lerp(upper, lower, fraction_vertical));
            }
        }
    }
    return result;
}
}
