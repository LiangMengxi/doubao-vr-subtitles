#pragma once
#include <algorithm>
#include <array>
#include <cstdint>
#include <exception>
#include <stdexcept>
#include <string>
#include <string_view>

template<size_t Count, typename Backend> void restore_audio_targets(const std::array<std::wstring, Count>& targets, Backend& backend) {
    if (std::ranges::all_of(targets, [](const auto& target) { return target.empty(); })) throw std::invalid_argument("No audio device selected.");
    std::array<std::wstring, Count> previous;
    bool needs_change = false;
    for (uint32_t role = 0; role < previous.size(); ++role) {
        if (targets[role].empty()) continue;
        previous[role] = backend.current(role);
        if (previous[role].empty()) throw std::runtime_error("Cannot read the original audio defaults.");
        needs_change = needs_change || previous[role] != targets[role];
    }
    if (!needs_change) return;
    try {
        for (uint32_t role = 0; role < previous.size(); ++role) {
            if (!targets[role].empty() && previous[role] != targets[role]) backend.set(targets[role], role);
        }
        for (uint32_t role = 0; role < previous.size(); ++role) {
            if (!targets[role].empty() && backend.current(role) != targets[role]) throw std::runtime_error("Audio defaults changed during switching.");
        }
    } catch (...) {
        const auto failure = std::current_exception();
        for (uint32_t role = 0; role < previous.size(); ++role) {
            if (targets[role].empty()) continue;
            bool changed = true;
            try { changed = backend.current(role) != previous[role]; }
            catch (...) { }
            if (changed) { try { backend.set(previous[role], role); } catch (...) { } }
        }
        bool restored = true;
        for (uint32_t role = 0; role < previous.size(); ++role) {
            if (targets[role].empty()) continue;
            try { if (backend.current(role) != previous[role]) restored = false; }
            catch (...) { restored = false; }
        }
        if (!restored) throw std::runtime_error("Audio switching failed; the original audio defaults could not be fully restored.");
        std::rethrow_exception(failure);
    }
}

template<typename Backend> void restore_audio_defaults(std::wstring_view target, Backend& backend) {
    std::array<std::wstring, 3> targets;
    targets.fill(std::wstring(target));
    restore_audio_targets(targets, backend);
}

template<typename Backend> void restore_audio_devices(std::wstring_view playback, std::wstring_view microphone, Backend& backend) {
    const std::array<std::wstring, 6> targets{std::wstring(playback), std::wstring(playback), std::wstring(playback),
        std::wstring(microphone), std::wstring(microphone), std::wstring(microphone)};
    restore_audio_targets(targets, backend);
}
