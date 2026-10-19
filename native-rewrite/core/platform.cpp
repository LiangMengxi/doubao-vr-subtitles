#include "platform.h"
#include <algorithm>
#include <cstring>
#include <stdexcept>
#include <openvr.h>

namespace platform {
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
