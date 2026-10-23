#pragma once
#include <cmath>
#include <cstdint>
#include <optional>
#include <stdexcept>

class OverlayCurvature {
public:
    enum class Change { unchanged, updated, failed };

    static float fraction(float degrees) noexcept { return degrees / 360.f; }

    template<typename Apply, typename Read>
    Change update(float degrees, Apply apply, Read read) {
        if (!std::isfinite(degrees) || degrees < 0 || degrees > 90) throw std::invalid_argument("Invalid overlay curvature.");
        if (confirmed && requested && *requested == degrees) return Change::unchanged;
        if (confirmed && applied == degrees) {
            requested = degrees;
            flat_fallback = false;
            failure = restoration = 0;
            return Change::updated;
        }
        confirmed = false;
        failure = apply(degrees);
        restoration = 0;
        if (failure == 0) {
            applied = degrees;
            requested = degrees;
            flat_fallback = false;
            confirmed = true;
            return Change::updated;
        }
        restoration = apply(0.f);
        if (restoration != 0) {
            const auto actual = read();
            if (!actual || *actual != 0.f) {
                requested.reset();
                return Change::failed;
            }
        }
        applied = 0;
        requested = degrees;
        flat_fallback = true;
        confirmed = true;
        return Change::updated;
    }

    float degrees() const noexcept { return applied; }
    bool fallback() const noexcept { return flat_fallback; }
    int32_t error() const noexcept { return failure; }
    int32_t restore_error() const noexcept { return restoration; }

private:
    std::optional<float> requested;
    float applied = 0;
    bool flat_fallback = false;
    bool confirmed = true;
    int32_t failure = 0;
    int32_t restoration = 0;
};
