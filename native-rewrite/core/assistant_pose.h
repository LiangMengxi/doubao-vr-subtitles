#pragma once
#include "spatial_selection.h"
#include <openvr.h>

namespace assistant {
inline bool valid_pose(const vr::HmdMatrix34_t& pose) {
    for (const auto& row : pose.m) for (const float value : row) if (!std::isfinite(value)) return false;
    std::array<spatial::Vector, 3> axes{};
    for (size_t axis = 0; axis < axes.size(); ++axis) axes[axis] = {pose.m[0][axis], pose.m[1][axis], pose.m[2][axis]};
    for (const auto& axis : axes) if (std::abs(axis.dot(axis) - 1.f) > .02f) return false;
    if (std::abs(axes[0].dot(axes[1])) > .02f || std::abs(axes[0].dot(axes[2])) > .02f || std::abs(axes[1].dot(axes[2])) > .02f) return false;
    const spatial::Vector cross{axes[0].vertical * axes[1].depth - axes[0].depth * axes[1].vertical,
        axes[0].depth * axes[1].horizontal - axes[0].horizontal * axes[1].depth,
        axes[0].horizontal * axes[1].vertical - axes[0].vertical * axes[1].horizontal};
    return cross.dot(axes[2]) > .98f;
}

inline std::optional<vr::HmdMatrix34_t> aim_pose(const vr::HmdMatrix34_t& device, const vr::HmdMatrix34_t& component) {
    if (!valid_pose(device) || !valid_pose(component)) return {};
    vr::HmdMatrix34_t result{};
    for (size_t row = 0; row < 3; ++row) for (size_t column = 0; column < 4; ++column) {
        result.m[row][column] = column == 3 ? device.m[row][3] : 0;
        for (size_t axis = 0; axis < 3; ++axis) result.m[row][column] += device.m[row][axis] * component.m[axis][column];
    }
    return valid_pose(result) ? std::optional{result} : std::nullopt;
}

inline std::optional<std::array<float, 2>> intersect(const spatial::Plane& plane, const vr::HmdMatrix34_t& aim) {
    if (!valid_pose(aim) || !std::isfinite(plane.width) || !std::isfinite(plane.height)) return {};
    const spatial::Vector origin{aim.m[0][3], aim.m[1][3], aim.m[2][3]};
    const spatial::Vector direction{-aim.m[0][2], -aim.m[1][2], -aim.m[2][2]};
    const auto denominator = direction.dot(plane.back);
    if (!std::isfinite(denominator) || denominator >= -.001f || plane.width <= 0 || plane.height <= 0) return {};
    const auto distance = (plane.center - origin).dot(plane.back) / denominator;
    if (!std::isfinite(distance) || distance < 0 || distance > 8.f) return {};
    const auto relative = origin + direction * distance - plane.center;
    const float horizontal = .5f + relative.dot(plane.right) / plane.width;
    const float vertical = .5f - relative.dot(plane.up) / plane.height;
    if (!std::isfinite(horizontal) || !std::isfinite(vertical) || horizontal < 0 || horizontal > 1 || vertical < 0 || vertical > 1) return {};
    return std::array{horizontal, vertical};
}
}
