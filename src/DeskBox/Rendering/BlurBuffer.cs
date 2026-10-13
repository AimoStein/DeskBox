using Vortice.Direct3D11;
using Vortice.DXGI;

namespace DeskBox.Rendering;

/// <summary>Half-resolution separable Gaussian blur, reused across all boxes on this wallpaper.</summary>
internal sealed class BlurBuffer : IDisposable
{
    internal ID3D11Texture2D Horizontal { get; }
    internal ID3D11Texture2D Output { get; }
    internal ID3D11RenderTargetView HorizontalTarget { get; }
    internal ID3D11RenderTargetView OutputTarget { get; }
    internal uint Width { get; }
    internal uint Height { get; }
    internal long Frame { get; set; } = -1;
    internal float Radius { get; set; } = -1;

    internal BlurBuffer(ID3D11Device device, uint width, uint height)
    {
        Width = Math.Max(1, width / 2); Height = Math.Max(1, height / 2);
        var description = new Texture2DDescription
        {
            Width = Width, Height = Height, MipLevels = 1, ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource
        };
        Horizontal = device.CreateTexture2D(description);
        Output = device.CreateTexture2D(description);
        HorizontalTarget = device.CreateRenderTargetView(Horizontal);
        OutputTarget = device.CreateRenderTargetView(Output);
    }

    public void Dispose()
    {
        OutputTarget.Dispose(); HorizontalTarget.Dispose(); Output.Dispose(); Horizontal.Dispose();
    }
}
