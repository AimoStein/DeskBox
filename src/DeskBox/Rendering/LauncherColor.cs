using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Media3D;
using DeskBox.Services;
using Vortice.D3DCompiler;

namespace DeskBox.Rendering;

internal sealed class LauncherColor : ShaderEffect
{
    private static readonly Lazy<PixelShader?> Shader = new(LoadShader);
    private static readonly DependencyProperty InputProperty = RegisterPixelShaderSamplerProperty("Input", typeof(LauncherColor), 0);
    private static readonly DependencyProperty[] ChannelProperties = new[] { "Red", "Green", "Blue" }
        .Select((name, index) => DependencyProperty.Register(name, typeof(Point3D), typeof(LauncherColor),
            new UIPropertyMetadata(new Point3D(), PixelShaderConstantCallback(index)))).ToArray();

    internal static LauncherColor? Create() => Shader.Value is { } shader ? new LauncherColor(shader) : null;

    private static PixelShader? LoadShader()
    {
        try
        {
            using var source = typeof(LauncherColor).Assembly.GetManifestResourceStream("DeskBox.Rendering.LauncherColor.hlsl")!;
            using var reader = new StreamReader(source);
            using var bytes = new MemoryStream(Compiler.Compile(reader.ReadToEnd(), "PS", "LauncherColor", "ps_2_0").ToArray());
            var shader = new PixelShader();
            shader.SetStreamSource(bytes);
            shader.Freeze();
            return shader;
        }
        catch (Exception ex)
        {
            Log.Warn($"面板背景颜色不可用: {ex.Message}");
            return null;
        }
    }

    private LauncherColor(PixelShader shader)
    {
        PixelShader = shader;
        UpdateShaderValue(InputProperty);
        foreach (var channel in ChannelProperties) UpdateShaderValue(channel);
    }

    internal void SetColor(double brightness, double hue, double saturation)
    {
        var angle = hue * Math.PI / 180;
        var cos = Math.Cos(angle) * saturation;
        var sin = Math.Sin(angle) * saturation;
        // Hue rotation preserves the luminance axis. At zero saturation all
        // three output channels receive the same luminance.
        var luminance = new Vector3D(0.213, 0.715, 0.072);
        var rotation = new[]
        {
            new Vector3D(-0.213, -0.715, 0.928),
            new Vector3D(0.143, 0.140, -0.283),
            new Vector3D(-0.787, 0.715, 0.072),
        };
        for (var i = 0; i < ChannelProperties.Length; i++)
        {
            var identity = new Vector3D(i == 0 ? 1 : 0, i == 1 ? 1 : 0, i == 2 ? 1 : 0);
            var row = (luminance + (identity - luminance) * cos + rotation[i] * sin) * brightness;
            SetValue(ChannelProperties[i], (Point3D)row);
        }
    }
}
