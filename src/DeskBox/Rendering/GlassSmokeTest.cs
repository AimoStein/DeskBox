using System.IO;
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
        var config = new AppConfig { CornerRadius = 20, BoxOpacity = 0.65 };
        ThemeManager.Apply(ThemeMode.Light);
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
                    windows[0].MoveContentTo(firstConfig.X + 40, firstConfig.Y + 20);
                    windows[1].Width += 96;
                    break;
                case 3:
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
                    timer.Stop();
                    foreach (var window in windows) window.Close();
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
