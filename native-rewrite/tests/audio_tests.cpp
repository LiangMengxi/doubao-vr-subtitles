#include "audio_restore.h"
#include "bridge.h"
#include <iostream>
#include <vector>

void require(bool condition, std::string_view message) {
    if (!condition) throw std::runtime_error(std::string(message));
}

struct FakeAudio {
    std::array<std::wstring, 3> devices{L"console", L"multimedia", L"communications"};
    std::vector<std::pair<std::wstring, uint32_t>> writes;
    int failed_role = -1;
    bool rollback_failure = false;
    bool hijack = false;
    std::wstring current(uint32_t role) const {
        if (hijack && role == 1 && std::ranges::all_of(devices, [](const auto& device) { return device == L"speaker"; })) return L"other";
        return devices.at(role);
    }
    void set(std::wstring_view device, uint32_t role) {
        writes.emplace_back(device, role);
        if (device == L"speaker" && static_cast<int>(role) == failed_role) throw std::runtime_error("Controlled switch failure.");
        if (rollback_failure && device == L"console" && role == 0) throw std::runtime_error("Controlled rollback failure.");
        devices.at(role) = device;
    }
};

struct FakeDuplexAudio {
    std::array<std::wstring, 6> devices{L"console", L"multimedia", L"communications", L"input-console", L"input-multimedia", L"input-communications"};
    std::vector<std::pair<std::wstring, uint32_t>> writes;
    int failed_role = -1;
    bool fail_after_write = false;
    std::wstring current(uint32_t role) const { return devices.at(role); }
    void set(std::wstring_view device, uint32_t role) {
        writes.emplace_back(device, role);
        const bool failing = (device == L"speaker" || device == L"mic") && static_cast<int>(role) == failed_role;
        if (failing && !fail_after_write) throw std::runtime_error("Controlled duplex failure.");
        devices.at(role) = device;
        if (failing) throw std::runtime_error("Controlled failure after write.");
    }
};

int main() {
    try {
        static_assert(sizeof(BridgeAudioDevice) == 1540);
        static_assert(sizeof(BridgeAudioDevices) == 197128);
        static_assert(sizeof(BridgeAudioTarget) == 2052);
        FakeDuplexAudio duplex;
        restore_audio_devices(L"speaker", L"mic", duplex);
        require(duplex.writes.size() == 6 && duplex.devices[0] == L"speaker" && duplex.devices[5] == L"mic", "Playback and microphone must restore together.");
        duplex.writes.clear();
        restore_audio_devices(L"speaker", L"mic", duplex);
        require(duplex.writes.empty(), "An unchanged duplex target must not write defaults.");
        for (int failed_role = 0; failed_role < 6; ++failed_role) {
            for (bool fail_after_write : {false, true}) {
                FakeDuplexAudio failing;
                const auto before = failing.devices;
                failing.failed_role = failed_role;
                failing.fail_after_write = fail_after_write;
                bool rejected = false;
                try { restore_audio_devices(L"speaker", L"mic", failing); }
                catch (const std::exception&) { rejected = true; }
                require(rejected && failing.devices == before, "A duplex failure must roll back both playback and microphone roles.");
            }
        }
        FakeDuplexAudio microphone_only;
        for (uint32_t role = 0; role < 3; ++role) microphone_only.devices[role].clear();
        restore_audio_devices(L"", L"mic", microphone_only);
        require(microphone_only.writes.size() == 3 && microphone_only.devices[0].empty(), "Unselected playback must not be read or changed.");
        FakeDuplexAudio playback_only;
        for (uint32_t role = 3; role < 6; ++role) playback_only.devices[role].clear();
        restore_audio_devices(L"speaker", L"", playback_only);
        require(playback_only.writes.size() == 3 && playback_only.devices[3].empty(), "Unselected microphone must not be read or changed.");
        FakeDuplexAudio missing_microphone;
        missing_microphone.devices[5].clear();
        bool missing_rejected = false;
        try { restore_audio_devices(L"speaker", L"mic", missing_microphone); }
        catch (const std::exception&) { missing_rejected = true; }
        require(missing_rejected && missing_microphone.writes.empty(), "Both directions must be captured before any write.");
        FakeDuplexAudio empty_duplex;
        bool empty_rejected = false;
        try { restore_audio_devices(L"", L"", empty_duplex); }
        catch (const std::invalid_argument&) { empty_rejected = true; }
        require(empty_rejected && empty_duplex.writes.empty(), "Empty duplex targets must not change defaults.");
        FakeAudio normal;
        restore_audio_defaults(L"speaker", normal);
        require(normal.writes.size() == 3, "All three playback roles must switch.");
        require(std::ranges::all_of(normal.devices, [](const auto& device) { return device == L"speaker"; }), "Playback role mismatch.");
        normal.writes.clear();
        restore_audio_defaults(L"speaker", normal);
        require(normal.writes.empty(), "An unchanged target must not write system defaults.");
        for (int failed_role = 0; failed_role < 3; ++failed_role) {
            FakeAudio failing;
            const auto before = failing.devices;
            failing.failed_role = failed_role;
            bool rejected = false;
            try { restore_audio_defaults(L"speaker", failing); }
            catch (const std::exception&) { rejected = true; }
            require(rejected && failing.devices == before, "Partial switching must restore every original playback role.");
            if (failed_role == 0) require(failing.writes.size() == 1, "An unchanged default must not be rewritten during rollback.");
        }
        FakeAudio invalid;
        bool rejected = false;
        try { restore_audio_defaults(L"", invalid); }
        catch (const std::invalid_argument&) { rejected = true; }
        require(rejected && invalid.writes.empty(), "An empty target must never change defaults.");
        invalid.devices[1].clear();
        rejected = false;
        try { restore_audio_defaults(L"speaker", invalid); }
        catch (const std::exception&) { rejected = true; }
        require(rejected && invalid.writes.empty(), "Missing original defaults must fail before any write.");
        FakeAudio hijacked;
        hijacked.hijack = true;
        const auto original = hijacked.devices;
        rejected = false;
        try { restore_audio_defaults(L"speaker", hijacked); }
        catch (const std::exception&) { rejected = true; }
        require(rejected && hijacked.devices == original, "A concurrent default change must be detected and rolled back.");
        FakeAudio rollback;
        rollback.failed_role = 1;
        rollback.rollback_failure = true;
        rejected = false;
        try { restore_audio_defaults(L"speaker", rollback); }
        catch (const std::exception& failure) { rejected = std::string_view(failure.what()).find("fully restored") != std::string_view::npos; }
        require(rejected, "Rollback failure must be reported instead of claiming success.");
        std::cout << "PASS: playback/microphone roles, single-direction isolation, no-op, invalid targets, duplex rollback and concurrent default changes.\n";
        return 0;
    } catch (const std::exception& failure) {
        std::cerr << failure.what() << "\n";
        return 1;
    }
}
