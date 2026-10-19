#include "graphics.h"
#include "platform.h"
#include <dxgi1_2.h>
#include <d3dcompiler.h>
#include <algorithm>
#include <cstring>
#include <thread>
#include <stdexcept>

using winrt::check_hresult;
using winrt::com_ptr;

namespace {
constexpr char shader[] = R"(
Texture2D<float4> image : register(t0);
SamplerState sampling : register(s0);
cbuffer Region : register(b0) { float4 region; };
struct Vertex { float4 position : SV_Position; float2 uv : TEXCOORD0; };
Vertex vertex_main(uint index : SV_VertexID) {
    Vertex output;
    float2 uv = float2((index << 1) & 2, index & 2);
    output.position = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
    output.uv = uv;
    return output;
}
float4 pixel_main(Vertex input) : SV_Target {
    return image.SampleLevel(sampling, region.xy + input.uv * region.zw, 0);
}
RWByteAddressBuffer extent : register(u0);
[numthreads(16, 16, 1)]
void bounds_main(uint3 position : SV_DispatchThreadID) {
    uint width, height;
    image.GetDimensions(width, height);
    if (position.x >= width || position.y >= height) return;
    if (position.x >= (uint)region.x || position.y >= (uint)region.y) return;
    if (image.Load(int3(position.xy, 0)).a <= 16.0 / 255.0) return;
    uint previous;
    extent.InterlockedMin(0, position.x, previous);
    extent.InterlockedMin(4, position.y, previous);
    extent.InterlockedMax(8, position.x + 1, previous);
    extent.InterlockedMax(12, position.y + 1, previous);
}
)";

}

Graphics::Graphics(int adapter_index) {
    com_ptr<IDXGIFactory1> factory;
    check_hresult(CreateDXGIFactory1(__uuidof(IDXGIFactory1), factory.put_void()));
    com_ptr<IDXGIAdapter1> adapter;
    check_hresult(factory->EnumAdapters1(static_cast<UINT>(std::max(0, adapter_index)), adapter.put()));
    D3D_FEATURE_LEVEL level;
    const D3D_FEATURE_LEVEL requested[] = {D3D_FEATURE_LEVEL_11_0};
    check_hresult(D3D11CreateDevice(adapter.get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr,
        D3D11_CREATE_DEVICE_BGRA_SUPPORT, requested, 1, D3D11_SDK_VERSION,
        device.put(), &level, context.put()));
    auto vertex = platform::compile_shader(shader, "vertex_main", "vs_5_0");
    auto pixel = platform::compile_shader(shader, "pixel_main", "ps_5_0");
    auto compute = platform::compile_shader(shader, "bounds_main", "cs_5_0");
    check_hresult(device->CreateVertexShader(vertex->GetBufferPointer(), vertex->GetBufferSize(), nullptr, vertex_shader.put()));
    check_hresult(device->CreatePixelShader(pixel->GetBufferPointer(), pixel->GetBufferSize(), nullptr, pixel_shader.put()));
    check_hresult(device->CreateComputeShader(compute->GetBufferPointer(), compute->GetBufferSize(), nullptr, bounds_shader.put()));
    D3D11_BUFFER_DESC desc{};
    desc.ByteWidth = sizeof(Constants);
    desc.Usage = D3D11_USAGE_DEFAULT;
    desc.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
    check_hresult(device->CreateBuffer(&desc, nullptr, constants.put()));
    desc.BindFlags = D3D11_BIND_UNORDERED_ACCESS;
    desc.MiscFlags = D3D11_RESOURCE_MISC_BUFFER_ALLOW_RAW_VIEWS;
    check_hresult(device->CreateBuffer(&desc, nullptr, bounds_buffer.put()));
    desc.BindFlags = 0;
    desc.MiscFlags = 0;
    desc.Usage = D3D11_USAGE_STAGING;
    desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    check_hresult(device->CreateBuffer(&desc, nullptr, bounds_readback.put()));
    D3D11_UNORDERED_ACCESS_VIEW_DESC view{};
    view.Format = DXGI_FORMAT_R32_TYPELESS;
    view.ViewDimension = D3D11_UAV_DIMENSION_BUFFER;
    view.Buffer.NumElements = 4;
    view.Buffer.Flags = D3D11_BUFFER_UAV_FLAG_RAW;
    check_hresult(device->CreateUnorderedAccessView(bounds_buffer.get(), &view, bounds_view.put()));
    D3D11_SAMPLER_DESC sampling{};
    sampling.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR;
    sampling.AddressU = sampling.AddressV = sampling.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
    sampling.MaxLOD = D3D11_FLOAT32_MAX;
    check_hresult(device->CreateSamplerState(&sampling, sampler.put()));
    D3D11_RASTERIZER_DESC raster{};
    raster.FillMode = D3D11_FILL_SOLID;
    raster.CullMode = D3D11_CULL_NONE;
    raster.DepthClipEnable = TRUE;
    check_hresult(device->CreateRasterizerState(&raster, rasterizer.put()));
    D3D11_QUERY_DESC event_desc{D3D11_QUERY_EVENT, 0};
    check_hresult(device->CreateQuery(&event_desc, query.put()));
}

com_ptr<ID3D11Texture2D> Graphics::texture(uint32_t width, uint32_t height, bool staging) {
    D3D11_TEXTURE2D_DESC desc{};
    desc.Width = width;
    desc.Height = height;
    desc.MipLevels = desc.ArraySize = 1;
    desc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    desc.SampleDesc.Count = 1;
    desc.Usage = staging ? D3D11_USAGE_STAGING : D3D11_USAGE_DEFAULT;
    desc.BindFlags = staging ? 0 : D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
    desc.CPUAccessFlags = staging ? D3D11_CPU_ACCESS_READ : 0;
    com_ptr<ID3D11Texture2D> result;
    check_hresult(device->CreateTexture2D(&desc, nullptr, result.put()));
    return result;
}

Graphics::Constants Graphics::bounds(const com_ptr<ID3D11Texture2D>& source, uint32_t width, uint32_t height, bool trim) {
    Constants result{0, 0, static_cast<float>(width), static_cast<float>(height)};
    if (!trim) return result;
    const uint32_t initial[] = {width, height, 0, 0};
    context->UpdateSubresource(bounds_buffer.get(), 0, nullptr, initial, 0, 0);
    Constants limit{static_cast<float>(width), static_cast<float>(height), 0, 0};
    context->UpdateSubresource(constants.get(), 0, nullptr, &limit, 0, 0);
    com_ptr<ID3D11ShaderResourceView> input;
    check_hresult(device->CreateShaderResourceView(source.get(), nullptr, input.put()));
    context->CSSetShader(bounds_shader.get(), nullptr, 0);
    platform::bind_compute(context, input, bounds_view, constants);
    context->Dispatch((width + 15) / 16, (height + 15) / 16, 1);
    platform::unbind_compute(context);
    context->CopyResource(bounds_readback.get(), bounds_buffer.get());
    completed();
    platform::ReadMapping mapping(context, bounds_readback.as<ID3D11Resource>());
    auto extent = mapping.extent();
    if (extent[2] > extent[0] && extent[3] > extent[1]) {
        result = {static_cast<float>(extent[0]), static_cast<float>(extent[1]),
            static_cast<float>(extent[2] - extent[0]), static_cast<float>(extent[3] - extent[1])};
    }
    return result;
}

void Graphics::draw(const com_ptr<ID3D11Texture2D>& source, const com_ptr<ID3D11Texture2D>& destination, Constants region) {
    D3D11_TEXTURE2D_DESC input_desc{}, output_desc{};
    source->GetDesc(&input_desc);
    destination->GetDesc(&output_desc);
    region.left /= input_desc.Width;
    region.top /= input_desc.Height;
    region.width /= input_desc.Width;
    region.height /= input_desc.Height;
    context->UpdateSubresource(constants.get(), 0, nullptr, &region, 0, 0);
    com_ptr<ID3D11ShaderResourceView> input;
    com_ptr<ID3D11RenderTargetView> output;
    check_hresult(device->CreateShaderResourceView(source.get(), nullptr, input.put()));
    check_hresult(device->CreateRenderTargetView(destination.get(), nullptr, output.put()));
    D3D11_VIEWPORT viewport{0, 0, static_cast<float>(output_desc.Width), static_cast<float>(output_desc.Height), 0, 1};
    context->RSSetViewports(1, &viewport);
    context->RSSetState(rasterizer.get());
    platform::bind_pixel(context, output, input, constants, sampler);
    context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    context->VSSetShader(vertex_shader.get(), nullptr, 0);
    context->PSSetShader(pixel_shader.get(), nullptr, 0);
    context->Draw(3, 0);
    platform::unbind_pixel(context);
}

com_ptr<ID3D11Texture2D> Graphics::render(const com_ptr<ID3D11Texture2D>& source, uint32_t width, uint32_t height,
    const BridgeOptions& options) {
    if (width == 0 || height == 0) throw std::invalid_argument("Invalid capture dimensions.");
    std::erase_if(retired, [](const Retired& entry) {
        return std::chrono::steady_clock::now() - entry.time > std::chrono::seconds(2);
    });
    auto region = bounds(source, width, height, options.trim != 0);
    auto crop = std::min(static_cast<float>(options.crop_top), region.height - 1);
    region.top += crop;
    region.height -= crop;
    auto target_width = static_cast<uint32_t>(region.width);
    auto target_height = static_cast<uint32_t>(region.height);
    if (target_width != output_width || target_height != output_height) {
        constexpr uint64_t memory_budget = 128 * 1024 * 1024;
        const auto requested_bytes = static_cast<uint64_t>(target_width) * target_height * 4 * textures.size();
        if (requested_bytes > memory_budget) throw std::invalid_argument("Captured image exceeds the GPU texture budget.");
        uint64_t retained_bytes = static_cast<uint64_t>(output_width) * output_height * 4 * textures.size();
        for (const auto& entry : retired) {
            D3D11_TEXTURE2D_DESC description{};
            entry.textures[0]->GetDesc(&description);
            retained_bytes += static_cast<uint64_t>(description.Width) * description.Height * 4 * textures.size();
        }
        if (requested_bytes + retained_bytes > memory_budget) return {};
        std::array<com_ptr<ID3D11Texture2D>, 3> fresh;
        for (auto& entry : fresh) entry = texture(target_width, target_height, false);
        if (textures[0]) retired.push_back({std::move(textures), std::chrono::steady_clock::now()});
        textures = std::move(fresh);
        output_width = target_width;
        output_height = target_height;
        next = 0;
    }
    auto target = textures[next++ % textures.size()];
    draw(source, target, region);
    completed();
    return target;
}

void Graphics::completed() {
    context->End(query.get());
    context->Flush();
    auto deadline = std::chrono::steady_clock::now() + std::chrono::milliseconds(250);
    BOOL done = FALSE;
    while (true) {
        auto result = context->GetData(query.get(), &done, sizeof(done), D3D11_ASYNC_GETDATA_DONOTFLUSH);
        check_hresult(result);
        if (result == S_OK && done) return;
        if (std::chrono::steady_clock::now() >= deadline) {
            throw winrt::hresult_error(DXGI_ERROR_DEVICE_HUNG, L"GPU 响应超时，请停止后重新连接。");
        }
        std::this_thread::sleep_for(std::chrono::milliseconds(1));
    }
}

PixelPreview Graphics::preview(const com_ptr<ID3D11Texture2D>& source) {
    D3D11_TEXTURE2D_DESC desc{};
    source->GetDesc(&desc);
    auto scale = std::min({1.0, 640.0 / desc.Width, 240.0 / desc.Height});
    auto width = std::max(1u, static_cast<uint32_t>(desc.Width * scale));
    auto height = std::max(1u, static_cast<uint32_t>(desc.Height * scale));
    D3D11_TEXTURE2D_DESC current{};
    if (preview_target) preview_target->GetDesc(&current);
    if (current.Width != width || current.Height != height) {
        preview_target = texture(width, height, false);
        preview_readback = texture(width, height, true);
    }
    draw(source, preview_target, {0, 0, static_cast<float>(desc.Width), static_cast<float>(desc.Height)});
    context->CopyResource(preview_readback.get(), preview_target.get());
    completed();
    platform::ReadMapping mapping(context, preview_readback.as<ID3D11Resource>());
    PixelPreview result;
    result.width = width;
    result.height = height;
    result.pixels = mapping.bgra(width, height);
    return result;
}
