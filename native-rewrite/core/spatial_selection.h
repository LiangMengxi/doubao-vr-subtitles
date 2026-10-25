#pragma once
#include <algorithm>
#include <array>
#include <cmath>
#include <optional>
#include <chrono>

namespace spatial {
struct Vector {
    float horizontal = 0, vertical = 0, depth = 0;
    Vector operator+(Vector other) const { return {horizontal + other.horizontal, vertical + other.vertical, depth + other.depth}; }
    Vector operator-(Vector other) const { return {horizontal - other.horizontal, vertical - other.vertical, depth - other.depth}; }
    Vector operator*(float factor) const { return {horizontal * factor, vertical * factor, depth * factor}; }
    float dot(Vector other) const { return horizontal * other.horizontal + vertical * other.vertical + depth * other.depth; }
    Vector cross(Vector other) const {
        return {vertical * other.depth - depth * other.vertical,
            depth * other.horizontal - horizontal * other.depth,
            horizontal * other.vertical - vertical * other.horizontal};
    }
    float length() const { return std::sqrt(dot(*this)); }
    Vector normalized(Vector fallback = {}) const {
        const float len = length();
        return len > 1e-6f && std::isfinite(len) ? (*this) * (1.f / len) : fallback;
    }
    bool finite() const { return std::isfinite(horizontal) && std::isfinite(vertical) && std::isfinite(depth); }
};
struct Plane {
    Vector center, right, up, back;
    float width = 0, height = 0;
    Vector point(float horizontal, float vertical) const {
        return center + right * ((horizontal - .5f) * width) + up * ((.5f - vertical) * height);
    }
};
inline std::optional<Plane> frame(Vector head, Vector right, Vector up, Vector back, Vector left_hand, Vector right_hand) {
    if (!head.finite() || !right.finite() || !up.finite() || !back.finite() || !left_hand.finite() || !right_hand.finite()) return {};
    if (std::abs(right.length() - 1.f) > .01f || std::abs(up.length() - 1.f) > .01f || std::abs(back.length() - 1.f) > .01f) return {};
    if (std::abs(right.dot(up)) > .01f || std::abs(right.dot(back)) > .01f || std::abs(up.dot(back)) > .01f) return {};
    const auto center = (left_hand + right_hand) * .5f;
    const auto diagonal = right_hand - left_hand;
    const float diagonal_len = diagonal.length();
    if (diagonal_len < 1e-4f || !std::isfinite(diagonal_len)) return {};
    const auto diagonal_dir = diagonal * (1.f / diagonal_len);
    const auto view_direction = head - center;
    const float distance = view_direction.dot(back);
    const auto back_candidate = view_direction - diagonal_dir * view_direction.dot(diagonal_dir);
    auto plane_back = back_candidate.normalized(back);
    if (plane_back.dot(back) < 0.f) plane_back = plane_back * -1.f;
    const auto right_candidate = right - plane_back * right.dot(plane_back);
    auto plane_right = right_candidate.normalized(right);
    if (plane_right.dot(right) < 0.f) plane_right = plane_right * -1.f;
    const auto plane_up = plane_back.cross(plane_right).normalized(up);
    const float width = std::abs(diagonal.dot(plane_right)), height = std::abs(diagonal.dot(plane_up));
    if (distance < .15f || distance > 4.f || width < .05f || height < .05f || width > 4.f || height > 4.f) return {};
    return Plane{center, plane_right, plane_up, plane_back, width, height};
}

class Selection {
public:
    enum class State { idle, armed, sizing, capture };
    void toggle() { current = current == State::idle ? State::armed : State::idle; plane.reset(); released = false; }
    std::optional<Plane> update(bool left, bool right, const std::optional<Plane>& measured) {
        if (current == State::idle || current == State::capture) return {};
        if (!left && !right) released = true;
        if (current == State::armed) {
            if (released && left && right && measured) { current = State::sizing; plane = measured; }
            return {};
        }
        if (left && right) { plane = measured; return {}; }
        if (plane && measured) {
            const auto result = plane;
            current = State::capture;
            return result;
        }
        current = State::armed;
        plane.reset();
        released = false;
        return {};
    }
    void reset() { current = State::idle; plane.reset(); released = false; }
    State state() const { return current; }
    const std::optional<Plane>& region() const { return plane; }
private:
    State current = State::idle;
    bool released = false;
    std::optional<Plane> plane;
};

struct Projection {
    Vector eye, right, up, back;
    float left = 0, top = 0, width = 0, height = 0;
    std::optional<std::array<float, 2>> project_raw(Vector world) const {
        const auto relative = world - eye;
        const float depth = -relative.dot(back);
        if (depth < .05f || !std::isfinite(depth) || width <= 0 || height <= 0) return {};
        const float horizontal = (relative.dot(right) / depth - left) / width;
        const float vertical = (-relative.dot(up) / depth - top) / height;
        if (!std::isfinite(horizontal) || !std::isfinite(vertical)) return {};
        return std::array{horizontal, vertical};
    }
    std::optional<std::array<float, 2>> project(Vector world) const {
        const auto coordinates = project_raw(world);
        if (!coordinates || (*coordinates)[0] < 0 || (*coordinates)[0] > 1 || (*coordinates)[1] < 0 || (*coordinates)[1] > 1) return {};
        return coordinates;
    }
};
}
