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
#include <format>
#include "bridge.h"
#include "caption_close.h"
#include <filesystem>
#include <windows.graphics.capture.interop.h>
#include <windows.graphics.directx.direct3d11.interop.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Graphics.Capture.h>
#include <winrt/Windows.Graphics.DirectX.h>
#include <winrt/Windows.Graphics.DirectX.Direct3D11.h>

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
namespace {
struct CaptionIdentity {
    uint64_t started = 0;
    uint32_t width = 0, height = 0, dpi = 0;
};

bool caption_identity(HWND window, uint32_t process_id, CaptionIdentity& identity) {
    if (!window || process_id == 0) throw winrt::hresult_invalid_argument(L"字幕窗口标识无效。");
    if (!IsWindow(window)) return false;
    DWORD owner = 0;
    GetWindowThreadProcessId(window, &owner);
    if (owner != process_id) throw winrt::hresult_access_denied(L"字幕窗口归属已变化，已取消关闭。");
    if (!IsWindowVisible(window) || IsIconic(window)) return false;
    std::array<wchar_t, 256> class_name{};
    GetClassNameW(window, class_name.data(), static_cast<int>(class_name.size()));
    RECT bounds{}, client{};
    if (!GetWindowRect(window, &bounds) || !GetClientRect(window, &client)) winrt::throw_last_error();
    const int width = bounds.right - bounds.left, height = bounds.bottom - bounds.top;
    const double ratio = width / static_cast<double>(std::max(1, height));
    if (GetWindowTextLengthW(window) != 0 || std::wstring_view(class_name.data()) != L"Chrome_WidgetWin_1"
        || (GetWindowLongPtrW(window, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) == 0
        || width < 350 || height < 40 || !(ratio > 2.5 || (width >= 600 && ratio >= .7 && ratio <= 2.5))) {
        throw winrt::hresult_access_denied(L"目标不是已核实的豆包字幕窗口，已取消关闭。");
    }
    winrt::handle process{OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, process_id)};
    if (!process) winrt::throw_last_error();
    std::wstring path(32768, L'\0');
    DWORD length = static_cast<DWORD>(path.size());
    if (!QueryFullProcessImageNameW(process.get(), 0, path.data(), &length)) winrt::throw_last_error();
    path.resize(length);
    if (_wcsicmp(std::filesystem::path(path).filename().c_str(), L"doubao.exe") != 0) {
        throw winrt::hresult_access_denied(L"目标不属于豆包，已取消关闭。");
    }
    FILETIME created{}, exited{}, kernel{}, user{};
    if (!GetProcessTimes(process.get(), &created, &exited, &kernel, &user)) winrt::throw_last_error();
    identity.started = static_cast<uint64_t>(created.dwHighDateTime) << 32 | created.dwLowDateTime;
    identity.width = static_cast<uint32_t>(std::max(0L, client.right - client.left));
    identity.height = static_cast<uint32_t>(std::max(0L, client.bottom - client.top));
    identity.dpi = GetDpiForWindow(window);
    if (identity.width == 0 || identity.height == 0 || identity.dpi < 96 || identity.dpi > 384) {
        throw winrt::hresult_invalid_argument(L"字幕窗口尺寸或缩放不受支持。");
    }
    return true;
}

struct CaptionCapture {
    winrt::Windows::Graphics::Capture::Direct3D11CaptureFramePool pool{nullptr};
    winrt::Windows::Graphics::Capture::GraphicsCaptureSession session{nullptr};
    ~CaptionCapture() noexcept {
        if (session) { try { session.Close(); } catch (...) { } }
        if (pool) { try { pool.Close(); } catch (...) { } }
    }
};
}

bool caption_button(uintptr_t identifier, uint32_t process_id, BridgeCaptionButton& button) {
    using namespace winrt::Windows::Graphics::Capture;
    const auto window = reinterpret_cast<HWND>(identifier);
    CaptionIdentity identity;
    if (!caption_identity(window, process_id, identity)) return false;
    ApartmentScope apartment;
    winrt::com_ptr<ID3D11Device> device;
    winrt::com_ptr<ID3D11DeviceContext> context;
    check_result(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
        nullptr, 0, D3D11_SDK_VERSION, device.put(), nullptr, context.put()), L"创建字幕关闭识别设备");
    winrt::com_ptr<IInspectable> inspectable;
    winrt::check_hresult(CreateDirect3D11DeviceFromDXGIDevice(device.as<IDXGIDevice>().get(), inspectable.put()));
    const auto capture_device = inspectable.as<winrt::Windows::Graphics::DirectX::Direct3D11::IDirect3DDevice>();
    auto interop = winrt::get_activation_factory<IGraphicsCaptureItemInterop>(L"Windows.Graphics.Capture.GraphicsCaptureItem");
    GraphicsCaptureItem item{nullptr};
    check_result(interop->CreateForWindow(window, winrt::guid_of<GraphicsCaptureItem>(), winrt::put_abi(item)), L"读取字幕关闭按钮所在窗口");
    const auto size = item.Size();
    if (size.Width <= 0 || size.Height <= 0 || static_cast<uint64_t>(size.Width) * size.Height > 16 * 1024 * 1024) {
        throw winrt::hresult_invalid_argument(L"字幕窗口过大，无法安全识别关闭按钮。");
    }
    CaptionCapture capture;
    const auto pool_factory = winrt::get_activation_factory<IDirect3D11CaptureFramePoolStatics2>(L"Windows.Graphics.Capture.Direct3D11CaptureFramePool");
    capture.pool = pool_factory.CreateFreeThreaded(capture_device,
        winrt::Windows::Graphics::DirectX::DirectXPixelFormat::B8G8R8A8UIntNormalized, 1, size);
    capture.session = capture.pool.CreateCaptureSession(item);
    capture.session.StartCapture();
    Direct3D11CaptureFrame frame{nullptr};
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(2);
    while (!frame && std::chrono::steady_clock::now() < deadline) {
        frame = capture.pool.TryGetNextFrame();
        if (!frame) std::this_thread::sleep_for(std::chrono::milliseconds(10));
    }
    if (!frame) throw winrt::hresult_error(HRESULT_FROM_WIN32(ERROR_TIMEOUT), L"未能读取字幕工具栏，请稍后重试。");
    ScopeExit close_frame([&] { frame.Close(); });
    if (frame.ContentSize() != size) throw winrt::hresult_error(E_ABORT, L"字幕窗口正在缩放，请稍后重试。");
    auto access = frame.Surface().as<::Windows::Graphics::DirectX::Direct3D11::IDirect3DDxgiInterfaceAccess>();
    winrt::com_ptr<ID3D11Texture2D> source;
    winrt::check_hresult(access->GetInterface(winrt::guid_of<ID3D11Texture2D>(), source.put_void()));
    D3D11_TEXTURE2D_DESC source_description{};
    source->GetDesc(&source_description);
    const auto width = static_cast<uint32_t>(size.Width), height = static_cast<uint32_t>(size.Height);
    const uint32_t left = width * 7 / 10;
    const uint32_t toolbar_height = std::min(height, 75 * identity.dpi / 96);
    if (width > source_description.Width || height > source_description.Height || width - left > 4096 || toolbar_height == 0) {
        throw winrt::hresult_invalid_argument(L"字幕工具栏尺寸不受支持。");
    }
    D3D11_TEXTURE2D_DESC description{};
    description.Width = width - left;
    description.Height = toolbar_height;
    description.MipLevels = description.ArraySize = description.SampleDesc.Count = 1;
    description.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    description.Usage = D3D11_USAGE_STAGING;
    description.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    winrt::com_ptr<ID3D11Texture2D> staging;
    winrt::check_hresult(device->CreateTexture2D(&description, nullptr, staging.put()));
    D3D11_BOX region{left, 0, 0, width, toolbar_height, 1};
    context->CopySubresourceRegion(staging.get(), 0, 0, 0, 0, source.get(), 0, &region);
    D3D11_QUERY_DESC query_description{D3D11_QUERY_EVENT, 0};
    winrt::com_ptr<ID3D11Query> query;
    winrt::check_hresult(device->CreateQuery(&query_description, query.put()));
    context->End(query.get());
    context->Flush();
    const auto read_deadline = std::chrono::steady_clock::now() + std::chrono::milliseconds(250);
    BOOL ready = FALSE;
    while (true) {
        const auto result = context->GetData(query.get(), &ready, sizeof(ready), D3D11_ASYNC_GETDATA_DONOTFLUSH);
        winrt::check_hresult(result);
        if (result == S_OK && ready) break;
        if (std::chrono::steady_clock::now() >= read_deadline) throw winrt::hresult_error(DXGI_ERROR_DEVICE_HUNG, L"字幕关闭识别等待 GPU 超时，请稍后重试。");
        std::this_thread::sleep_for(std::chrono::milliseconds(1));
    }
    std::vector<uint8_t> pixels;
    {
        ReadMapping mapping(context, staging.as<ID3D11Resource>());
        pixels = mapping.caption_pixels(description.Width, description.Height);
    }
    const auto point = caption_close::locate(pixels, description.Width, description.Height, identity.dpi);
    if (!point) throw winrt::hresult_error(HRESULT_FROM_WIN32(ERROR_NOT_FOUND), L"未能唯一识别字幕关闭按钮，请展开豆包字幕条后重试。");
    CaptionIdentity current;
    if (!caption_identity(window, process_id, current)) return false;
    if (current.started != identity.started || current.width != identity.width || current.height != identity.height || current.dpi != identity.dpi) {
        throw winrt::hresult_error(E_ABORT, L"字幕窗口已变化，请重试。");
    }
    button.process_id = process_id;
    button.window = identifier;
    button.observed = GetTickCount64();
    button.process_started = identity.started;
    button.client_width = identity.width;
    button.client_height = identity.height;
    button.source_width = width;
    button.source_height = height;
    button.horizontal = static_cast<uint32_t>(std::lround((left + point->horizontal) * static_cast<double>(identity.width) / width));
    button.vertical = static_cast<uint32_t>(std::lround(point->vertical * static_cast<double>(identity.height) / height));
    button.dpi = identity.dpi;
    return true;
}

bool close_caption(const BridgeCaptionButton& button) {
    const auto now = GetTickCount64();
    if (button.observed == 0 || button.observed > now || now - button.observed > 1000
        || button.process_started == 0 || button.source_width == 0 || button.source_height == 0
        || button.horizontal >= button.client_width || button.vertical >= button.client_height
        || button.horizontal > 32767 || button.vertical > 32767) {
        throw winrt::hresult_invalid_argument(L"字幕关闭按钮快照无效或已过期，请重试。");
    }
    const auto window = reinterpret_cast<HWND>(button.window);
    CaptionIdentity current;
    if (!caption_identity(window, button.process_id, current)) return false;
    if (current.started != button.process_started || current.width != button.client_width
        || current.height != button.client_height || current.dpi != button.dpi) {
        throw winrt::hresult_error(E_ABORT, L"字幕窗口已变化，已取消关闭。");
    }
    const LPARAM point = MAKELPARAM(button.horizontal, button.vertical);
    if (!PostMessageW(window, WM_MOUSEMOVE, 0, point) || !PostMessageW(window, WM_LBUTTONDOWN, MK_LBUTTON, point)
        || !PostMessageW(window, WM_LBUTTONUP, 0, point)) {
        throw winrt::hresult_error(HRESULT_FROM_WIN32(GetLastError()), L"无法调用字幕关闭按钮，请确认豆包与本程序以相同权限运行。");
    }
    return true;
}

std::wstring error_text(std::wstring_view stage, HRESULT code, std::wstring_view details) {
    return std::format(L"{}（0x{:08X}）：{}", stage, static_cast<uint32_t>(code), details);
}

void check_result(HRESULT code, std::wstring_view stage) {
    if (FAILED(code)) {
        const winrt::hresult_error failure(code);
        throw winrt::hresult_error(code, winrt::hstring(error_text(stage, code, std::wstring(failure.message()))));
    }
}

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

class StartupRuntime {
public:
    StartupRuntime() {
        vr::EVRInitError error = vr::VRInitError_None;
        vr::VR_Init(&error, vr::VRApplication_Utility);
        if (error != vr::VRInitError_None) throw winrt::hresult_error(E_FAIL,
            L"无法连接 SteamVR，请确认 SteamVR 已安装。" + winrt::to_hstring(vr::VR_GetVRInitErrorAsEnglishDescription(error)));
        if (!vr::VRApplications()) {
            vr::VR_Shutdown();
            throw winrt::hresult_error(E_NOINTERFACE, L"SteamVR 应用注册接口不可用。");
        }
    }
    ~StartupRuntime() { vr::VR_Shutdown(); }
    StartupRuntime(const StartupRuntime&) = delete;
    StartupRuntime& operator=(const StartupRuntime&) = delete;
};

constexpr char startup_key[] = "org.liangmengxi.doubaovr";

void startup_check(vr::EVRApplicationError result) {
    if (result != vr::VRApplicationError_None) throw winrt::hresult_error(E_FAIL,
        L"SteamVR 注册操作失败：" + winrt::to_hstring(vr::VRApplications()->GetApplicationsErrorNameFromEnum(result)));
}

SteamVrState steamvr_state() {
    StartupRuntime runtime;
    auto& applications = *vr::VRApplications();
    SteamVrState result;
    result.installed = applications.IsApplicationInstalled(startup_key);
    if (!result.installed) return result;
    auto property = [&](vr::EVRApplicationProperty name) {
        std::array<char, 16384> value{};
        vr::EVRApplicationError error = vr::VRApplicationError_None;
        const auto length = applications.GetApplicationPropertyString(startup_key, name, value.data(), static_cast<uint32_t>(value.size()), &error);
        startup_check(error);
        if (!length || length > value.size() || value[length - 1] != '\0') throw winrt::hresult_error(E_BOUNDS, L"SteamVR 启动项属性无效。");
        return std::wstring(winrt::to_hstring(std::string_view(value.data(), length - 1)));
    };
    result.executable = property(vr::VRApplicationProperty_BinaryPath_String);
    result.arguments = property(vr::VRApplicationProperty_Arguments_String);
    vr::EVRApplicationError error = vr::VRApplicationError_None;
    result.dashboard = applications.GetApplicationPropertyBool(startup_key, vr::VRApplicationProperty_IsDashboardOverlay_Bool, &error);
    startup_check(error);
    result.auto_launch = applications.GetApplicationAutoLaunch(startup_key);
    if (!applications.IsApplicationInstalled(startup_key)) throw winrt::hresult_error(E_ABORT, L"SteamVR 启动项在读取时发生变化，请重试。");
    return result;
}

void register_steamvr(std::wstring_view manifest, bool installed, bool auto_launch) {
    const std::wstring file(manifest);
    const auto attributes = GetFileAttributesW(file.c_str());
    if (attributes == INVALID_FILE_ATTRIBUTES || (attributes & FILE_ATTRIBUTE_DIRECTORY) != 0) {
        throw winrt::hresult_invalid_argument(L"SteamVR 注册文件不存在。");
    }
    StartupRuntime runtime;
    auto& applications = *vr::VRApplications();
    const auto path = winrt::to_string(file);
    if (installed) {
        startup_check(applications.AddApplicationManifest(path.c_str(), false));
        for (uint32_t attempt = 0; attempt < 20 && !applications.IsApplicationInstalled(startup_key); ++attempt) std::this_thread::sleep_for(std::chrono::milliseconds(50));
        if (!applications.IsApplicationInstalled(startup_key)) throw winrt::hresult_error(E_FAIL, L"SteamVR 尚未读取注册文件，请重试。");
        startup_check(applications.SetApplicationAutoLaunch(startup_key, auto_launch));
        if (applications.GetApplicationAutoLaunch(startup_key) != auto_launch) throw winrt::hresult_error(E_FAIL, L"SteamVR 未保存自启动设置。");
    } else if (applications.IsApplicationInstalled(startup_key)) {
        startup_check(applications.SetApplicationAutoLaunch(startup_key, false));
        if (applications.GetApplicationAutoLaunch(startup_key)) throw winrt::hresult_error(E_FAIL, L"SteamVR 未关闭自启动。");
        startup_check(applications.RemoveApplicationManifest(path.c_str()));
        if (applications.IsApplicationInstalled(startup_key)) throw winrt::hresult_error(E_FAIL, L"SteamVR 未移除应用注册。");
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

void ReadMapping::bgra(uint32_t width, uint32_t height, std::vector<uint8_t>& result) const {
    if (width == 0 || height == 0 || width > 640 || height > 240 || mapping.RowPitch < width * 4) {
        throw std::invalid_argument("Invalid preview mapping dimensions.");
    }
    std::span input{static_cast<const uint8_t*>(mapping.pData), static_cast<size_t>(mapping.RowPitch) * height};
    const auto length = static_cast<size_t>(width) * height * 4;
    if (length > result.capacity()) {
        const auto expanded = std::max(length, result.capacity() + result.capacity() / 2);
        result.reserve(std::min<size_t>(bridge_preview_capacity, expanded));
    }
    result.resize(length);
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
}

std::vector<uint8_t> ReadMapping::caption_pixels(uint32_t width, uint32_t height) const {
    if (width == 0 || height == 0 || width > 4096 || height > 300 || mapping.RowPitch < width * 4) {
        throw std::invalid_argument("Invalid caption toolbar mapping dimensions.");
    }
    std::span input{static_cast<const uint8_t*>(mapping.pData), static_cast<size_t>(mapping.RowPitch) * height};
    std::vector<uint8_t> result(static_cast<size_t>(width) * height * 4);
    for (uint32_t row = 0; row < height; ++row) {
        std::memcpy(result.data() + static_cast<size_t>(row) * width * 4,
            input.data() + static_cast<size_t>(row) * mapping.RowPitch, static_cast<size_t>(width) * 4);
    }
    return result;
}
}
