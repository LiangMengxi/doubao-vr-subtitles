#pragma once
#include <d3d11.h>
#include <winrt/base.h>
#include <array>
#include <chrono>
#include <functional>
#include <vector>
#include "bridge.h"

struct PixelPreview {
    std::vector<uint8_t> pixels;
    uint32_t width = 0;
    uint32_t height = 0;
};

class Graphics {
public:
    explicit Graphics(int adapter, std::function<void()> release_consumer_output = {});
    winrt::com_ptr<ID3D11Device> device;
    winrt::com_ptr<ID3D11DeviceContext> context;
    winrt::com_ptr<ID3D11Texture2D> render(const winrt::com_ptr<ID3D11Texture2D>& source, uint32_t width, uint32_t height,
        const BridgeOptions& options);
    PixelPreview preview(const winrt::com_ptr<ID3D11Texture2D>& source);
    void preview(const winrt::com_ptr<ID3D11Texture2D>& source, PixelPreview& output);
    void reset_source();
    void completed();
private:
    struct Constants {
        float left;
        float top;
        float width;
        float height;
    };
    struct Target {
        winrt::com_ptr<ID3D11Texture2D> texture;
        winrt::com_ptr<ID3D11RenderTargetView> render_view;
        winrt::com_ptr<ID3D11ShaderResourceView> sample_view;
    };
    struct Retired {
        std::array<Target, 3> textures;
        std::chrono::steady_clock::time_point time;
    };
    void draw(const winrt::com_ptr<ID3D11Texture2D>& source, const winrt::com_ptr<ID3D11ShaderResourceView>& input,
        const Target& destination, Constants region);
    Target target(uint32_t width, uint32_t height);
    winrt::com_ptr<ID3D11Texture2D> texture(uint32_t width, uint32_t height, bool staging);
    winrt::com_ptr<ID3D11ShaderResourceView> source_view(const winrt::com_ptr<ID3D11Texture2D>& source);
    winrt::com_ptr<ID3D11ShaderResourceView> preview_view(const winrt::com_ptr<ID3D11Texture2D>& source);
    Constants bounds(const winrt::com_ptr<ID3D11ShaderResourceView>& input, uint32_t width, uint32_t height, bool trim);
    winrt::com_ptr<ID3D11VertexShader> vertex_shader;
    winrt::com_ptr<ID3D11PixelShader> pixel_shader;
    winrt::com_ptr<ID3D11ComputeShader> bounds_shader;
    winrt::com_ptr<ID3D11Buffer> constants;
    winrt::com_ptr<ID3D11SamplerState> sampler;
    winrt::com_ptr<ID3D11RasterizerState> rasterizer;
    winrt::com_ptr<ID3D11Buffer> bounds_buffer;
    winrt::com_ptr<ID3D11Buffer> bounds_readback;
    winrt::com_ptr<ID3D11UnorderedAccessView> bounds_view;
    winrt::com_ptr<ID3D11Query> query;
    std::array<Target, 3> textures;
    std::vector<Retired> retired;
    uint32_t output_width = 0;
    uint32_t output_height = 0;
    size_t next = 0;
    std::function<void()> release_consumer;
    winrt::com_ptr<ID3D11Texture2D> sampled_source;
    winrt::com_ptr<ID3D11ShaderResourceView> sampled_view;
    Target preview_target;
    winrt::com_ptr<ID3D11Texture2D> preview_readback;
};
