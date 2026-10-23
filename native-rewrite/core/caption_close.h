#pragma once
#include <algorithm>
#include <array>
#include <cmath>
#include <cstdint>
#include <optional>
#include <span>
#include <vector>

namespace caption_close {
inline constexpr uint32_t template_width = 22;
inline constexpr uint32_t template_height = 23;
inline constexpr std::array<uint8_t, template_width * template_height> reference{
    54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54,
    54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54,
    54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54,
    54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54,
    54, 54, 54, 54, 54, 54, 54, 54, 63, 89, 89, 97, 80, 71, 54, 54, 54, 54, 54, 54, 54, 54,
    54, 54, 54, 54, 54, 54, 54, 89, 124, 124, 124, 124, 124, 124, 89, 54, 54, 54, 54, 54, 54, 54,
    54, 54, 54, 54, 54, 54, 107, 124, 124, 124, 124, 124, 124, 124, 124, 115, 54, 54, 54, 54, 54, 54,
    54, 54, 54, 54, 54, 97, 124, 124, 124, 124, 124, 124, 124, 124, 124, 124, 97, 54, 54, 54, 54, 54,
    54, 54, 54, 54, 63, 124, 124, 124, 115, 97, 124, 124, 115, 115, 124, 124, 124, 71, 54, 54, 54, 54,
    54, 54, 54, 54, 80, 124, 124, 124, 107, 63, 89, 107, 54, 107, 124, 124, 124, 89, 54, 54, 54, 54,
    54, 54, 54, 54, 97, 124, 124, 124, 124, 97, 63, 54, 107, 124, 124, 124, 124, 107, 54, 54, 54, 54,
    54, 54, 54, 54, 97, 124, 124, 124, 124, 107, 54, 63, 89, 124, 124, 124, 124, 97, 54, 54, 54, 54,
    54, 54, 54, 54, 89, 124, 124, 124, 107, 54, 107, 97, 63, 107, 124, 124, 124, 80, 54, 54, 54, 54,
    54, 54, 54, 54, 71, 124, 124, 124, 115, 115, 124, 124, 107, 115, 124, 124, 124, 63, 54, 54, 54, 54,
    54, 54, 54, 54, 54, 89, 124, 124, 124, 124, 124, 124, 124, 124, 124, 124, 97, 54, 54, 54, 54, 54,
    54, 54, 54, 54, 54, 63, 115, 124, 124, 124, 124, 124, 124, 124, 124, 115, 54, 54, 54, 54, 54, 54,
    54, 54, 54, 54, 54, 54, 63, 89, 124, 124, 124, 124, 124, 124, 89, 54, 54, 54, 54, 54, 54, 54,
    54, 54, 54, 54, 54, 54, 54, 54, 63, 80, 97, 89, 89, 63, 54, 54, 54, 54, 54, 54, 54, 54,
    54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54,
    54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54,
    54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54,
    54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54,
    54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54, 54,
};

struct Point { uint32_t horizontal, vertical; };

inline std::optional<Point> locate(std::span<const uint8_t> pixels, uint32_t width, uint32_t height, uint32_t dpi) {
    if (dpi < 96 || dpi > 384 || width == 0 || height == 0
        || width > 4096 || height > 300 || pixels.size() != static_cast<uint64_t>(width) * height * 4) return {};
    const double scale = dpi / 96.0;
    const auto columns = static_cast<uint32_t>(std::floor(width / scale));
    const auto rows = static_cast<uint32_t>(std::floor(height / scale));
    if (columns < template_width || rows < template_height) return {};
    std::vector<double> gray(static_cast<size_t>(columns) * rows);
    auto sample_original = [&](double column, double row) {
        const double source_horizontal = std::clamp((column + .5) * scale - .5, 0.0, static_cast<double>(width - 1));
        const double source_vertical = std::clamp((row + .5) * scale - .5, 0.0, static_cast<double>(height - 1));
        const auto left = std::min(static_cast<uint32_t>(source_horizontal), width - 1);
        const auto top = std::min(static_cast<uint32_t>(source_vertical), height - 1);
        const auto right = std::min(left + 1, width - 1);
        const auto bottom = std::min(top + 1, height - 1);
        const double horizontal_weight = source_horizontal - left;
        const double vertical_weight = source_vertical - top;
        auto sample = [&](uint32_t sample_column, uint32_t sample_row) {
            const size_t offset = (static_cast<size_t>(sample_row) * width + sample_column) * 4;
            return .114 * pixels[offset] + .587 * pixels[offset + 1] + .299 * pixels[offset + 2];
        };
        return (sample(left, top) * (1 - horizontal_weight) + sample(right, top) * horizontal_weight) * (1 - vertical_weight)
            + (sample(left, bottom) * (1 - horizontal_weight) + sample(right, bottom) * horizontal_weight) * vertical_weight;
    };
    for (uint32_t row = 0; row < rows; ++row) {
        for (uint32_t column = 0; column < columns; ++column) {
            gray[static_cast<size_t>(row) * columns + column] = sample_original(column, row);
        }
    }
    double reference_sum = 0, reference_square = 0;
    for (auto value : reference) { reference_sum += value; reference_square += value * value; }
    constexpr double count = static_cast<double>(reference.size());
    const double reference_variance = reference_square - reference_sum * reference_sum / count;
    std::vector<Point> matches;
    double best = .90;
    double best_horizontal_phase = 0, best_vertical_phase = 0;
    std::optional<Point> position;
    for (uint32_t row = 0; row + template_height <= rows; ++row) {
        for (uint32_t column = 0; column + template_width <= columns; ++column) {
            double sum = 0, square = 0, product = 0;
            for (uint32_t offset_row = 0; offset_row < template_height; ++offset_row) {
                for (uint32_t offset_column = 0; offset_column < template_width; ++offset_column) {
                    const double value = gray[static_cast<size_t>(row + offset_row) * columns + column + offset_column];
                    const auto expected = reference[offset_row * template_width + offset_column];
                    sum += value;
                    square += value * value;
                    product += value * expected;
                }
            }
            const double variance = square - sum * sum / count;
            if (variance < 1) continue;
            const double score = (product - sum * reference_sum / count) / std::sqrt(variance * reference_variance);
            if (score < .85) continue;
            bool matched = false;
            for (double horizontal_phase : {0.0, -.33, .33}) {
                for (double vertical_phase : {0.0, -.33, .33}) {
                    double refined_sum = 0, refined_square = 0, refined_product = 0;
                    double glyph_sum = 0, glyph_square = 0, glyph_product = 0, expected_sum = 0, expected_square = 0;
                    for (uint32_t offset_row = 0; offset_row < template_height; ++offset_row) {
                        for (uint32_t offset_column = 0; offset_column < template_width; ++offset_column) {
                            const double value = sample_original(column + offset_column + horizontal_phase, row + offset_row + vertical_phase);
                            const double expected = reference[offset_row * template_width + offset_column];
                            refined_sum += value;
                            refined_square += value * value;
                            refined_product += value * expected;
                            if (offset_row < 7 || offset_row >= 15 || offset_column < 7 || offset_column >= 15) continue;
                            glyph_sum += value;
                            glyph_square += value * value;
                            glyph_product += value * expected;
                            expected_sum += expected;
                            expected_square += expected * expected;
                        }
                    }
                    const double glyph_variance = glyph_square - glyph_sum * glyph_sum / 64;
                    const double refined_variance = refined_square - refined_sum * refined_sum / count;
                    if (glyph_variance < 1 || refined_variance < 1) continue;
                    const double glyph_score = (glyph_product - glyph_sum * expected_sum / 64)
                        / std::sqrt(glyph_variance * (expected_square - expected_sum * expected_sum / 64));
                    const double refined_score = (refined_product - refined_sum * reference_sum / count)
                        / std::sqrt(refined_variance * reference_variance);
                    if (glyph_score < .8 || refined_score < .85) continue;
                    matched = true;
                    if (refined_score < best || (position && refined_score <= best + 1e-9)) continue;
                    best = refined_score;
                    position = Point{column, row};
                    best_horizontal_phase = horizontal_phase;
                    best_vertical_phase = vertical_phase;
                }
            }
            if (matched) matches.push_back({column, row});
        }
    }
    if (!position) return {};
    for (const auto& match : matches) {
        if (std::abs(static_cast<int>(match.horizontal) - static_cast<int>(position->horizontal)) > static_cast<int>(template_width / 2)
            || std::abs(static_cast<int>(match.vertical) - static_cast<int>(position->vertical)) > static_cast<int>(template_height / 2)) return {};
    }
    return Point{static_cast<uint32_t>(std::lround((position->horizontal + best_horizontal_phase + template_width / 2.0) * scale)),
        static_cast<uint32_t>(std::lround((position->vertical + best_vertical_phase + template_height / 2.0) * scale))};
}
}
