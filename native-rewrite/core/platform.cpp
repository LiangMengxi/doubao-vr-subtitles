#include "platform.h"
#include <algorithm>
#include <cstring>
#include <stdexcept>
#include <openvr.h>
#include "audio_restore.h"
#include <mmdeviceapi.h>
#include <propsys.h>
#include <audioclient.h>
#include <chrono>
#include <thread>

namespace {
struct __declspec(uuid("F8679F50-850A-41CF-9C72-430F290290C8")) AudioPolicy : IUnknown {
    virtual HRESULT STDMETHODCALLTYPE GetMixFormat(PCWSTR device, void** format) = 0;
    virtual HRESULT STDMETHODCALLTYPE GetDeviceFormat(PCWSTR device, int format_type, void** format) = 0;
    virtual HRESULT STDMETHODCALLTYPE ResetDeviceFormat(PCWSTR device) = 0;
    virtual HRESULT STDMETHODCALLTYPE SetDeviceFormat(PCWSTR device, void* endpoint, void* mix) = 0;
    virtual HRESULT STDMETHODCALLTYPE GetProcessingPeriod(PCWSTR device, int defaults, void* period, void* minimum) = 0;
    virtual HRESULT STDMETHODCALLTYPE SetProcessingPeriod(PCWSTR device, void* period) = 0;
    virtual HRESULT STDMETHODCALLTYPE GetShareMode(PCWSTR device, void* mode) = 0;
    virtual HRESULT STDMETHODCALLTYPE SetShareMode(PCWSTR device, void* mode) = 0;
    virtual HRESULT STDMETHODCALLTYPE GetPropertyValue(PCWSTR device, const PROPERTYKEY& key, PROPVARIANT* value) = 0;
    virtual HRESULT STDMETHODCALLTYPE SetPropertyValue(PCWSTR device, const PROPERTYKEY& key, PROPVARIANT* value) = 0;
    virtual HRESULT STDMETHODCALLTYPE SetDefaultEndpoint(PCWSTR device, ERole role) = 0;
    virtual HRESULT STDMETHODCALLTYPE SetEndpointVisibility(PCWSTR device, int visible) = 0;
};

struct PropertyValue {
    PROPVARIANT value{};
    ~PropertyValue() { PropVariantClear(&value); }
    PropertyValue() = default;
    PropertyValue(const PropertyValue&) = delete;
    PropertyValue& operator=(const PropertyValue&) = delete;
    std::wstring text() const { return value.vt == VT_LPWSTR && value.pwszVal ? std::wstring(value.pwszVal) : std::wstring(); }
};

std::wstring audio_id(const winrt::com_ptr<IMMDevice>& device) {
    PropertyValue identifier;
    identifier.value.vt = VT_LPWSTR;
    winrt::check_hresult(device->GetId(&identifier.value.pwszVal));
    return identifier.text();
}

class AudioDefaults {
public:
    winrt::com_ptr<IMMDeviceEnumerator> enumerator = winrt::create_instance<IMMDeviceEnumerator>(__uuidof(MMDeviceEnumerator));
    std::wstring current(uint32_t role) const {
        winrt::com_ptr<IMMDevice> device;
        winrt::check_hresult(enumerator->GetDefaultAudioEndpoint(role < 3 ? eRender : eCapture, static_cast<ERole>(role % 3), device.put()));
        return audio_id(device);
    }
    void set(std::wstring_view device, uint32_t role) {
        if (!policy) {
            constexpr GUID policy_class{0x870af99c, 0x171d, 0x4f9e, {0xaf, 0x0d, 0xe6, 0x3d, 0xf4, 0x0c, 0x2b, 0xc9}};
            policy = winrt::create_instance<AudioPolicy>(policy_class);
        }
        const std::wstring identifier(device);
        winrt::check_hresult(policy->SetDefaultEndpoint(identifier.c_str(), static_cast<ERole>(role % 3)));
    }
private:
    winrt::com_ptr<AudioPolicy> policy;
};

std::wstring validated_audio_id(const AudioDefaults& defaults, std::wstring_view identifier, EDataFlow expected) {
    if (identifier.empty()) return {};
    winrt::com_ptr<IMMDevice> device;
    const std::wstring target(identifier);
    winrt::check_hresult(defaults.enumerator->GetDevice(target.c_str(), device.put()));
    DWORD state = 0;
    winrt::check_hresult(device->GetState(&state));
    if (state != DEVICE_STATE_ACTIVE) throw winrt::hresult_error(HRESULT_FROM_WIN32(ERROR_NOT_FOUND), L"目标音频设备未连接。");
    auto endpoint = device.as<IMMEndpoint>();
    EDataFlow flow = eAll;
    winrt::check_hresult(endpoint->GetDataFlow(&flow));
    if (flow != expected) throw winrt::hresult_invalid_argument(L"目标设备类型不匹配，请重新选择播放设备或麦克风。");
    return audio_id(device);
}
}

namespace platform {
struct MicrophoneCaptureState {
    winrt::com_ptr<IAudioClient> client;
    winrt::com_ptr<IAudioCaptureClient> capture;
    winrt::handle ready{CreateEventW(nullptr, FALSE, FALSE, nullptr)};
    bool started = false;
    ~MicrophoneCaptureState() { if (started) client->Stop(); }
};

MicrophoneCapture::MicrophoneCapture(std::wstring_view identifier) : state(std::make_unique<MicrophoneCaptureState>()) {
    if (!state->ready) winrt::throw_last_error();
    AudioDefaults defaults;
    const auto target = validated_audio_id(defaults, identifier, eCapture);
    if (target.empty()) throw winrt::hresult_invalid_argument();
    winrt::com_ptr<IMMDevice> device;
    winrt::check_hresult(defaults.enumerator->GetDevice(target.c_str(), device.put()));
    winrt::check_hresult(device->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr, state->client.put_void()));
    WAVEFORMATEX format{};
    format.wFormatTag = WAVE_FORMAT_PCM;
    format.nChannels = 1;
    format.nSamplesPerSec = 48000;
    format.wBitsPerSample = 16;
    format.nBlockAlign = 2;
    format.nAvgBytesPerSec = 96000;
    constexpr DWORD flags = AUDCLNT_STREAMFLAGS_EVENTCALLBACK | AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
    winrt::check_hresult(state->client->Initialize(AUDCLNT_SHAREMODE_SHARED, flags, 400000, 0, &format, nullptr));
    winrt::check_hresult(state->client->SetEventHandle(state->ready.get()));
    winrt::check_hresult(state->client->GetService(__uuidof(IAudioCaptureClient), state->capture.put_void()));
    winrt::check_hresult(state->client->Start());
    state->started = true;
}

MicrophoneCapture::~MicrophoneCapture() = default;

void MicrophoneCapture::wait(uint32_t milliseconds) const {
    if (WaitForSingleObject(state->ready.get(), milliseconds) == WAIT_FAILED) winrt::throw_last_error();
}

MicrophoneProgress MicrophoneCapture::consume() {
    MicrophoneProgress progress;
    for (uint32_t index = 0; index < 256; ++index) {
        UINT32 available = 0;
        winrt::check_hresult(state->capture->GetNextPacketSize(&available));
        if (available == 0) break;
        BYTE* packet = nullptr;
        UINT32 frames = 0;
        DWORD flags = 0;
        winrt::check_hresult(state->capture->GetBuffer(&packet, &frames, &flags, nullptr, nullptr));
        HRESULT released = S_OK;
        {
            ScopeExit release([&] { released = state->capture->ReleaseBuffer(frames); });
            progress.frames += frames;
            ++progress.packets;
        }
        winrt::check_hresult(released);
    }
    return progress;
}

std::vector<AudioDevice> audio_devices(uint32_t flow) {
    if (flow > 1) throw winrt::hresult_invalid_argument();
    ApartmentScope apartment;
    AudioDefaults defaults;
    std::array<std::wstring, 3> current;
    for (uint32_t role = 0; role < current.size(); ++role) {
        try { current[role] = defaults.current(flow * 3 + role); }
        catch (const winrt::hresult_error&) { }
    }
    winrt::com_ptr<IMMDeviceCollection> collection;
    winrt::check_hresult(defaults.enumerator->EnumAudioEndpoints(static_cast<EDataFlow>(flow), DEVICE_STATE_ACTIVE, collection.put()));
    uint32_t count = 0;
    winrt::check_hresult(collection->GetCount(&count));
    std::vector<AudioDevice> result;
    for (uint32_t index = 0; index < count; ++index) {
        winrt::com_ptr<IMMDevice> device;
        winrt::check_hresult(collection->Item(index, device.put()));
        DWORD state = 0;
        winrt::check_hresult(device->GetState(&state));
        if (state != DEVICE_STATE_ACTIVE) continue;
        AudioDevice entry;
        entry.id = audio_id(device);
        winrt::com_ptr<IPropertyStore> properties;
        winrt::check_hresult(device->OpenPropertyStore(STGM_READ, properties.put()));
        constexpr PROPERTYKEY friendly_name{{0xa45c254e, 0xdf1c, 0x4efd, {0x80, 0x20, 0x67, 0xd1, 0x46, 0xa8, 0x50, 0xe0}}, 14};
        PropertyValue name;
        winrt::check_hresult(properties->GetValue(friendly_name, &name.value));
        entry.name = name.text();
        if (entry.name.empty()) entry.name = entry.id;
        for (uint32_t role = 0; role < current.size(); ++role) {
            if (current[role] == entry.id) entry.default_roles |= 1u << role;
        }
        result.push_back(std::move(entry));
    }
    return result;
}

void restore_audio(std::wstring_view playback, std::wstring_view microphone) {
    ApartmentScope apartment;
    AudioDefaults defaults;
    const auto playback_id = validated_audio_id(defaults, playback, eRender);
    const auto microphone_id = validated_audio_id(defaults, microphone, eCapture);
    restore_audio_devices(playback_id, microphone_id, defaults);
}

void register_steamvr(std::wstring_view manifest, bool enabled) {
    constexpr auto key = "org.liangmengxi.doubaovr";
    const std::wstring file(manifest);
    const auto attributes = GetFileAttributesW(file.c_str());
    if (attributes == INVALID_FILE_ATTRIBUTES || (attributes & FILE_ATTRIBUTE_DIRECTORY) != 0) {
        throw winrt::hresult_invalid_argument(L"SteamVR 注册文件不存在。");
    }
    vr::EVRInitError init_error = vr::VRInitError_None;
    vr::VR_Init(&init_error, vr::VRApplication_Utility);
    if (init_error != vr::VRInitError_None) throw winrt::hresult_error(E_FAIL, L"无法连接 SteamVR，请确认 SteamVR 已安装。" + winrt::to_hstring(vr::VR_GetVRInitErrorAsEnglishDescription(init_error)));
    ScopeExit runtime([] { vr::VR_Shutdown(); });
    if (!vr::VRApplications()) throw winrt::hresult_error(E_NOINTERFACE, L"SteamVR 应用注册接口不可用。");
    auto& applications = *vr::VRApplications();
    const auto path = winrt::to_string(file);
    const bool installed = applications.IsApplicationInstalled(key);
    const bool previous = installed && applications.GetApplicationAutoLaunch(key);
    auto check = [&](vr::EVRApplicationError result) {
        if (result != vr::VRApplicationError_None) throw winrt::hresult_error(E_FAIL, L"SteamVR 注册失败：" + winrt::to_hstring(applications.GetApplicationsErrorNameFromEnum(result)));
    };
    try {
        if (enabled) {
            check(applications.AddApplicationManifest(path.c_str(), false));
            for (uint32_t attempt = 0; attempt < 20 && !applications.IsApplicationInstalled(key); ++attempt) std::this_thread::sleep_for(std::chrono::milliseconds(50));
            if (!applications.IsApplicationInstalled(key)) throw winrt::hresult_error(E_FAIL, L"SteamVR 尚未读取注册文件，请重启 SteamVR 后重试。");
            check(applications.SetApplicationAutoLaunch(key, true));
            if (!applications.GetApplicationAutoLaunch(key)) throw winrt::hresult_error(E_FAIL, L"SteamVR 未保存自启动设置。");
        } else if (installed) {
            check(applications.SetApplicationAutoLaunch(key, false));
            if (applications.GetApplicationAutoLaunch(key)) throw winrt::hresult_error(E_FAIL, L"SteamVR 未关闭自启动。");
            check(applications.RemoveApplicationManifest(path.c_str()));
            if (applications.IsApplicationInstalled(key)) throw winrt::hresult_error(E_FAIL, L"SteamVR 未移除应用注册。");
        }
    } catch (...) {
        bool restored = true;
        if (installed) {
            if (!applications.IsApplicationInstalled(key) && applications.AddApplicationManifest(path.c_str(), false) != vr::VRApplicationError_None) restored = false;
            if (applications.SetApplicationAutoLaunch(key, previous) != vr::VRApplicationError_None || applications.GetApplicationAutoLaunch(key) != previous) restored = false;
        } else if (applications.IsApplicationInstalled(key)) {
            applications.SetApplicationAutoLaunch(key, false);
            if (applications.RemoveApplicationManifest(path.c_str()) != vr::VRApplicationError_None || applications.IsApplicationInstalled(key)) restored = false;
        }
        if (!restored) throw winrt::hresult_error(E_FAIL, L"SteamVR 注册失败，原状态未能完整恢复，请在 SteamVR 设置中检查启动项。");
        throw;
    }
}

uint32_t submit_overlay(uint64_t handle, const winrt::com_ptr<ID3D11Texture2D>& texture,
    const winrt::com_ptr<ID3D11DeviceContext>& context) {
    vr::Texture_t descriptor{texture.get(), vr::TextureType_DirectX, vr::ColorSpace_Auto};
    auto result = vr::VROverlay()->SetOverlayTexture(handle, &descriptor);
    context->Flush();
    return static_cast<uint32_t>(result);
}
winrt::com_ptr<ID3DBlob> compile_shader(std::string_view source, std::string_view entry, std::string_view profile) {
    winrt::com_ptr<ID3DBlob> code;
    winrt::com_ptr<ID3DBlob> errors;
    auto result = D3DCompile(source.data(), source.size(), nullptr, nullptr, nullptr, entry.data(), profile.data(),
        D3DCOMPILE_ENABLE_STRICTNESS | D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, code.put(), errors.put());
    if (FAILED(result) && errors) {
        throw winrt::hresult_error(result, winrt::to_hstring(std::string_view(
            static_cast<const char*>(errors->GetBufferPointer()), errors->GetBufferSize())));
    }
    winrt::check_hresult(result);
    return code;
}

winrt::com_ptr<ID3D11Texture2D> bgra_texture(const winrt::com_ptr<ID3D11Device>& device,
    uint32_t width, uint32_t height, std::span<const uint32_t> pixels) {
    if (static_cast<uint64_t>(width) * height != pixels.size()) throw std::invalid_argument("Invalid source pixel count.");
    D3D11_TEXTURE2D_DESC desc{};
    desc.Width = width;
    desc.Height = height;
    desc.MipLevels = desc.ArraySize = 1;
    desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    desc.SampleDesc.Count = 1;
    desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
    D3D11_SUBRESOURCE_DATA data{pixels.data(), width * 4, 0};
    winrt::com_ptr<ID3D11Texture2D> texture;
    winrt::check_hresult(device->CreateTexture2D(&desc, &data, texture.put()));
    return texture;
}

void bind_compute(const winrt::com_ptr<ID3D11DeviceContext>& context,
    const winrt::com_ptr<ID3D11ShaderResourceView>& input,
    const winrt::com_ptr<ID3D11UnorderedAccessView>& target,
    const winrt::com_ptr<ID3D11Buffer>& constants) {
    const std::array resources{input.get()};
    const std::array targets{target.get()};
    const std::array buffers{constants.get()};
    context->CSSetShaderResources(0, 1, resources.data());
    context->CSSetUnorderedAccessViews(0, 1, targets.data(), nullptr);
    context->CSSetConstantBuffers(0, 1, buffers.data());
}

void unbind_compute(const winrt::com_ptr<ID3D11DeviceContext>& context) {
    const std::array<ID3D11ShaderResourceView*, 1> resources{};
    const std::array<ID3D11UnorderedAccessView*, 1> targets{};
    context->CSSetShaderResources(0, 1, resources.data());
    context->CSSetUnorderedAccessViews(0, 1, targets.data(), nullptr);
}

void bind_pixel(const winrt::com_ptr<ID3D11DeviceContext>& context,
    const winrt::com_ptr<ID3D11RenderTargetView>& target,
    const winrt::com_ptr<ID3D11ShaderResourceView>& input,
    const winrt::com_ptr<ID3D11Buffer>& constants,
    const winrt::com_ptr<ID3D11SamplerState>& sampler) {
    const std::array targets{target.get()};
    const std::array resources{input.get()};
    const std::array buffers{constants.get()};
    const std::array samplers{sampler.get()};
    context->OMSetRenderTargets(1, targets.data(), nullptr);
    context->PSSetShaderResources(0, 1, resources.data());
    context->PSSetConstantBuffers(0, 1, buffers.data());
    context->PSSetSamplers(0, 1, samplers.data());
}

void unbind_pixel(const winrt::com_ptr<ID3D11DeviceContext>& context) {
    const std::array<ID3D11ShaderResourceView*, 1> resources{};
    context->PSSetShaderResources(0, 1, resources.data());
    context->OMSetRenderTargets(0, nullptr, nullptr);
}

ReadMapping::ReadMapping(winrt::com_ptr<ID3D11DeviceContext> context_value,
    winrt::com_ptr<ID3D11Resource> resource_value) : context(std::move(context_value)), resource(std::move(resource_value)) {
    winrt::check_hresult(context->Map(resource.get(), 0, D3D11_MAP_READ, D3D11_MAP_FLAG_DO_NOT_WAIT, &mapping));
}

ReadMapping::~ReadMapping() { context->Unmap(resource.get(), 0); }

std::array<uint32_t, 4> ReadMapping::extent() const {
    std::array<uint32_t, 4> result{};
    std::memcpy(result.data(), mapping.pData, sizeof(result));
    return result;
}

std::vector<uint8_t> ReadMapping::bgra(uint32_t width, uint32_t height) const {
    if (width == 0 || height == 0 || width > 640 || height > 240 || mapping.RowPitch < width * 4) {
        throw std::invalid_argument("Invalid preview mapping dimensions.");
    }
    std::span input{static_cast<const uint8_t*>(mapping.pData), static_cast<size_t>(mapping.RowPitch) * height};
    std::vector<uint8_t> result(static_cast<size_t>(width) * height * 4);
    std::span output{result};
    for (uint32_t row = 0; row < height; ++row) {
        auto source = input.subspan(static_cast<size_t>(row) * mapping.RowPitch, static_cast<size_t>(width) * 4);
        auto destination = output.subspan(static_cast<size_t>(row) * width * 4, static_cast<size_t>(width) * 4);
        for (uint32_t column = 0; column < width; ++column) {
            auto offset = column * 4;
            auto alpha = source[offset + 3];
            destination[offset] = static_cast<uint8_t>(source[offset + 2] * alpha / 255);
            destination[offset + 1] = static_cast<uint8_t>(source[offset + 1] * alpha / 255);
            destination[offset + 2] = static_cast<uint8_t>(source[offset] * alpha / 255);
            destination[offset + 3] = alpha;
        }
    }
    return result;
}
}
