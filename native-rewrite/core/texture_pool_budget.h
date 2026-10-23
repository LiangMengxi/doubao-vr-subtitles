#pragma once
#include <cstdint>

namespace texture_pool {
inline constexpr uint64_t memory_budget = 128 * 1024 * 1024;

enum class Resize { allocate, defer, rebuild, reject };

constexpr Resize resize(uint64_t requested, uint64_t current, uint64_t retired) noexcept {
    if (requested > memory_budget) return Resize::reject;
    if (current > memory_budget - requested) return Resize::rebuild;
    if (retired > memory_budget - requested - current) return Resize::defer;
    return Resize::allocate;
}
}
