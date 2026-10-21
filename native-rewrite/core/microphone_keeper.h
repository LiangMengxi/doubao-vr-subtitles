#pragma once
#include "bridge.h"
#include <memory>
#include <mutex>
#include <string_view>
#include <string>
#include <thread>

inline bool is_pico_microphone(std::wstring_view name) {
    std::wstring normalized;
    normalized.reserve(name.size());
    for (auto character : name) {
        if (character == L' ' || character == L'_' || character == L'-') continue;
        normalized.push_back(character >= L'A' && character <= L'Z' ? static_cast<wchar_t>(character + L'a' - L'A') : character);
    }
    return normalized.find(L"picostreamingmicrophone") != std::wstring::npos;
}

struct MicrophoneKeeperState;
class MicrophoneKeeper {
public:
    MicrophoneKeeper();
    ~MicrophoneKeeper();
    MicrophoneKeeper(const MicrophoneKeeper&) = delete;
    MicrophoneKeeper& operator=(const MicrophoneKeeper&) = delete;
    void enable(bool enabled);
    BridgeMicrophoneStats snapshot() const;
private:
    std::shared_ptr<MicrophoneKeeperState> state;
    std::jthread worker;
    std::mutex control;
};
