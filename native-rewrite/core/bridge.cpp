#include "bridge.h"
#include "graphics.h"
#include "platform.h"
#include "overlay_transform.h"
#include "overlay_curvature.h"
#include "microphone_keeper.h"
#include "assistant.h"
#include <windows.h>
#include <windows.graphics.capture.interop.h>
#include <windows.graphics.directx.direct3d11.interop.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Graphics.Capture.h>
#include <winrt/Windows.Graphics.DirectX.h>
#include <winrt/Windows.Graphics.DirectX.Direct3D11.h>
#include <openvr.h>
#include <algorithm>
#include <atomic>
#include <cmath>
#include <memory>
#include <mutex>
#include <thread>
#include <string>
#include <unordered_map>
#include <limits>
#include <filesystem>

using namespace winrt;
using namespace winrt::Windows::Graphics;
using namespace winrt::Windows::Graphics::Capture;
using namespace winrt::Windows::Graphics::DirectX;
using namespace winrt::Windows::Graphics::DirectX::Direct3D11;
using Clock = std::chrono::steady_clock;

namespace {
struct Signal {
    winrt::handle value{CreateEventW(nullptr, FALSE, FALSE, nullptr)};
    Signal() { if (!value) throw_last_error(); }
    void wake() const { SetEvent(value.get()); }
};

struct CaptureLifetime {
    GraphicsCaptureItem item{nullptr};
    Direct3D11CaptureFramePool pool{nullptr};
    GraphicsCaptureSession session{nullptr};
    Direct3D11CaptureFramePool::FrameArrived_revoker frame_event;
    GraphicsCaptureItem::Closed_revoker close_event;
    std::shared_ptr<std::atomic<bool>> closed = std::make_shared<std::atomic<bool>>(false);
    std::shared_ptr<std::atomic<bool>> frame_arrived = std::make_shared<std::atomic<bool>>(false);
    void reset() noexcept {
        frame_event.revoke();
        close_event.revoke();
        if (session) { try { session.Close(); } catch (...) { } session = nullptr; }
        if (pool) { try { pool.Close(); } catch (...) { } pool = nullptr; }
        item = nullptr;
        closed = std::make_shared<std::atomic<bool>>(false);
        frame_arrived = std::make_shared<std::atomic<bool>>(false);
    }
    ~CaptureLifetime() noexcept { reset(); }
};

std::mutex openvr_mutex;

void overlay_check(vr::EVROverlayError error) {
    if (error != vr::VROverlayError_None) {
        throw hresult_error(E_FAIL, to_hstring(vr::VROverlay()->GetOverlayErrorNameFromEnum(error)));
    }
}

class Overlay {
public:
    Overlay(const std::optional<BridgeAssistantRequest>& request, const std::shared_ptr<AssistantMailbox>& mailbox)
        : runtime_lock(openvr_mutex, std::try_to_lock) {
        if (!runtime_lock.owns_lock()) throw hresult_error(E_UNEXPECTED, L"已有 VR 悬浮连接，请先关闭原连接。");
        vr::EVRInitError error = vr::VRInitError_None;
        for (int attempt = 0; attempt < 8; ++attempt) {
            error = vr::VRInitError_None;
            vr::VR_Init(&error, vr::VRApplication_Overlay);
            if (error == vr::VRInitError_None) break;
            if (error == vr::VRInitError_Driver_WirelessHmdNotConnected ||
                error == vr::VRInitError_Init_HmdNotFoundPresenceFailed) {
                std::this_thread::sleep_for(std::chrono::milliseconds(400));
                continue;
            }
            break;
        }
        if (error != vr::VRInitError_None) {
            if (error == vr::VRInitError_Driver_WirelessHmdNotConnected) {
                throw hresult_error(E_FAIL, L"无法连接 SteamVR：无线头显驱动未建立连接 (215)。如果您正在头显中串流，通常是因为 SteamVR 中存在闲置或冲突的无线驱动（如 AMD ReLive VR、旧版串流驱动），请在 SteamVR 设置 -> 启动/开发者 -> 管理外设驱动 中禁用闲置驱动后重试。");
            }
            throw hresult_error(E_FAIL, L"无法连接 SteamVR：" + to_hstring(vr::VR_GetVRInitErrorAsEnglishDescription(error)));
        }
        initialized = true;
        if (!request) {
            std::array<wchar_t, MAX_PATH> module_path{};
            const auto length = GetModuleFileNameW(nullptr, module_path.data(), static_cast<DWORD>(module_path.size()));
            if (length > 0 && length < module_path.size()) {
                std::error_code ec;
                const auto manifest = std::filesystem::path(module_path.data()).parent_path() / L"steamvr" / L"assistant_actions.json";
                if (std::filesystem::is_regular_file(manifest, ec) && vr::VRInput()) {
                    vr::VRInput()->SetActionManifestPath(manifest.string().c_str());
                }
            }
        }
        if (request) mailbox->update(1, L"SteamVR 已连接，正在创建悬浮层…");
        try {
            overlay_check(vr::VROverlay()->CreateOverlay(request ? "local.doubao.assistant.native" : "local.doubao.subtitle.bridge.native",
                request ? "Doubao VR Assistant" : "Doubao VR Native", &handle));
            overlay_check(vr::VROverlay()->SetOverlayInputMethod(handle, vr::VROverlayInputMethod_None));
            if (request) assistant = std::make_unique<AssistantInteraction>(handle, *request, mailbox);
        } catch (...) {
            close();
            throw;
        }
    }
    ~Overlay() { close(); }
    int adapter() const {
        int adapter_index = 0;
        vr::VRSystem()->GetDXGIOutputInfo(&adapter_index);
        return adapter_index;
    }
    bool configure(const BridgeOptions& options) {
        if (assistant) {
            assistant->set_width(options.width);
            assistant->set_reset_on_dashboard(options.crop_top != 0);
            overlay_check(vr::VROverlay()->SetOverlayWidthInMeters(handle, options.width));
            overlay_check(vr::VROverlay()->SetOverlayAlpha(handle, options.opacity));
            return false;
        }
        const auto matrix = overlay_transform(options);
        vr::HmdMatrix34_t transform{};
        for (size_t row = 0; row < matrix.size(); ++row) {
            for (size_t column = 0; column < matrix[row].size(); ++column) transform.m[row][column] = matrix[row][column];
        }
        overlay_check(vr::VROverlay()->SetOverlayWidthInMeters(handle, options.width));
        overlay_check(vr::VROverlay()->SetOverlayAlpha(handle, options.opacity));
        overlay_check(vr::VROverlay()->SetOverlayTransformTrackedDeviceRelative(handle,
            vr::k_unTrackedDeviceIndex_Hmd, &transform));
        const auto change = curved.update(options.curvature_degrees, [this](float degrees) {
            if (degrees > 0) {
                const auto result = vr::VROverlay()->SetOverlayPreCurvePitch(handle, 0.f);
                if (result != vr::VROverlayError_None) return static_cast<int32_t>(result);
            }
            return static_cast<int32_t>(vr::VROverlay()->SetOverlayCurvature(handle, OverlayCurvature::fraction(degrees)));
        }, [this]() -> std::optional<float> {
            float actual = 0;
            if (vr::VROverlay()->GetOverlayCurvature(handle, &actual) != vr::VROverlayError_None) return {};
            return actual * 360.f;
        });
        if (change == OverlayCurvature::Change::failed) {
            throw hresult_error(E_FAIL, L"曲面设置失败，无法确认已恢复平面，已停止字幕输出。"
                + to_hstring(vr::VROverlay()->GetOverlayErrorNameFromEnum(static_cast<vr::EVROverlayError>(curved.error())))
                + L" / " + to_hstring(vr::VROverlay()->GetOverlayErrorNameFromEnum(static_cast<vr::EVROverlayError>(curved.restore_error()))));
        }
        return change == OverlayCurvature::Change::updated;
    }
    const OverlayCurvature& curvature() const noexcept { return curved; }
    bool is_standby() const noexcept {
        if (!vr::VRSystem()) return false;
        const auto level = vr::VRSystem()->GetTrackedDeviceActivityLevel(vr::k_unTrackedDeviceIndex_Hmd);
        return level == vr::k_EDeviceActivityLevel_Standby || level == vr::k_EDeviceActivityLevel_UserInteraction_Timeout;
    }
    void submit(const com_ptr<ID3D11Texture2D>& texture, const com_ptr<ID3D11DeviceContext>& context) {
        std::unique_lock visibility_lock(visibility, std::defer_lock);
        if (assistant) {
            visibility_lock = std::unique_lock(assistant->visibility_mutex());
            D3D11_TEXTURE2D_DESC description{};
            texture->GetDesc(&description);
            assistant->dimensions(description.Width, description.Height);
        }
        const auto submit_result = static_cast<vr::EVROverlayError>(platform::submit_overlay(handle, texture, context));
        if (submit_result != vr::VROverlayError_None) {
            if (is_standby()) return;
            overlay_check(submit_result);
        }
        if (assistant && assistant->suppressed()) return;
        const auto show_result = vr::VROverlay()->ShowOverlay(handle);
        if (show_result != vr::VROverlayError_None) {
            if (is_standby()) return;
            overlay_check(show_result);
        }
    }
    void hide() { overlay_check(vr::VROverlay()->HideOverlay(handle)); }
    void reset_assistant_position() noexcept { if (assistant) assistant->reset_position(); }
    void clear_texture() { overlay_check(vr::VROverlay()->ClearOverlayTexture(handle)); }
    bool quitting() {
        vr::VREvent_t event{};
        for (int count = 0; count < 32 && vr::VROverlay()->PollNextOverlayEvent(handle, &event, sizeof(event)); ++count) {
            if (event.eventType == vr::VREvent_Quit) {
                if (vr::VRSystem()) vr::VRSystem()->AcknowledgeQuit_Exiting();
                return true;
            }
        }
        if (vr::VRSystem()) {
            for (int count = 0; count < 32 && vr::VRSystem()->PollNextEvent(&event, sizeof(event)); ++count) {
                if (event.eventType == vr::VREvent_Quit || event.eventType == vr::VREvent_ProcessQuit) {
                    vr::VRSystem()->AcknowledgeQuit_Exiting();
                    return true;
                }
            }
        }
        return false;
    }
private:
    void close() noexcept {
        assistant.reset();
        if (handle != vr::k_ulOverlayHandleInvalid) {
            vr::VROverlay()->HideOverlay(handle);
            vr::VROverlay()->ClearOverlayTexture(handle);
            vr::VROverlay()->DestroyOverlay(handle);
            handle = vr::k_ulOverlayHandleInvalid;
        }
        if (initialized) { vr::VR_Shutdown(); initialized = false; }
    }
    bool initialized = false;
    std::unique_lock<std::mutex> runtime_lock;
    std::mutex visibility;
    vr::VROverlayHandle_t handle = vr::k_ulOverlayHandleInvalid;
    std::unique_ptr<AssistantInteraction> assistant;
    OverlayCurvature curved;
};

bool valid(const BridgeOptions& options) {
    if (options.size != sizeof(BridgeOptions)) return false;
    auto range = [](float value, float minimum, float maximum) {
        return std::isfinite(value) && value >= minimum && value <= maximum;
    };
    return range(options.width, .25f, 2.5f) && range(options.distance, .4f, 3.f)
        && range(options.horizontal, -1.2f, 1.2f) && range(options.vertical, -1.2f, .8f)
        && range(options.opacity, .1f, 1.f) && range(options.pitch, -45.f, 45.f)
        && range(options.yaw, -45.f, 45.f)
        && range(options.curvature_degrees, 0.f, 90.f)
        && options.crop_top <= 120 && options.trim <= 1 && options.border <= 1
        && options.vr <= 1 && options.preview <= 1;
}

class Bridge : public std::enable_shared_from_this<Bridge> {
public:
    Bridge() : signal(std::make_shared<Signal>()) {}
    ~Bridge() { stop(); }
    int start(HWND window, const BridgeOptions& requested, std::optional<BridgeAssistantRequest> assistant_request = {}) {
        std::lock_guard control_guard(control);
        if (disposed) return E_HANDLE;
        if (!valid(requested)) return reject_start(L"捕获参数无效或接口结构不匹配。");
        if (!IsWindow(window)) return reject_start(L"字幕窗口已失效，请刷新来源后重新选择。");
        stop_worker();
        DWORD process_id = 0;
        GetWindowThreadProcessId(window, &process_id);
        {
            std::lock_guard lock(mutex);
            options = requested;
            error.clear();
            stats = {sizeof(BridgeStats), 1, 0, 0, 0, 0, 0};
            preview = {};
        }
        stopping = false;
        is_restarting.store(false);
        pending_rebind_window.store(0);
        {
            std::lock_guard lock(assistant_mailbox->mutex);
            assistant_mailbox->state = 0;
            assistant_mailbox->message.clear();
            assistant_mailbox->pixels.clear();
        }
        worker = std::jthread([self = shared_from_this(), window, process_id, assistant_request](std::stop_token stop_token) {
            self->run(stop_token, window, process_id, assistant_request);
        });
        return S_OK;
    }
    void stop() {
        std::lock_guard control_guard(control);
        stop_worker();
    }
    void dispose() {
        std::lock_guard control_guard(control);
        disposed = true;
        microphone.enable(false);
        stop_worker();
    }
    int configure_microphone(uint32_t enabled) {
        std::lock_guard control_guard(control);
        if (disposed) return E_HANDLE;
        if (enabled > 1) return E_INVALIDARG;
        microphone.enable(enabled != 0);
        return S_OK;
    }
    int microphone_stats(BridgeMicrophoneStats& output) {
        std::lock_guard control_guard(control);
        if (disposed) return E_HANDLE;
        output = microphone.snapshot();
        return S_OK;
    }
    int configure(const BridgeOptions& requested) {
        std::lock_guard control_guard(control);
        if (disposed) return E_HANDLE;
        if (!valid(requested)) return E_INVALIDARG;
        std::lock_guard lock(mutex);
        if (stats.state != 0 && requested.vr != options.vr) {
            error = L"切换 VR 需要重新连接捕获。";
            return E_INVALIDARG;
        }
        options = requested;
        signal->wake();
        return S_OK;
    }
    int set_restarting(uint32_t restarting) noexcept {
        std::lock_guard control_guard(control);
        if (disposed) return E_HANDLE;
        if (restarting > 1) return E_INVALIDARG;
        is_restarting.store(restarting != 0);
        signal->wake();
        return S_OK;
    }
    int rebind_window(HWND window) noexcept {
        std::lock_guard control_guard(control);
        if (disposed) return E_HANDLE;
        if (!window || !IsWindow(window)) return E_INVALIDARG;
        pending_rebind_window.store(reinterpret_cast<uintptr_t>(window));
        signal->wake();
        return S_OK;
    }
    BridgeOptions configuration() {
        std::lock_guard lock(mutex);
        return options;
    }
    void request_reset_assistant_position() noexcept {
        reset_assistant_requested.store(true);
        signal->wake();
    }
    void fail(std::wstring message) {
        std::lock_guard lock(mutex);
        error = std::move(message);
        stats.state = 3;
    }
    std::mutex mutex;
    BridgeStats stats{sizeof(BridgeStats), 0, 0, 0, 0, 0, 0};
    std::wstring error;
    PixelPreview preview;
    uint64_t preview_sequence = 0;
    std::shared_ptr<AssistantMailbox> assistant_mailbox = std::make_shared<AssistantMailbox>();
private:
    int reject_start(std::wstring_view details) {
        std::lock_guard lock(mutex);
        error = platform::error_text(L"检查捕获请求", E_INVALIDARG, details);
        return E_INVALIDARG;
    }
    void stop_worker() {
        stopping = true;
        is_restarting.store(false);
        pending_rebind_window.store(0);
        if (worker.joinable()) worker.request_stop();
        signal->wake();
        if (worker.joinable()) {
            HANDLE thread_handle = reinterpret_cast<HANDLE>(worker.native_handle());
            if (thread_handle) {
                DWORD wait_result = WaitForSingleObject(thread_handle, 2000);
                if (wait_result == WAIT_OBJECT_0) {
                    worker.join();
                } else {
                    worker.detach();
                }
            } else {
                worker.join();
            }
        }
        std::lock_guard lock(mutex);
        stats.state = 0;
        stats.curvature_fallback = 0;
        stats.curvature_degrees = 0;
        preview = {};
        std::lock_guard assistant_lock(assistant_mailbox->mutex);
        assistant_mailbox->state = 0;
        assistant_mailbox->message.clear();
        std::vector<uint8_t>().swap(assistant_mailbox->pixels);
    }
    void run(std::stop_token stop_token, HWND window, DWORD process_id, const std::optional<BridgeAssistantRequest>& assistant_request) noexcept {
        std::wstring_view stage = L"初始化捕获线程";
        try {
            platform::ApartmentScope apartment;
            capture(stop_token, window, process_id, stage, assistant_request);
        } catch (const hresult_error& exception) {
            if (!stop_token.stop_requested()) fail(platform::error_text(stage, exception.code(), std::wstring(exception.message())));
        } catch (const std::exception& exception) {
            if (!stop_token.stop_requested()) fail(std::wstring(stage) + L"：" + std::wstring(to_hstring(exception.what())));
        } catch (...) {
            if (!stop_token.stop_requested()) fail(L"原生捕获发生未知错误。");
        }
    }
    void capture(std::stop_token stop_token, HWND window, DWORD process_id, std::wstring_view& stage, const std::optional<BridgeAssistantRequest>& assistant_request) {
        if (stop_token.stop_requested()) return;
        stage = L"检查 Windows 窗口捕获支持";
        if (!GraphicsCaptureSession::IsSupported()) throw hresult_error(E_NOTIMPL, L"系统不支持 Windows Graphics Capture。");
        auto current = configuration();
        std::unique_ptr<Graphics> graphics_storage;
        stage = L"连接 SteamVR";
        if (assistant_request) assistant_mailbox->update(1, L"正在连接 SteamVR…");
        auto overlay = current.vr ? std::make_unique<Overlay>(assistant_request, assistant_mailbox) : nullptr;
        stage = L"初始化 Direct3D 图形资源";
        graphics_storage = std::make_unique<Graphics>(overlay ? overlay->adapter() : 0,
            [&overlay] { if (overlay) overlay->clear_texture(); });
        auto& graphics = *graphics_storage;
        com_ptr<IInspectable> inspectable;
        stage = L"创建窗口捕获 Direct3D 设备";
        check_hresult(CreateDirect3D11DeviceFromDXGIDevice(graphics.device.as<IDXGIDevice>().get(), inspectable.put()));
        auto capture_device = inspectable.as<IDirect3DDevice>();
        auto interop = get_activation_factory<GraphicsCaptureItem, IGraphicsCaptureItemInterop>();
        CaptureLifetime lifetime;
        SizeInt32 size{};
        auto bind_time = Clock::now();
        bool first_frame_nudged = false;
        uint32_t applied_border = current.border;
        auto bind_window = [&](HWND target_window, SizeInt32& current_size) {
            lifetime.reset();
            stage = L"创建目标窗口捕获对象（CreateForWindow）";
            if (!IsWindow(target_window)) throw hresult_error(E_INVALIDARG, L"目标窗口句柄无效或已被关闭。");
            HWND root = GetAncestor(target_window, GA_ROOT);
            if (root && IsWindow(root)) {
                target_window = root;
            }
            HRESULT hr = E_FAIL;
            for (int attempt = 0; attempt < 20 && !stop_token.stop_requested(); ++attempt) {
                if (!IsWindow(target_window)) {
                    std::this_thread::sleep_for(std::chrono::milliseconds(100));
                    continue;
                }
                hr = interop->CreateForWindow(target_window, guid_of<GraphicsCaptureItem>(), put_abi(lifetime.item));
                if (SUCCEEDED(hr) && lifetime.item) break;
                std::this_thread::sleep_for(std::chrono::milliseconds(100));
            }
            if (stop_token.stop_requested()) return;
            check_hresult(hr);
            stage = L"读取目标窗口捕获尺寸";
            current_size = lifetime.item.Size();
            for (int attempt = 0; attempt < 15 && (current_size.Width <= 0 || current_size.Height <= 0) && !stop_token.stop_requested(); ++attempt) {
                std::this_thread::sleep_for(std::chrono::milliseconds(100));
                current_size = lifetime.item.Size();
            }
            if (stop_token.stop_requested()) return;
            if (current_size.Width <= 0 || current_size.Height <= 0 || static_cast<uint64_t>(current_size.Width) * current_size.Height > 16 * 1024 * 1024) {
                throw hresult_error(E_OUTOFMEMORY, L"捕获窗口尺寸不受支持，请缩小字幕窗口。");
            }
            stage = L"创建窗口捕获帧池";
            lifetime.pool = Direct3D11CaptureFramePool::CreateFreeThreaded(capture_device,
                DirectXPixelFormat::B8G8R8A8UIntNormalized, 2, current_size);
            stage = L"创建窗口捕获会话";
            lifetime.session = lifetime.pool.CreateCaptureSession(lifetime.item);
            auto arrived_flag = lifetime.frame_arrived;
            lifetime.frame_event = lifetime.pool.FrameArrived(auto_revoke, [arrived_flag, wake = signal](auto&&, auto&&) {
                arrived_flag->store(true);
                wake->wake();
            });
            auto closed_flag = lifetime.closed;
            lifetime.close_event = lifetime.item.Closed(auto_revoke, [closed_flag, wake = signal](auto&&, auto&&) {
                closed_flag->store(true);
                wake->wake();
            });
            stage = L"设置捕获光标";
            lifetime.session.IsCursorCaptureEnabled(false);
            stage = L"设置窗口捕获边框";
            applied_border = current.border;
            lifetime.session.IsBorderRequired(applied_border != 0);
            stage = L"启动 Windows 窗口捕获";
            lifetime.session.StartCapture();
            bind_time = Clock::now();
            first_frame_nudged = false;
        };
        bind_window(window, size);
        if (stop_token.stop_requested()) return;
        {
            com_ptr<ID3D11Texture2D> saved;
            BridgeOptions rendered{};
            auto last_frame = Clock::now() - std::chrono::milliseconds(50);
            auto last_copy = Clock::now() - std::chrono::milliseconds(50);
            auto last_preview = Clock::now() - std::chrono::milliseconds(100);
            uint64_t serial = 0;
            uint64_t preview_serial = 0;
            bool pending = false;
            uint32_t surface_width = 0;
            uint32_t surface_height = 0;
            bool hidden = false;
            bool hold_old_frame = false;
            bool awaiting_rebind_frame = false;
            auto rebind_deadline = Clock::now();
            PixelPreview updated;
            while (!stop_token.stop_requested()) {
                stage = L"检查字幕窗口和 VR 连接状态";
                const bool defer_copy = !hold_old_frame && !hidden && lifetime.pool && lifetime.frame_arrived->load();
                const bool defer_render = !hidden && (current.vr || current.preview) && saved && pending;
                DWORD wait_ms = 50;
                if (defer_copy || defer_render) {
                    const auto now_wait = Clock::now();
                    auto earliest = now_wait + std::chrono::milliseconds(50);
                    if (defer_copy) earliest = std::min(earliest, last_copy + std::chrono::milliseconds(50));
                    if (defer_render) earliest = std::min(earliest, last_frame + std::chrono::milliseconds(50));
                    const auto remaining = std::chrono::duration_cast<std::chrono::milliseconds>(earliest - now_wait).count();
                    wait_ms = remaining <= 0 ? 0 : static_cast<DWORD>(std::clamp<long long>(remaining, 1, 50));
                }
                WaitForSingleObject(signal->value.get(), wait_ms);
                if (stop_token.stop_requested()) break;
                if (assistant_request) {
                    std::lock_guard lock(assistant_mailbox->mutex);
                    if (assistant_mailbox->state == 6) throw hresult_error(E_FAIL, hstring(assistant_mailbox->message));
                }
                if (overlay && reset_assistant_requested.exchange(false)) {
                    overlay->reset_assistant_position();
                }
                if (overlay && overlay->quitting()) throw hresult_error(E_ABORT, L"SteamVR 正在退出，请重新开启 VR 悬浮。");
                if (const auto next_hwnd = pending_rebind_window.exchange(0)) {
                    const auto candidate = reinterpret_cast<HWND>(next_hwnd);
                    DWORD next_owner = 0;
                    GetWindowThreadProcessId(candidate, &next_owner);
                    if (IsWindow(candidate) && next_owner != 0) {
                        try {
                            current = configuration();
                            bind_window(candidate, size);
                            if (stop_token.stop_requested()) break;
                            window = candidate;
                            process_id = next_owner;
                            awaiting_rebind_frame = true;
                            rebind_deadline = Clock::now() + std::chrono::seconds(5);
                            hidden = false;
                        } catch (...) {
                            is_restarting.store(false);
                            awaiting_rebind_frame = false;
                            throw;
                        }
                    } else {
                        is_restarting.store(false);
                        awaiting_rebind_frame = false;
                    }
                }
                const bool restarting_now = is_restarting.load();
                if (!restarting_now) awaiting_rebind_frame = false;
                else if (awaiting_rebind_frame && Clock::now() >= rebind_deadline) {
                    awaiting_rebind_frame = false;
                    is_restarting.store(false);
                }
                hold_old_frame = restarting_now && !awaiting_rebind_frame;
                if (!restarting_now && !awaiting_rebind_frame) {
                    DWORD owner = 0;
                    GetWindowThreadProcessId(window, &owner);
                    if (lifetime.closed->load() || !IsWindow(window) || owner != process_id || !IsWindowVisible(window)) {
                        throw hresult_error(E_HANDLE, L"字幕窗口已关闭，等待重新连接。");
                    }
                    if (IsIconic(window)) {
                        if (overlay && !hidden) overlay->hide();
                        hidden = true;
                        continue;
                    }
                }
                if (!saved && !first_frame_nudged && !hold_old_frame && IsWindow(window)
                    && Clock::now() - bind_time >= std::chrono::milliseconds(200)) {
                    first_frame_nudged = true;
                    RedrawWindow(window, nullptr, nullptr, RDW_INVALIDATE | RDW_FRAME | RDW_ALLCHILDREN);
                }
                current = configuration();
                if (current.border != applied_border && lifetime.session) {
                    stage = L"更新窗口捕获边框";
                    applied_border = current.border;
                    lifetime.session.IsBorderRequired(applied_border != 0);
                }
                stage = L"更新 VR 字幕显示参数";
                if (overlay && overlay->configure(current)) {
                    const auto& curve = overlay->curvature();
                    std::lock_guard lock(mutex);
                    stats.curvature_fallback = curve.fallback() ? 1u : 0;
                    stats.curvature_degrees = curve.degrees();
                    error = curve.fallback() ? L"曲面设置未成功，已恢复平面字幕。"
                        + to_hstring(vr::VROverlay()->GetOverlayErrorNameFromEnum(static_cast<vr::EVROverlayError>(curve.error()))) : L"";
                }
                auto now = Clock::now();
                stage = L"读取窗口捕获帧";
                Direct3D11CaptureFrame frame{nullptr};
                const bool copy_due = !saved || awaiting_rebind_frame || (now - last_copy >= std::chrono::milliseconds(50));
                if (!hold_old_frame && lifetime.pool && copy_due) {
                    lifetime.frame_arrived->store(false);
                    frame = lifetime.pool.TryGetNextFrame();
                }
                platform::ScopeExit release_frame([&frame] { if (frame) frame.Close(); });
                if (frame) {
                    while (auto newer = lifetime.pool.TryGetNextFrame()) {
                        frame.Close();
                        frame = std::move(newer);
                    }
                }
                if (frame) {
                    auto incoming_size = frame.ContentSize();
                    if (incoming_size.Width <= 0 || incoming_size.Height <= 0) {
                        frame.Close();
                        frame = nullptr;
                        continue;
                    }
                    if (static_cast<uint64_t>(incoming_size.Width) * incoming_size.Height > 16 * 1024 * 1024) {
                        throw hresult_error(E_OUTOFMEMORY, L"捕获窗口过大，请缩小字幕窗口。");
                    }
                    stage = L"读取捕获图像纹理";
                    auto access = frame.Surface().as<::Windows::Graphics::DirectX::Direct3D11::IDirect3DDxgiInterfaceAccess>();
                    com_ptr<ID3D11Texture2D> source;
                    check_hresult(access->GetInterface(__uuidof(ID3D11Texture2D), source.put_void()));
                    D3D11_TEXTURE2D_DESC desc{}, saved_desc{};
                    source->GetDesc(&desc);
                    if (static_cast<uint64_t>(desc.Width) * desc.Height > 16 * 1024 * 1024) {
                        throw hresult_error(E_OUTOFMEMORY, L"捕获窗口过大，请缩小字幕窗口。");
                    }
                    if (saved) saved->GetDesc(&saved_desc);
                    surface_width = std::min(static_cast<uint32_t>(incoming_size.Width), desc.Width);
                    surface_height = std::min(static_cast<uint32_t>(incoming_size.Height), desc.Height);
                    if (!saved || desc.Width != saved_desc.Width || desc.Height != saved_desc.Height) {
                        desc.Usage = D3D11_USAGE_DEFAULT;
                        desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
                        desc.CPUAccessFlags = 0;
                        desc.MiscFlags = 0;
                        stage = L"创建捕获图像副本纹理";
                        com_ptr<ID3D11Texture2D> fresh;
                        check_hresult(graphics.device->CreateTexture2D(&desc, nullptr, fresh.put()));
                        graphics.reset_source();
                        saved = std::move(fresh);
                    }
                    stage = L"复制捕获图像";
                    graphics.context->CopyResource(saved.get(), source.get());
                    last_copy = now;
                    frame.Close();
                    frame = nullptr;
                    if (incoming_size != size) {
                        stage = L"按窗口尺寸重建捕获帧池";
                        size = incoming_size;
                        lifetime.pool.Recreate(capture_device, DirectXPixelFormat::B8G8R8A8UIntNormalized, 2, size);
                    }
                    if (awaiting_rebind_frame) {
                        awaiting_rebind_frame = false;
                        is_restarting.store(false);
                        last_frame = now - std::chrono::milliseconds(50);
                    }
                    pending = true;
                    ++serial;
                }
                if (!current.preview) rendered.preview = 0;
                if (!current.vr && !current.preview) continue;
                bool crop_changed = !assistant_request && (current.crop_top != rendered.crop_top || current.trim != rendered.trim);
                bool preview_changed = current.preview && !rendered.preview;
                bool preview_due = current.preview && (preview_serial != serial || preview_changed)
                    && now - last_preview >= std::chrono::milliseconds(100);
                if (!saved || (!(pending && now - last_frame >= std::chrono::milliseconds(50))
                    && !crop_changed && !preview_due && !hidden)) continue;
                auto processing_start = Clock::now();
                stage = L"裁剪和绘制字幕图像";
                BridgeOptions render_options = current;
                if (assistant_request) {
                    render_options.crop_top = 0;
                    render_options.trim = 0;
                }
                auto texture = graphics.render(saved, surface_width, surface_height, render_options);
                if (!texture) {
                    pending = false;
                    last_frame = now;
                    continue;
                }
                stage = L"提交 SteamVR 字幕纹理";
                if (overlay) overlay->submit(texture, graphics.context);
                bool update_preview = current.preview && (preview_changed || now - last_preview >= std::chrono::milliseconds(100));
                if (update_preview) {
                    stage = L"生成桌面字幕预览";
                    graphics.preview(texture, updated);
                    last_preview = now;
                    preview_serial = serial;
                }
                rendered = current;
                pending = false;
                last_frame = now;
                hidden = false;
                D3D11_TEXTURE2D_DESC output_desc{};
                texture->GetDesc(&output_desc);
                if (stop_token.stop_requested()) break;
                {
                    std::lock_guard lock(mutex);
                    stats.state = 2;
                    stats.frames = serial;
                    if (overlay) ++stats.submitted;
                    stats.width = output_desc.Width;
                    stats.height = output_desc.Height;
                    stats.processing_ms = std::chrono::duration<double, std::milli>(Clock::now() - processing_start).count();
                    if (update_preview) {
                        std::swap(preview, updated);
                        ++preview_sequence;
                    }
                }
            }
        }
    }
    std::shared_ptr<Signal> signal;
    MicrophoneKeeper microphone;
    std::jthread worker;
    std::mutex control;
    bool disposed = false;
    std::atomic<bool> stopping{false};
    std::atomic<bool> reset_assistant_requested{false};
    std::atomic<bool> is_restarting{false};
    std::atomic<uintptr_t> pending_rebind_window{0};
    BridgeOptions options{};
};

class Registry {
public:
    ~Registry() { for (const auto& entry : instances) entry.second->dispose(); }
    BridgeId create() {
        auto instance = std::make_shared<Bridge>();
        std::lock_guard lock(mutex);
        if (next == static_cast<uint64_t>(std::numeric_limits<intptr_t>::max())) return 0;
        auto identifier = next++;
        instances.emplace(identifier, std::move(instance));
        return identifier;
    }
    std::shared_ptr<Bridge> acquire(BridgeId identifier) {
        std::lock_guard lock(mutex);
        auto found = instances.find(identifier);
        return found == instances.end() ? nullptr : found->second;
    }
    std::shared_ptr<Bridge> remove(BridgeId identifier) {
        std::lock_guard lock(mutex);
        auto node = instances.extract(identifier);
        return node.empty() ? nullptr : std::move(node.mapped());
    }
private:
    BridgeId next = 1;
    std::mutex mutex;
    std::unordered_map<BridgeId, std::shared_ptr<Bridge>> instances;
};

Registry& registry() {
    static Registry instances;
    return instances;
}
}

uint32_t __cdecl bridge_version() noexcept { return 10; }

int32_t __cdecl bridge_assistant_start(BridgeId bridge, const BridgeAssistantRequest& request) noexcept {
    auto instance = registry().acquire(bridge);
    if (!instance) return E_HANDLE;
    try {
        if (request.size != sizeof(request) || request.border > 1 || request.activate_on_click > 1
            || !std::isfinite(request.width) || request.width < .3f || request.width > 1.5f
            || !std::isfinite(request.distance) || request.distance < .4f || request.distance > 3.f
            || request.manifest.front() == 0 || request.manifest.back() != 0) return E_INVALIDARG;
        platform::assistant_validate({sizeof(BridgeAssistantTarget), request.process_id, request.window, request.process_started});
        const std::filesystem::path manifest(request.manifest.data());
        if (!manifest.is_absolute() || !std::filesystem::is_regular_file(manifest)) throw hresult_invalid_argument(L"找不到助手手柄动作清单。");
        BridgeOptions options{sizeof(BridgeOptions), request.width, request.distance, 0, 0, 1, 0, 0, 0, 0, request.border, 1, 1, 0};
        return instance->start(reinterpret_cast<HWND>(request.window), options, request);
    } catch (const hresult_error& exception) {
        instance->fail(std::wstring(exception.message()));
        return exception.code();
    } catch (...) { instance->fail(L"助手连接失败。"); return E_FAIL; }
}

int32_t __cdecl bridge_assistant_status(BridgeId bridge, BridgeAssistantStatus& output) noexcept {
    try {
        if (output.size != sizeof(output)) return E_INVALIDARG;
        auto instance = registry().acquire(bridge);
        if (!instance) return E_HANDLE;
        const auto& mailbox = instance->assistant_mailbox;
        std::lock_guard lock(mailbox->mutex);
        output.state = mailbox->state;
        output.sequence = mailbox->sequence;
        output.message.fill(0);
        std::copy_n(mailbox->message.begin(), std::min(mailbox->message.size(), output.message.size() - 1), output.message.begin());
        return S_OK;
    } catch (...) { return E_FAIL; }
}

int32_t __cdecl bridge_assistant_image(BridgeId bridge, uint64_t sequence, BridgeAssistantImage& output) noexcept {
    try {
        if (output.size != sizeof(output)) return E_INVALIDARG;
        auto instance = registry().acquire(bridge);
        if (!instance) return E_HANDLE;
        const auto& mailbox = instance->assistant_mailbox;
        std::lock_guard lock(mailbox->mutex);
        if (mailbox->sequence != sequence || mailbox->pixels.empty()) return S_FALSE;
        if (mailbox->pixels.size() > output.pixels.size()) return E_BOUNDS;
        output.width = mailbox->width;
        output.height = mailbox->height;
        output.sequence = mailbox->sequence;
        std::copy(mailbox->pixels.begin(), mailbox->pixels.end(), output.pixels.begin());
        std::vector<uint8_t>().swap(mailbox->pixels);
        return S_OK;
    } catch (...) { return E_FAIL; }
}

int32_t __cdecl bridge_assistant_reset_position(BridgeId bridge) noexcept {
    try {
        auto instance = registry().acquire(bridge);
        if (!instance) return E_HANDLE;
        instance->request_reset_assistant_position();
        return S_OK;
    } catch (...) { return E_FAIL; }
}

BridgeId __cdecl bridge_create() noexcept {
    try { return registry().create(); } catch (...) { return 0; }
}
void __cdecl bridge_destroy(BridgeId bridge) noexcept {
    try { if (auto instance = registry().remove(bridge)) instance->dispose(); } catch (...) { }
}
int32_t __cdecl bridge_start(BridgeId bridge, uintptr_t hwnd, const BridgeOptions& options) noexcept {
    try {
        auto instance = registry().acquire(bridge);
        return instance ? instance->start(reinterpret_cast<HWND>(hwnd), options) : E_HANDLE;
    } catch (...) { return E_FAIL; }
}
int32_t __cdecl bridge_configure(BridgeId bridge, const BridgeOptions& options) noexcept {
    try {
        auto instance = registry().acquire(bridge);
        return instance ? instance->configure(options) : E_HANDLE;
    } catch (...) { return E_FAIL; }
}
int32_t __cdecl bridge_set_restarting(BridgeId bridge, uint32_t restarting) noexcept {
    try {
        auto instance = registry().acquire(bridge);
        return instance ? instance->set_restarting(restarting) : E_HANDLE;
    } catch (...) { return E_FAIL; }
}
int32_t __cdecl bridge_rebind_window(BridgeId bridge, uintptr_t hwnd) noexcept {
    try {
        auto instance = registry().acquire(bridge);
        return instance ? instance->rebind_window(reinterpret_cast<HWND>(hwnd)) : E_HANDLE;
    } catch (...) { return E_FAIL; }
}
int32_t __cdecl bridge_microphone_configure(BridgeId bridge, uint32_t enabled) noexcept {
    try {
        auto instance = registry().acquire(bridge);
        return instance ? instance->configure_microphone(enabled) : E_HANDLE;
    } catch (...) { return E_FAIL; }
}
int32_t __cdecl bridge_microphone_stats(BridgeId bridge, BridgeMicrophoneStats& output) noexcept {
    try {
        if (output.size != sizeof(BridgeMicrophoneStats)) return E_INVALIDARG;
        auto instance = registry().acquire(bridge);
        return instance ? instance->microphone_stats(output) : E_HANDLE;
    } catch (...) { return E_FAIL; }
}
void __cdecl bridge_stop(BridgeId bridge) noexcept {
    try { if (auto instance = registry().acquire(bridge)) instance->stop(); } catch (...) { }
}
int32_t __cdecl bridge_stats(BridgeId bridge, BridgeStats& stats) noexcept {
    try {
        if (stats.size != sizeof(BridgeStats)) return E_INVALIDARG;
        auto instance = registry().acquire(bridge);
        if (!instance) return E_HANDLE;
        std::lock_guard lock(instance->mutex);
        stats = instance->stats;
        return S_OK;
    } catch (...) { return E_FAIL; }
}
int32_t __cdecl bridge_error(BridgeId bridge, BridgeError& output) noexcept {
    try {
        if (output.size != sizeof(BridgeError)) return E_INVALIDARG;
        auto instance = registry().acquire(bridge);
        if (!instance) return E_HANDLE;
        std::lock_guard lock(instance->mutex);
        output.text.fill(0);
        std::copy_n(instance->error.begin(), std::min(instance->error.size(), output.text.size() - 1), output.text.begin());
        return S_OK;
    } catch (...) { return E_FAIL; }
}
int32_t __cdecl bridge_preview(BridgeId bridge, uint64_t after_sequence, BridgePreview& output) noexcept {
    try {
        if (output.size != sizeof(BridgePreview)) return E_INVALIDARG;
        auto instance = registry().acquire(bridge);
        if (!instance) return E_HANDLE;
        std::lock_guard lock(instance->mutex);
        output.sequence = instance->preview_sequence;
        if (instance->preview_sequence == after_sequence || instance->preview.pixels.empty()) {
            output.width = output.height = 0;
            return S_FALSE;
        }
        if (instance->preview.pixels.size() > output.pixels.size()) return E_BOUNDS;
        output.width = instance->preview.width;
        output.height = instance->preview.height;
        std::copy(instance->preview.pixels.begin(), instance->preview.pixels.end(), output.pixels.begin());
        return S_OK;
    } catch (...) { return E_FAIL; }
}
