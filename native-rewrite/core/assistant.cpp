#include "assistant.h"
#include "graphics.h"
#include "platform.h"
#include "spatial_image.h"
#include "assistant_pose.h"
#include <openvr.h>
#include <filesystem>
#include <functional>
#include <chrono>

using namespace std::chrono_literals;

namespace {
void check_overlay(vr::EVROverlayError result) {
    if (result != vr::VROverlayError_None) throw winrt::hresult_error(E_FAIL,
        winrt::to_hstring(vr::VROverlay()->GetOverlayErrorNameFromEnum(result)));
}
spatial::Vector column(const vr::HmdMatrix34_t& matrix, size_t index) {
    return {matrix.m[0][index], matrix.m[1][index], matrix.m[2][index]};
}
vr::HmdMatrix34_t transform(const spatial::Plane& plane) {
    return {{{plane.right.horizontal, plane.up.horizontal, plane.back.horizontal, plane.center.horizontal},
        {plane.right.vertical, plane.up.vertical, plane.back.vertical, plane.center.vertical},
        {plane.right.depth, plane.up.depth, plane.back.depth, plane.center.depth}}};
}
vr::HmdMatrix34_t multiply(const vr::HmdMatrix34_t& left, const vr::HmdMatrix34_t& right) {
    vr::HmdMatrix34_t result{};
    for (size_t row = 0; row < 3; ++row) for (size_t column_index = 0; column_index < 4; ++column_index) {
        result.m[row][column_index] = column_index == 3 ? left.m[row][3] : 0;
        for (size_t axis = 0; axis < 3; ++axis) result.m[row][column_index] += left.m[row][axis] * right.m[axis][column_index];
    }
    return result;
}
vr::HmdMatrix34_t inverse(const vr::HmdMatrix34_t& source) {
    vr::HmdMatrix34_t result{};
    for (size_t row = 0; row < 3; ++row) {
        for (size_t axis = 0; axis < 3; ++axis) result.m[row][axis] = source.m[axis][row];
        result.m[row][3] = -column(source, row).dot(column(source, 3));
    }
    return result;
}
spatial::Plane panel(const vr::HmdMatrix34_t& pose, float width, float height) {
    return {column(pose, 3), column(pose, 0), column(pose, 1), column(pose, 2), width, height};
}
spatial::Plane standard_placement(const vr::HmdMatrix34_t& head_pose, float width, float distance, bool right_side = true) {
    const auto head_pos = column(head_pose, 3);
    const auto head_right = column(head_pose, 0);
    const auto head_up = column(head_pose, 1);
    const auto head_back = column(head_pose, 2);
    const auto forward = head_back * -1.f;
    if (!right_side) {
        auto p = panel(head_pose, width, width);
        p.center = p.center - p.back * distance;
        return p;
    }
    constexpr float angle = 0.55f;
    const auto dir = (forward * std::cos(angle) + head_right * std::sin(angle)).normalized(forward);
    const auto center = head_pos + dir * distance;
    const auto plane_back = (head_pos - center).normalized(head_back);
    const auto plane_right = head_up.cross(plane_back).normalized(head_right);
    const auto plane_up = plane_back.cross(plane_right).normalized(head_up);
    return {center, plane_right, plane_up, plane_back, width, width};
}

class OwnedOverlay {
public:
    explicit OwnedOverlay(std::string_view key) {
        check_overlay(vr::VROverlay()->CreateOverlay(key.data(), "Doubao VR", &handle));
    }
    ~OwnedOverlay() {
        vr::VROverlay()->HideOverlay(handle);
        vr::VROverlay()->ClearOverlayTexture(handle);
        vr::VROverlay()->DestroyOverlay(handle);
    }
    OwnedOverlay(const OwnedOverlay&) = delete;
    OwnedOverlay& operator=(const OwnedOverlay&) = delete;
    uint64_t handle = vr::k_ulOverlayHandleInvalid;
};

class Actions {
public:
    explicit Actions(std::wstring_view manifest) {
        const std::filesystem::path path(manifest);
        if (!path.is_absolute() || !std::filesystem::is_regular_file(path)) throw winrt::hresult_invalid_argument(L"找不到手柄动作清单。");
        if (vr::VRInput()) {
            if (vr::VRInput()->SetActionManifestPath(winrt::to_string(manifest).c_str()) == vr::VRInputError_None) {
                if (vr::VRInput()->GetActionSetHandle("/actions/assistant", &set.ulActionSet) == vr::VRInputError_None) {
                    constexpr std::array names{"capture", "left_trigger", "right_trigger", "left_grip", "right_grip", "scroll"};
                    bool all_handles_ok = true;
                    for (size_t index = 0; index < names.size(); ++index) {
                        const auto name = std::string("/actions/assistant/in/") + names[index];
                        if (vr::VRInput()->GetActionHandle(name.c_str(), &handles[index]) != vr::VRInputError_None) {
                            all_handles_ok = false;
                        }
                    }
                    input_available = all_handles_ok;
                }
            }
        }
    }
    void update() {
        if (input_available && vr::VRInput()) {
            vr::VRInput()->UpdateActionState(&set, sizeof(set), 1);
        }
        fallback_pressed.fill(false);
        fallback_scroll = 0.f;
        if (vr::VRSystem()) {
            const auto left_index = vr::VRSystem()->GetTrackedDeviceIndexForControllerRole(vr::TrackedControllerRole_LeftHand);
            vr::VRControllerState_t left_state{};
            if (left_index != vr::k_unTrackedDeviceIndexInvalid && vr::VRSystem()->GetControllerState(left_index, &left_state, sizeof(left_state))) {
                fallback_pressed[0] = (left_state.ulButtonPressed & (vr::ButtonMaskFromId(vr::k_EButton_SteamVR_Touchpad) | vr::ButtonMaskFromId(vr::k_EButton_IndexController_JoyStick))) != 0;
                fallback_pressed[1] = ((left_state.ulButtonPressed & vr::ButtonMaskFromId(vr::k_EButton_SteamVR_Trigger)) != 0) || (left_state.rAxis[1].x > 0.55f);
                fallback_pressed[3] = ((left_state.ulButtonPressed & (vr::ButtonMaskFromId(vr::k_EButton_Grip) | vr::ButtonMaskFromId(vr::k_EButton_Axis2))) != 0) || (left_state.rAxis[2].x > 0.55f);
            }
            const auto right_index = vr::VRSystem()->GetTrackedDeviceIndexForControllerRole(vr::TrackedControllerRole_RightHand);
            vr::VRControllerState_t right_state{};
            if (right_index != vr::k_unTrackedDeviceIndexInvalid && vr::VRSystem()->GetControllerState(right_index, &right_state, sizeof(right_state))) {
                fallback_pressed[2] = ((right_state.ulButtonPressed & vr::ButtonMaskFromId(vr::k_EButton_SteamVR_Trigger)) != 0) || (right_state.rAxis[1].x > 0.55f);
                fallback_pressed[4] = ((right_state.ulButtonPressed & (vr::ButtonMaskFromId(vr::k_EButton_Grip) | vr::ButtonMaskFromId(vr::k_EButton_Axis2))) != 0) || (right_state.rAxis[2].x > 0.55f);
                for (const float y : {right_state.rAxis[0].y, right_state.rAxis[3].y}) {
                    if (std::abs(y) > 0.1f && std::abs(y) > std::abs(fallback_scroll)) fallback_scroll = y;
                }
            }
        }
    }
    bool pressed(size_t index) const {
        if (input_available && vr::VRInput()) {
            vr::InputDigitalActionData_t value{};
            if (vr::VRInput()->GetDigitalActionData(handles[index], &value, sizeof(value), vr::k_ulInvalidInputValueHandle) == vr::VRInputError_None) {
                if (value.bActive && value.bState) return true;
            }
        }
        return index < fallback_pressed.size() ? fallback_pressed[index] : false;
    }
    float scroll() const {
        if (input_available && vr::VRInput()) {
            vr::InputAnalogActionData_t value{};
            if (vr::VRInput()->GetAnalogActionData(handles[5], &value, sizeof(value), vr::k_ulInvalidInputValueHandle) == vr::VRInputError_None) {
                if (value.bActive && std::abs(value.y) > 0.01f) return value.y;
            }
        }
        return fallback_scroll;
    }
private:
    bool input_available = false;
    vr::VRActiveActionSet_t set{};
    std::array<vr::VRActionHandle_t, 6> handles{};
    std::array<bool, 5> fallback_pressed{};
    float fallback_scroll = 0.f;
};

class ControllerAim {
public:
    explicit ControllerAim(std::string_view path) {
        if (vr::VRInput()) {
            vr::VRInput()->GetInputSourceHandle(path.data(), &source);
        }
    }
    std::optional<vr::HmdMatrix34_t> pose(vr::TrackedDeviceIndex_t index, const vr::HmdMatrix34_t& device) {
        const auto now = std::chrono::steady_clock::now();
        if (tracked_index != index || now >= refresh_at) {
            tracked_index = index;
            refresh_at = now + 1s;
            model.fill(0);
            vr::ETrackedPropertyError error{};
            const auto length = vr::VRSystem()->GetStringTrackedDeviceProperty(index, vr::Prop_RenderModelName_String,
                model.data(), static_cast<uint32_t>(model.size()), &error);
            if (error != vr::TrackedProp_Success || length == 0 || length > model.size() || model[length - 1] != 0) model.fill(0);
        }
        if (model[0] == 0) return {};
        constexpr std::array<std::string_view, 2> names{"tip", "openxr_aim"};
        vr::RenderModel_ControllerMode_State_t mode{};
        vr::VRControllerState_t controller_state{};
        bool has_controller_state = false;
        for (const auto name : names) {
            vr::RenderModel_ComponentState_t component{};
            bool got_component = false;
            if (source != vr::k_ulInvalidInputValueHandle && vr::VRRenderModels()) {
                got_component = vr::VRRenderModels()->GetComponentStateForDevicePath(model.data(), name.data(), source, &mode, &component);
            }
            if (!got_component && vr::VRRenderModels() && vr::VRSystem()) {
                if (!has_controller_state) {
                    has_controller_state = vr::VRSystem()->GetControllerState(index, &controller_state, sizeof(controller_state));
                }
                if (has_controller_state) {
                    got_component = vr::VRRenderModels()->GetComponentState(model.data(), name.data(), &controller_state, &mode, &component);
                }
            }
            if (got_component) {
                if (const auto result = assistant::aim_pose(device, component.mTrackingToComponentLocal)) return result;
            }
        }
        return {};
    }
private:
    vr::VRInputValueHandle_t source = vr::k_ulInvalidInputValueHandle;
    vr::TrackedDeviceIndex_t tracked_index = vr::k_unTrackedDeviceIndexInvalid;
    std::array<char, 1024> model{};
    std::chrono::steady_clock::time_point refresh_at{};
};

template<typename Action> int32_t target_call(BridgeError& error, Action action) noexcept {
    if (error.size != sizeof(error)) return E_INVALIDARG;
    error.text.fill(0);
    try { action(); return S_OK; }
    catch (...) {
        const auto result = winrt::to_hresult();
        try {
            const auto message = std::wstring(winrt::to_message());
            std::copy_n(message.begin(), std::min(message.size(), error.text.size() - 1), error.text.begin());
        } catch (...) { }
        return result;
    }
}
}

int32_t __cdecl bridge_assistant_target(BridgeAssistantTarget& target, BridgeError& error) noexcept {
    return target_call(error, [&] {
        if (target.size != sizeof(target)) throw winrt::hresult_invalid_argument();
        target = platform::assistant_target(static_cast<uintptr_t>(target.window), target.process_id);
    });
}
int32_t __cdecl bridge_assistant_paste(const BridgeAssistantTarget& target, BridgeError& error) noexcept {
    return target_call(error, [&] { platform::assistant_paste(target); });
}

AssistantInteraction::AssistantInteraction(uint64_t overlay_value, BridgeAssistantRequest requested, std::shared_ptr<AssistantMailbox> messages)
    : overlay(overlay_value), request(std::move(requested)), mailbox(std::move(messages)), panel_width(request.width) {
    worker = std::jthread([this](std::stop_token stop) { run(stop); });
}
AssistantInteraction::~AssistantInteraction() { worker.request_stop(); if (worker.joinable()) worker.join(); }

void AssistantInteraction::run(std::stop_token stop) noexcept {
    try {
        platform::ApartmentScope apartment;
        mailbox->update(1, L"正在加载手柄映射…");
        Actions actions(request.manifest.data());
        std::array<ControllerAim, 2> pointing{ControllerAim("/user/hand/left"), ControllerAim("/user/hand/right")};
        mailbox->update(1, L"正在初始化助手交互…");
        const BridgeAssistantTarget target{sizeof(BridgeAssistantTarget), request.process_id, request.window, request.process_started};
        platform::AssistantMouse mouse(target, request.activate_on_click != 0);
        int adapter = 0;
        vr::VRSystem()->GetDXGIOutputInfo(&adapter);
        Graphics graphics(adapter);
        OwnedOverlay selection_overlay(std::string_view("local.doubao.assistant.selection"));
        OwnedOverlay pointer_overlay(std::string_view("local.doubao.assistant.pointer"));
        std::vector<uint32_t> outline(512 * 512, 0);
        for (size_t row = 0; row < 512; ++row) for (size_t column_index = 0; column_index < 512; ++column_index) {
            if (row < 2 || row >= 510 || column_index < 2 || column_index >= 510) outline[row * 512 + column_index] = 0xf2ffb6cb;
            else if (row == 2 || row == 509 || column_index == 2 || column_index == 509) outline[row * 512 + column_index] = 0x6effb6cb;
        }
        auto outline_texture = platform::bgra_texture(graphics.device, 512, 512, outline);
        auto enter_prompt_texture = platform::prompt_texture(graphics.device, L"已进入截图模式 · 按住双扳机框选");
        auto exit_prompt_texture = platform::prompt_texture(graphics.device, L"已退出截图模式");
        std::vector<uint32_t> pointer_pixels(64 * 64, 0);
        for (size_t row = 0; row < 64; ++row) {
            for (size_t column_index = 0; column_index < 64; ++column_index) {
                const float horizontal = static_cast<float>(column_index) - 31.5f;
                const float vertical = static_cast<float>(row) - 31.5f;
                const float distance = std::sqrt(horizontal * horizontal + vertical * vertical);
                const float coverage = std::clamp(28.5f - distance, 0.f, 1.f);
                if (coverage > 0.f) {
                    const float highlight = std::clamp(1.f - distance / 20.f, 0.f, 1.f) * .45f;
                    const auto blue = static_cast<uint32_t>(std::lround(255.f * coverage));
                    const auto green = static_cast<uint32_t>(std::lround((200.f + 55.f * highlight) * coverage));
                    const auto red = static_cast<uint32_t>(std::lround((255.f * highlight) * coverage));
                    const auto alpha = static_cast<uint32_t>(std::lround(255.f * coverage));
                    pointer_pixels[row * 64 + column_index] = (alpha << 24) | (red << 16) | (green << 8) | blue;
                }
            }
        }
        auto pointer_texture = platform::bgra_texture(graphics.device, 64, 64, pointer_pixels);
        check_overlay(static_cast<vr::EVROverlayError>(platform::submit_overlay(pointer_overlay.handle, pointer_texture, graphics.context)));
        check_overlay(vr::VROverlay()->SetOverlayWidthInMeters(pointer_overlay.handle, .016f));
        vr::HmdMatrix34_t placement{};
        vr::HmdMatrix34_t grab_offset{};
        spatial::Selection selection;
        bool positioned = false, previous_toggle = true, previous_dashboard = false, input_ready = false, tracking_ready = false;
        std::array<bool, 2> previous_grips{true, true};
        int grabbed = -1;
        auto armed_at = std::chrono::steady_clock::now();
        auto exit_prompt_until = armed_at, scroll_at = armed_at, mouse_at = armed_at;
        std::atomic<bool> capture_done{true};
        std::jthread capture_worker;
        platform::ScopeExit finish_capture([&] {
            capture_worker.request_stop();
            if (capture_worker.joinable()) capture_worker.join();
        });
        mailbox->update(1, L"等待头显定位…");
        while (!stop.stop_requested()) {
            const auto frame_start = std::chrono::steady_clock::now();
            if (suppress && capture_done) {
                std::lock_guard lock(visibility);
                suppress = false;
                selection.reset();
                if (image_width > 0) check_overlay(vr::VROverlay()->ShowOverlay(overlay));
            }
            actions.update();
            const bool toggle = actions.pressed(0);
            const std::array triggers{actions.pressed(1), actions.pressed(2)};
            const std::array grips{actions.pressed(3), actions.pressed(4)};
            const bool dashboard = vr::VROverlay()->IsDashboardVisible();
            std::array<vr::TrackedDevicePose_t, vr::k_unMaxTrackedDeviceCount> poses{};
            const auto origin = vr::VRCompositor()->GetTrackingSpace();
            vr::VRSystem()->GetDeviceToAbsoluteTrackingPose(origin, 0, poses.data(), static_cast<uint32_t>(poses.size()));
            const auto& head = poses[vr::k_unTrackedDeviceIndex_Hmd];
            std::array<std::optional<vr::HmdMatrix34_t>, 2> hands;
            std::array<std::optional<vr::HmdMatrix34_t>, 2> aims;
            for (size_t hand_index = 0; hand_index < hands.size(); ++hand_index) {
                const auto index = vr::VRSystem()->GetTrackedDeviceIndexForControllerRole(hand_index == 0 ? vr::TrackedControllerRole_LeftHand : vr::TrackedControllerRole_RightHand);
                if (index < poses.size() && poses[index].bPoseIsValid && poses[index].bDeviceIsConnected) {
                    hands[hand_index] = poses[index].mDeviceToAbsoluteTracking;
                    aims[hand_index] = pointing[hand_index].pose(index, *hands[hand_index]);
                }
            }
            if (!head.bPoseIsValid || !head.bDeviceIsConnected) {
                if (tracking_ready) mailbox->update(1, L"等待头显定位…");
                tracking_ready = false;
                mouse.release(); grabbed = -1;
                input_ready = false;
                selection.reset();
                vr::VROverlay()->HideOverlay(selection_overlay.handle);
                vr::VROverlay()->HideOverlay(pointer_overlay.handle);
                previous_toggle = toggle; previous_grips = grips;
                std::this_thread::sleep_for(20ms);
                continue;
            }
            if (!positioned) {
                const float initial_width = panel_width.load();
                auto initial = panel(head.mDeviceToAbsoluteTracking, initial_width, initial_width);
                initial.center = initial.center - initial.back * request.distance;
                placement = transform(initial);
                check_overlay(vr::VROverlay()->SetOverlayTransformAbsolute(overlay, origin, &placement));
                positioned = true;
            }
            const bool auto_reset = reset_on_dashboard.load() && (dashboard && !previous_dashboard);
            if (reset_requested.exchange(false) || auto_reset) {
                const float current_width = panel_width.load();
                const auto standard = standard_placement(head.mDeviceToAbsoluteTracking, current_width, request.distance, true);
                placement = transform(standard);
                check_overlay(vr::VROverlay()->SetOverlayTransformAbsolute(overlay, origin, &placement));
                positioned = true;
                grabbed = -1;
            }
            previous_dashboard = dashboard;
            if (!tracking_ready) { mailbox->update(0, L"助手已连接"); tracking_ready = true; }
            if (dashboard) {
                mouse.release(); grabbed = -1;
                input_ready = false;
                selection.reset();
                vr::VROverlay()->HideOverlay(selection_overlay.handle);
                vr::VROverlay()->HideOverlay(pointer_overlay.handle);
                previous_toggle = toggle; previous_grips = grips;
                std::this_thread::sleep_for(20ms);
                continue;
            }
            if (!suppress && toggle && !previous_toggle) {
                mouse.release(); grabbed = -1;
                input_ready = false;
                selection.toggle(); armed_at = frame_start;
                exit_prompt_until = selection.state() == spatial::Selection::State::idle ? frame_start + 1200ms : frame_start;
                mailbox->update(selection.state() == spatial::Selection::State::idle ? 0u : 1u,
                    selection.state() == spatial::Selection::State::idle ? L"已退出截图模式" : L"已进入截图模式");
            }
            previous_toggle = toggle;
            if (selection.state() != spatial::Selection::State::idle && !suppress) {
                mouse.release();
                input_ready = false;
                vr::VROverlay()->HideOverlay(pointer_overlay.handle);
                std::optional<spatial::Plane> measured;
                if (hands[0] && hands[1]) {
                    const auto left_point = aims[0] ? column(*aims[0], 3) : column(*hands[0], 3);
                    const auto right_point = aims[1] ? column(*aims[1], 3) : column(*hands[1], 3);
                    measured = spatial::frame(column(head.mDeviceToAbsoluteTracking, 3),
                        column(head.mDeviceToAbsoluteTracking, 0), column(head.mDeviceToAbsoluteTracking, 1), column(head.mDeviceToAbsoluteTracking, 2),
                        left_point, right_point);
                }
                const auto captured = selection.update(triggers[0], triggers[1], measured);
                const auto visible_region = selection.region();
                if (selection.state() == spatial::Selection::State::armed && !captured) {
                    auto prompt_plane = panel(head.mDeviceToAbsoluteTracking, .42f, .42f * 96.f / 512.f);
                    prompt_plane.center = prompt_plane.center - prompt_plane.back * .85f;
                    const auto matrix = transform(prompt_plane);
                    check_overlay(vr::VROverlay()->SetOverlayWidthInMeters(selection_overlay.handle, prompt_plane.width));
                    check_overlay(vr::VROverlay()->SetOverlayTexelAspect(selection_overlay.handle, 1.f));
                    check_overlay(vr::VROverlay()->SetOverlayTransformAbsolute(selection_overlay.handle, origin, &matrix));
                    check_overlay(static_cast<vr::EVROverlayError>(platform::submit_overlay(selection_overlay.handle, enter_prompt_texture, graphics.context)));
                    check_overlay(vr::VROverlay()->ShowOverlay(selection_overlay.handle));
                } else if (visible_region && !captured) {
                    const auto& region = *visible_region;
                    const auto matrix = transform(region);
                    check_overlay(vr::VROverlay()->SetOverlayWidthInMeters(selection_overlay.handle, region.width));
                    check_overlay(vr::VROverlay()->SetOverlayTexelAspect(selection_overlay.handle, region.width / region.height));
                    check_overlay(vr::VROverlay()->SetOverlayTransformAbsolute(selection_overlay.handle, origin, &matrix));
                    check_overlay(static_cast<vr::EVROverlayError>(platform::submit_overlay(selection_overlay.handle, outline_texture, graphics.context)));
                    check_overlay(vr::VROverlay()->ShowOverlay(selection_overlay.handle));
                } else vr::VROverlay()->HideOverlay(selection_overlay.handle);
                if (captured) {
                    vr::VROverlay()->HideOverlay(selection_overlay.handle);
                    {
                        std::lock_guard lock(visibility);
                        suppress = true;
                        check_overlay(vr::VROverlay()->HideOverlay(overlay));
                    }
                    const auto captured_head = head.mDeviceToAbsoluteTracking;
                    const auto capture_eye = eye.load();
                    mailbox->update(3, L"正在截图…");
                    capture_done = false;
                    capture_worker = std::jthread([&, captured, captured_head, capture_eye](std::stop_token capture_stop) {
                        platform::ScopeExit complete([&] { capture_done = true; });
                        try {
                            platform::ApartmentScope capture_apartment;
                            Graphics capture_graphics(adapter);
                            const auto image = platform::mirror_pixels(capture_graphics.device, capture_graphics.context, capture_stop, captured_head, capture_eye);
                            spatial::Image result;
                            try { result = spatial::rectify(*captured, image.projection, image.pixels, image.width, image.height, capture_stop); }
                            catch (const std::invalid_argument&) { throw winrt::hresult_invalid_argument(L"选区过小或超出视野，请将选框移到视野中央后重试。"); }
                            if (capture_stop.stop_requested()) return;
                            std::lock_guard lock(mailbox->mutex);
                            mailbox->pixels = std::move(result.pixels);
                            mailbox->width = result.width; mailbox->height = result.height;
                            ++mailbox->sequence; mailbox->state = 4;
                            mailbox->message = L"截图已就绪";
                        } catch (...) {
                            if (!capture_stop.stop_requested()) mailbox->update(5, std::wstring(winrt::to_message()));
                        }
                    });
                } else if (frame_start - armed_at > 30s) {
                    selection.reset();
                    exit_prompt_until = frame_start + 1200ms;
                    mailbox->update(0, L"已退出截图模式");
                }
            } else {
                if (!suppress && frame_start < exit_prompt_until) {
                    auto prompt_plane = panel(head.mDeviceToAbsoluteTracking, .38f, .38f * 96.f / 512.f);
                    prompt_plane.center = prompt_plane.center - prompt_plane.back * .85f;
                    const auto matrix = transform(prompt_plane);
                    check_overlay(vr::VROverlay()->SetOverlayWidthInMeters(selection_overlay.handle, prompt_plane.width));
                    check_overlay(vr::VROverlay()->SetOverlayTexelAspect(selection_overlay.handle, 1.f));
                    check_overlay(vr::VROverlay()->SetOverlayTransformAbsolute(selection_overlay.handle, origin, &matrix));
                    check_overlay(static_cast<vr::EVROverlayError>(platform::submit_overlay(selection_overlay.handle, exit_prompt_texture, graphics.context)));
                    check_overlay(vr::VROverlay()->ShowOverlay(selection_overlay.handle));
                } else {
                    vr::VROverlay()->HideOverlay(selection_overlay.handle);
                }
                if (!suppress && image_width > 0 && image_height > 0) {
                if (!triggers[0] && !triggers[1]) input_ready = true;
                const float current_width = panel_width.load();
                const auto surface = panel(placement, current_width,
                    current_width * static_cast<float>(image_height.load()) / static_cast<float>(image_width.load()));
                std::array<std::optional<std::array<float, 2>>, 2> hits;
                for (size_t hand_index = 0; hand_index < aims.size(); ++hand_index)
                    if (aims[hand_index]) hits[hand_index] = assistant::intersect(surface, *aims[hand_index]);
                if (grabbed < 0) {
                    for (int hand_index = 1; hand_index >= 0; --hand_index) {
                        if (hits[hand_index] && grips[hand_index] && !previous_grips[hand_index]) {
                            grabbed = hand_index;
                            grab_offset = multiply(inverse(*hands[hand_index]), placement);
                            mouse.release();
                            break;
                        }
                    }
                }
                if (grabbed >= 0) {
                    vr::VROverlay()->HideOverlay(pointer_overlay.handle);
                    if (!hands[grabbed] || !grips[grabbed]) grabbed = -1;
                    else {
                        placement = multiply(*hands[grabbed], grab_offset);
                        check_overlay(vr::VROverlay()->SetOverlayTransformAbsolute(overlay, origin, &placement));
                    }
                } else {
                    const auto primary = hits[1] ? 1u : 0u;
                    if (hits[primary]) {
                        auto cursor = surface;
                        cursor.center = surface.point((*hits[primary])[0], (*hits[primary])[1]) + surface.back * .004f;
                        const auto matrix = transform(cursor);
                        check_overlay(vr::VROverlay()->SetOverlayTransformAbsolute(pointer_overlay.handle, origin, &matrix));
                        check_overlay(vr::VROverlay()->ShowOverlay(pointer_overlay.handle));
                    } else vr::VROverlay()->HideOverlay(pointer_overlay.handle);
                    int wheel = 0;
                    const auto scroll = actions.scroll();
                    if (input_ready && std::abs(scroll) > .25f && frame_start - scroll_at >= 100ms) { wheel = scroll > 0 ? 120 : -120; scroll_at = frame_start; }
                    if (!triggers[primary]) mouse.release();
                    if (frame_start - mouse_at >= 16ms || wheel != 0) {
                        try { mouse.update(hits[primary], input_ready && triggers[primary], wheel); }
                        catch (...) { mouse.release(); mailbox->update(5, std::wstring(winrt::to_message())); }
                        mouse_at = frame_start;
                    }
                }
                }
            }
            previous_grips = grips;
            std::this_thread::sleep_until(frame_start + 11ms);
        }
    } catch (...) {
        mailbox->update(6, std::wstring(winrt::to_message()));
    }
}
