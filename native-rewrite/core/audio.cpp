#include "bridge.h"
#include "platform.h"
#include <algorithm>
#include <memory>

namespace {
template<typename Action> int32_t audio_call(BridgeError& error, Action action) noexcept {
    if (error.size != sizeof(BridgeError)) return E_INVALIDARG;
    error.text.fill(0);
    try { action(); return S_OK; }
    catch (...) {
        int32_t result = E_FAIL;
        try {
            std::wstring message;
            try { throw; }
            catch (const winrt::hresult_error& failure) { result = failure.code(); message = failure.message(); }
            catch (const std::exception& failure) { message = winrt::to_hstring(failure.what()); }
            catch (...) { message = L"音频设备操作失败。"; }
            std::copy_n(message.begin(), std::min(message.size(), error.text.size() - 1), error.text.begin());
        } catch (...) { }
        return result;
    }
}
}

int32_t __cdecl bridge_audio_devices(uint32_t flow, BridgeAudioDevices& output, BridgeError& error) noexcept {
    if (output.size != sizeof(BridgeAudioDevices)) return E_INVALIDARG;
    output.count = 0;
    return audio_call(error, [&] {
        if (flow > 1) throw winrt::hresult_invalid_argument();
        const auto devices = platform::audio_devices(flow);
        if (devices.size() > bridge_audio_capacity) throw winrt::hresult_error(E_BOUNDS, L"音频设备数量超出接口限制。");
        auto snapshot = std::make_unique<BridgeAudioDevices>();
        for (size_t index = 0; index < devices.size(); ++index) {
            const auto& device = devices[index];
            auto& target = snapshot->devices[index];
            if (device.id.size() >= target.id.size()) throw winrt::hresult_error(E_BOUNDS, L"音频设备标识过长。");
            std::copy(device.id.begin(), device.id.end(), target.id.begin());
            std::copy_n(device.name.begin(), std::min(device.name.size(), target.name.size() - 1), target.name.begin());
            target.default_roles = device.default_roles;
        }
        snapshot->count = static_cast<uint32_t>(devices.size());
        output = *snapshot;
    });
}

int32_t __cdecl bridge_audio_restore(const BridgeAudioTarget& target, BridgeError& error) noexcept {
    return audio_call(error, [&] {
        if (target.size != sizeof(BridgeAudioTarget)) throw winrt::hresult_invalid_argument();
        const auto playback_end = std::find(target.playback.begin(), target.playback.end(), L'\0');
        const auto microphone_end = std::find(target.microphone.begin(), target.microphone.end(), L'\0');
        if (playback_end == target.playback.end() || microphone_end == target.microphone.end()) throw winrt::hresult_invalid_argument();
        if (playback_end == target.playback.begin() && microphone_end == target.microphone.begin()) throw winrt::hresult_invalid_argument(L"请先选择目标音频设备。");
        platform::restore_audio(std::wstring(target.playback.begin(), playback_end), std::wstring(target.microphone.begin(), microphone_end));
    });
}

int32_t __cdecl bridge_steamvr_register(const BridgeSteamVrRegistration& request, BridgeError& error) noexcept {
    return audio_call(error, [&] {
        if (request.size != sizeof(BridgeSteamVrRegistration) || request.enabled > 1) throw winrt::hresult_invalid_argument();
        const auto end = std::find(request.manifest.begin(), request.manifest.end(), L'\0');
        if (end == request.manifest.begin() || end == request.manifest.end()) throw winrt::hresult_invalid_argument();
        platform::register_steamvr(std::wstring(request.manifest.begin(), end), request.enabled != 0);
    });
}
