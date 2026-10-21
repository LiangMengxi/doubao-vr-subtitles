#pragma once
#include <d3d11.h>
#include <d3dcompiler.h>
#include <winrt/base.h>
#include <array>
#include <span>
#include <string_view>
#include <string>
#include <vector>
#include <memory>

namespace platform {
struct AudioDevice {
    std::wstring id;
    std::wstring name;
    uint32_t default_roles = 0;
};
std::vector<AudioDevice> audio_devices(uint32_t flow);
void restore_audio(std::wstring_view playback, std::wstring_view microphone);
void register_steamvr(std::wstring_view manifest, bool enabled);

struct MicrophoneProgress {
    uint64_t frames = 0;
    uint64_t packets = 0;
};

struct MicrophoneCaptureState;
class MicrophoneCapture {
public:
    explicit MicrophoneCapture(std::wstring_view identifier);
    ~MicrophoneCapture();
    MicrophoneCapture(const MicrophoneCapture&) = delete;
    MicrophoneCapture& operator=(const MicrophoneCapture&) = delete;
    MicrophoneProgress consume();
    void wait(uint32_t milliseconds) const;
private:
    std::unique_ptr<MicrophoneCaptureState> state;
};

class ApartmentScope {
public:
    explicit ApartmentScope(winrt::apartment_type type = winrt::apartment_type::multi_threaded) { winrt::init_apartment(type); }
    ~ApartmentScope() { winrt::uninit_apartment(); }
    ApartmentScope(const ApartmentScope&) = delete;
    ApartmentScope& operator=(const ApartmentScope&) = delete;
};

template<typename Action> class ScopeExit {
public:
    explicit ScopeExit(Action action_value) : action(std::move(action_value)) {}
    ~ScopeExit() noexcept { try { action(); } catch (...) { } }
    ScopeExit(const ScopeExit&) = delete;
    ScopeExit& operator=(const ScopeExit&) = delete;
private:
    Action action;
};

uint32_t submit_overlay(uint64_t handle, const winrt::com_ptr<ID3D11Texture2D>& texture,
    const winrt::com_ptr<ID3D11DeviceContext>& context);
winrt::com_ptr<ID3DBlob> compile_shader(std::string_view source, std::string_view entry, std::string_view profile);
winrt::com_ptr<ID3D11Texture2D> bgra_texture(const winrt::com_ptr<ID3D11Device>& device,
    uint32_t width, uint32_t height, std::span<const uint32_t> pixels);
void bind_compute(const winrt::com_ptr<ID3D11DeviceContext>& context,
    const winrt::com_ptr<ID3D11ShaderResourceView>& input,
    const winrt::com_ptr<ID3D11UnorderedAccessView>& target,
    const winrt::com_ptr<ID3D11Buffer>& constants);
void unbind_compute(const winrt::com_ptr<ID3D11DeviceContext>& context);
void bind_pixel(const winrt::com_ptr<ID3D11DeviceContext>& context,
    const winrt::com_ptr<ID3D11RenderTargetView>& target,
    const winrt::com_ptr<ID3D11ShaderResourceView>& input,
    const winrt::com_ptr<ID3D11Buffer>& constants,
    const winrt::com_ptr<ID3D11SamplerState>& sampler);
void unbind_pixel(const winrt::com_ptr<ID3D11DeviceContext>& context);

class ReadMapping {
public:
    ReadMapping(winrt::com_ptr<ID3D11DeviceContext> context, winrt::com_ptr<ID3D11Resource> resource);
    ~ReadMapping();
    ReadMapping(const ReadMapping&) = delete;
    ReadMapping& operator=(const ReadMapping&) = delete;
    ReadMapping(ReadMapping&&) = delete;
    ReadMapping& operator=(ReadMapping&&) = delete;
    std::array<uint32_t, 4> extent() const;
    std::vector<uint8_t> bgra(uint32_t width, uint32_t height) const;
private:
    winrt::com_ptr<ID3D11DeviceContext> context;
    winrt::com_ptr<ID3D11Resource> resource;
    D3D11_MAPPED_SUBRESOURCE mapping{};
};
}
