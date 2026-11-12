using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Media3D;
using Vortice.D3DCompiler;

namespace DeskBox.Rendering;

internal sealed class LauncherGlass : ShaderEffect
{
    private static readonly Lazy<PixelShader?> Shader = new(() => LoadShader("LauncherGlass"));
    private static readonly Lazy<PixelShader?> SectorShader = new(() => LoadShader("LauncherSectorGlass"));
    private static readonly DependencyProperty InputProperty = RegisterPixelShaderSamplerProperty("Input", typeof(LauncherGlass), 0);
    private static readonly DependencyProperty SurfaceSizeProperty = DependencyProperty.Register(nameof(SurfaceSize), typeof(Point),
        typeof(LauncherGlass), new UIPropertyMetadata(new Point(1, 1), PixelShaderConstantCallback(0)));
    private static readonly DependencyProperty StrengthProperty = DependencyProperty.Register(nameof(Strength), typeof(double),
        typeof(LauncherGlass), new UIPropertyMetadata(15.0, PixelShaderConstantCallback(1)));
    private static readonly DependencyProperty[] SectorProperties = new[] { "EdgeA", "EdgeB", "Hub" }
        .Select((name, index) => DependencyProperty.Register(name, typeof(Point3D), typeof(LauncherGlass),
            new UIPropertyMetadata(new Point3D(), PixelShaderConstantCallback(index + 2)))).ToArray();
    internal static LauncherGlass? Create() => Shader.Value is { } shader ? new LauncherGlass(shader) : null;

    internal static LauncherGlass? CreateSector(Point3D[] edges, Point3D hub)
    {
        if (SectorShader.Value is not { } shader) return null;
        var effect = new LauncherGlass(shader);
        var ignore = new Point3D(0, 0, 10000);
        var values = new[] { edges.Length > 0 ? edges[0] : ignore, edges.Length > 1 ? edges[1] : ignore, hub };
        for (var i = 0; i < SectorProperties.Length; i++)
        {
            effect.SetValue(SectorProperties[i], values[i]);
            effect.UpdateShaderValue(SectorProperties[i]);
        }
        return effect;
    }

    private LauncherGlass(PixelShader shader)
    {
        PixelShader = shader;
        UpdateShaderValue(InputProperty);
        UpdateShaderValue(SurfaceSizeProperty);
        UpdateShaderValue(StrengthProperty);
    }

    internal Point SurfaceSize { set => SetValue(SurfaceSizeProperty, value); }
    internal double Strength { set => SetValue(StrengthProperty, value); }

    internal static PixelShader? LoadShader(string name)
    {
        try
        {
            using var source = Assembly.GetExecutingAssembly().GetManifestResourceStream($"DeskBox.Rendering.{name}.hlsl")!;
            using var reader = new StreamReader(source);
            using var bytes = new MemoryStream(Compiler.Compile(reader.ReadToEnd(), "PS", name, "ps_2_0").ToArray());
            var shader = new PixelShader();
            shader.SetStreamSource(bytes);
            shader.Freeze();
            return shader;
        }
        catch (Exception ex)
        {
            Log.Warn($"面板着色器 {name} 不可用: {ex.Message}");
            return null;
        }
    }
}
