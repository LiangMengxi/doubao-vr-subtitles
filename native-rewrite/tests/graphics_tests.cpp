#include "graphics.h"
#include "platform.h"
#include "overlay_transform.h"
#include <iostream>
#include <stdexcept>
#include <string_view>
#include <thread>

void require(bool condition, std::string_view message) {
    if (!condition) throw std::runtime_error(std::string(message));
}

int main() {
    try {
        static_assert(sizeof(BridgeOptions) == 52);
        BridgeOptions placement{};
        placement.horizontal = .2f;
        placement.vertical = -.3f;
        placement.distance = 1.2f;
        auto transform = overlay_transform(placement);
        require(transform[0][0] == 1 && transform[1][1] == 1 && transform[2][2] == 1,
            "Zero tilt must preserve the original orientation.");
        placement.pitch = 30;
        transform = overlay_transform(placement);
        require(std::abs(transform[1][2] + .5f) < .00001f && transform[0][0] == 1,
            "Pitch-only rotation must preserve the previous behavior.");
        placement.pitch = 0;
        placement.yaw = 30;
        transform = overlay_transform(placement);
        require(std::abs(transform[0][2] - .5f) < .00001f && std::abs(transform[2][0] + .5f) < .00001f && transform[1][1] == 1,
            "Yaw must rotate around the vertical axis independently.");
        for (float pitch : {-45.f, 0.f, 30.f, 45.f}) {
            for (float yaw : {-45.f, 0.f, 30.f, 45.f}) {
                placement.pitch = pitch;
                placement.yaw = yaw;
                transform = overlay_transform(placement);
                require(transform[0][3] == .2f && transform[1][3] == -.3f && transform[2][3] == -1.2f,
                    "Tilt must not move the overlay center.");
                for (size_t row = 0; row < 3; ++row) {
                    for (size_t other = 0; other < 3; ++other) {
                        float product = 0;
                        for (size_t column = 0; column < 3; ++column) product += transform[row][column] * transform[other][column];
                        require(std::abs(product - (row == other ? 1.f : 0.f)) < .00001f,
                            "Combined tilt must remain orthonormal without stretching or skew.");
                    }
                }
                const float determinant = transform[0][0] * (transform[1][1] * transform[2][2] - transform[1][2] * transform[2][1])
                    - transform[0][1] * (transform[1][0] * transform[2][2] - transform[1][2] * transform[2][0])
                    + transform[0][2] * (transform[1][0] * transform[2][1] - transform[1][1] * transform[2][0]);
                require(std::abs(determinant - 1.f) < .00001f, "Tilt must not mirror the subtitle.");
            }
        }
        std::cout << "PASS: independent pitch/yaw, combined rotation, center preservation and no skew/reflection.\n";
        platform::ApartmentScope apartment;
        Graphics graphics(0);
        std::vector<uint32_t> pixels(16 * 16, 0);
        for (uint32_t row = 4; row < 10; ++row) {
            for (uint32_t column = 3; column < 7; ++column) pixels[row * 16 + column] = 0xffff0000;
        }
        auto source = platform::bgra_texture(graphics.device, 16, 16, pixels);
        BridgeOptions options{};
        options.trim = 1;
        options.crop_top = 2;
        auto rendered = graphics.render(source, 16, 16, options);
        D3D11_TEXTURE2D_DESC desc{};
        rendered->GetDesc(&desc);
        require(desc.Width == 4 && desc.Height == 4, "Alpha trim must precede toolbar cropping.");
        auto preview = graphics.preview(rendered);
        require(preview.width == 4 && preview.height == 4, "Small previews must preserve size.");
        require(preview.pixels[0] == 0 && preview.pixels[1] == 0 && preview.pixels[2] == 255 && preview.pixels[3] == 255,
            "BGRA capture must retain red through RGBA rendering and BGRA preview readback.");
        options.crop_top = 120;
        rendered = graphics.render(source, 16, 16, options);
        rendered->GetDesc(&desc);
        require(desc.Width == 4 && desc.Height == 1, "Excessive crop must retain one row.");
        pixels.assign(16 * 16, 0);
        source = platform::bgra_texture(graphics.device, 16, 16, pixels);
        options.crop_top = 0;
        rendered = graphics.render(source, 16, 16, options);
        rendered->GetDesc(&desc);
        require(desc.Width == 16 && desc.Height == 16, "Fully transparent frames must retain dimensions.");
        pixels.assign(800 * 400, 0xff0000ff);
        source = platform::bgra_texture(graphics.device, 800, 400, pixels);
        options.trim = 0;
        rendered = graphics.render(source, 800, 400, options);
        preview = graphics.preview(rendered);
        require(preview.width == 480 && preview.height == 240, "Preview readback must obey both size limits.");
        require(preview.pixels[0] == 255 && preview.pixels[2] == 0, "Blue channels must remain blue after resize.");
        bool budget_rejected = false;
        try { graphics.render(source, 8192, 8192, options); }
        catch (const std::invalid_argument&) { budget_rejected = true; }
        require(budget_rejected, "Oversized GPU pools must be rejected before allocation.");
        bool deferred = false;
        for (uint32_t crop = 1; crop <= 100; ++crop) {
            options.crop_top = crop;
            auto candidate = graphics.render(source, 800, 400, options);
            if (!candidate) { deferred = true; break; }
        }
        require(deferred, "Rapidly changing dimensions must obey the total retirement budget.");
        std::this_thread::sleep_for(std::chrono::milliseconds(2100));
        require(static_cast<bool>(graphics.render(source, 800, 400, options)), "Rendering must resume after retired textures expire.");
        std::cout << "PASS: GPU trim, crop limits, alpha, channel order, resize and bounded preview.\n";
        std::cout << "PASS: GPU budget rejection, deferred resize and recovery after retirement.\n";
        return 0;
    } catch (const winrt::hresult_error& exception) {
        std::wcerr << exception.message().c_str() << L"\n";
    } catch (const std::exception& exception) {
        std::cerr << exception.what() << "\n";
    }
    return 1;
}
