using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.Views;

namespace DeskBox.Rendering;

/// <summary>Exercises real box windows against the running wallpaper, with isolated fixture folders.</summary>
internal static class GlassSmokeTest
{
    internal static void Run(string output)
    {
        Directory.CreateDirectory(output);
        var fixture = Path.Combine(output, "fixtures");
        Directory.CreateDirectory(fixture);
        File.WriteAllText(Path.Combine(fixture, "玻璃测试.txt"), "DeskBox isolated rendering fixture");
        var config = new AppConfig();
        ThemeManager.Apply(ThemeMode.System);
        var firstConfig = new BoxConfig { Title = "玻璃测试 A", Folder = fixture,
            X = SystemParameters.WorkArea.Right - 850, Y = SystemParameters.WorkArea.Bottom - 390, Width = 350, Height = 270 };
        var secondConfig = new BoxConfig { Title = "玻璃测试 B", Folder = fixture,
            X = SystemParameters.WorkArea.Right - 450, Y = SystemParameters.WorkArea.Bottom - 390, Width = 350, Height = 270 };
        var windows = new[] { new BoxWindow(config, firstConfig, new DesktopShell()), new BoxWindow(config, secondConfig, new DesktopShell()) };
        foreach (var window in windows) window.SetVisible(true);
        var report = new List<string>();
        var failures = 0;
        void Check(string name, bool value)
        {
            report.Add($"[{(value ? "PASS" : "FAIL")}] {name}");
            if (!value) failures++;
        }
        var second = 0;
        long previousFrames = 0;
        long[]? previousDraws = null;
        LiquidGlassEngine? failedEngine = null;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            second++;
            var engine = LiquidGlassEngine.Current;
            report.Add($"second={second}: {engine?.SurfaceStatus}; tickMs={engine?.LastTickMilliseconds:F2}");
            switch (second)
            {
                case 2:
                    Check("两个正式盒子共用壁纸捕获", engine?.Captures.Count == 1);
                    previousFrames = engine?.Captures.Sum(c => c.Frames) ?? 0;
                    Check("两个盒子均显示 GPU 玻璃", windows.All(w => ((GlassBackdrop)w.FindName("GlassBackground")).Opacity == 1));
                    engine?.SaveSurface(Path.Combine(output, "before-surface.png"));
                    if (engine?.Captures.FirstOrDefault()?.Texture is { } wallpaper)
                        GlassPreview.SaveTexture(engine, wallpaper, Path.Combine(output, "wallpaper.png"));
                    GlassPreview.SaveVisual(windows[0], Path.Combine(output, "before-composite.png"));
                    Check("WPF 实际合成了玻璃背景", windows.All(w =>
                        GlassPreview.HasVisibleGlass((GlassBackdrop)w.FindName("GlassBackground"))));
                    // Model WPF dropping its back-buffer reference while native targets stay alive.
                    foreach (var surface in engine!.Surfaces)
                    {
                        surface.Image.Lock();
                        try { surface.Image.SetBackBuffer(System.Windows.Interop.D3DResourceType.IDirect3DSurface9, 0); }
                        finally { surface.Image.Unlock(); }
                    }
                    Check("模拟显示缓冲区丢失后玻璃确实不可见", windows.All(w =>
                        !GlassPreview.HasVisibleGlass((GlassBackdrop)w.FindName("GlassBackground"))));
                    windows[0].MoveContentTo(firstConfig.X + 40, firstConfig.Y + 20);
                    windows[1].Width += 96;
                    break;
                case 3:
                    GlassPreview.SaveVisual(windows[0], Path.Combine(output, "after-recovery-composite.png"));
                    Check("显示缓冲区丢失后恢复实际玻璃画面", windows.All(w =>
                        GlassPreview.HasVisibleGlass((GlassBackdrop)w.FindName("GlassBackground"))));
                    Check("视频持续更新", (engine?.Captures.Sum(c => c.Frames) ?? 0) > previousFrames);
                    ((Button)windows[0].FindName("CollapseButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check("收起盒子", firstConfig.Collapsed);
                    break;
                case 4:
                    ((Button)windows[0].FindName("CollapseButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    ThemeManager.Apply(ThemeMode.Dark);
                    foreach (var window in windows) window.SetVisible(false);
                    break;
                case 5:
                    Check("全部隐藏时释放捕获", engine?.Captures.Count == 0);
                    foreach (var window in windows) window.SetVisible(true);
                    break;
                case 6:
                    Check("恢复显示后重新取得视频", (engine?.Captures.Sum(c => c.Frames) ?? 0) > 0);
                    config.LiquidGlassEnabled = false;
                    foreach (var window in windows) window.RefreshFromConfig(false);
                    Check("关闭玻璃释放 GPU 服务", LiquidGlassEngine.Current is null);
                    break;
                case 7:
                    config.LiquidGlassEnabled = true;
                    foreach (var window in windows) window.RefreshFromConfig(false);
                    break;
                case 8:
                    Check("重新启用后恢复两盒子玻璃", windows.All(w => ((GlassBackdrop)w.FindName("GlassBackground")).Opacity == 1));
                    engine?.SaveSurface(Path.Combine(output, "box-glass.png"));
                    ThemeManager.Apply(ThemeMode.Light);
                    break;
                case 10:
                    // A caller holding the image makes nested TryLock return false.
                    // Leave it held for one second to exercise the skipped-frame path.
                    foreach (var surface in engine!.Surfaces) surface.Image.Lock();
                    break;
                case 11:
                    foreach (var surface in engine!.Surfaces) surface.Image.Unlock();
                    previousDraws = engine.Surfaces.Select(s => s.Draws).ToArray();
                    Check("繁忙时跳过绘制", engine.Surfaces.All(s => s.LockMisses > 0));
                    break;
                case 12:
                    Check("跳过绘制后仍持续更新", engine!.Surfaces.Select((s, i) => s.Draws > previousDraws![i]).All(x => x));
                    AppearanceTest.Run(engine, windows, config, output, Check);
                    break;
                case 14:
                    failedEngine = engine;
                    StartupTrace.IsStartup = false;
                    config.BoxOpacity = 0;
                    foreach (var window in windows) window.RefreshFromConfig(false);
                    // Exercise the production failure handler with the HRESULT seen in
                    // the user's log, without resetting the machine's graphics driver.
                    engine!.RecoverAfterFailure(new COMException("模拟 GPU 设备丢失", unchecked((int)0x887A0005)));
                    Check("设备丢失后释放旧 GPU 服务", LiquidGlassEngine.Current is null);
                    Check("设备丢失时暂时回退背景", windows.All(w => !((GlassBackdrop)w.FindName("GlassBackground")).IsAvailable));
                    Check("复现零浓度配置下玻璃丢失后的透明底色", windows.All(w =>
                        ((Border)w.FindName("Backdrop")).Opacity == 0));
                    break;
                case 17:
                    Check("设备丢失后自动创建新引擎", engine is not null && !ReferenceEquals(engine, failedEngine));
                    Check("设备丢失后恢复两盒子的实际玻璃画面", windows.All(w =>
                        ((GlassBackdrop)w.FindName("GlassBackground")).IsAvailable
                        && GlassPreview.HasVisibleGlass((GlassBackdrop)w.FindName("GlassBackground"))));
                    Check("恢复后保持原来的细边框", windows.All(w =>
                        ((Border)w.FindName("Frame")).BorderThickness == new Thickness(1)
                        && ((System.Windows.Media.SolidColorBrush)((Border)w.FindName("Frame")).BorderBrush).Color
                            == System.Windows.Media.Color.FromArgb(48, 255, 255, 255)));
                    GlassPreview.SaveVisual(windows[0], Path.Combine(output, "device-recovered-original-outline.png"));
                    engine!.RecoverAfterFailure(new COMException("模拟再次丢失设备", unchecked((int)0x887A0005)));
                    config.LiquidGlassEnabled = false;
                    foreach (var window in windows) window.RefreshFromConfig(false);
                    break;
                case 19:
                    Check("恢复等待期间关闭玻璃会取消重试", LiquidGlassEngine.Current is null);
                    config.LiquidGlassEnabled = true;
                    foreach (var window in windows) window.RefreshFromConfig(false);
                    break;
                case 21:
                    Check("取消重试后仍可重新开启玻璃", windows.All(w =>
                        ((GlassBackdrop)w.FindName("GlassBackground")).IsAvailable));
                    engine!.RecoverAfterFailure(new COMException("模拟退出前设备丢失", unchecked((int)0x887A0005)));
                    foreach (var window in windows) window.Close();
                    break;
                case 23:
                    timer.Stop();
                    Check("关闭全部盒子释放 GPU 服务", LiquidGlassEngine.Current is null);
                    report.Add($"failures={failures}");
                    File.WriteAllLines(Path.Combine(output, "smoke.txt"), report);
                    Application.Current.Shutdown(failures == 0 ? 0 : 1);
                    break;
            }
        };
        timer.Start();
    }
}
