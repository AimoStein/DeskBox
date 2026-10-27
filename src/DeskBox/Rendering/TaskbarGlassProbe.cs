using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Windows;
using System.Windows.Threading;
using DeskBox.Interop;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Rendering;

/// <summary>Isolated real-taskbar verification; never loads or saves the user's configuration.</summary>
internal static class TaskbarGlassProbe
{
    internal static void Run(string output)
    {
        Directory.CreateDirectory(output);
        var config = new AppConfig { TaskbarGlassEnabled = true };
        ThemeManager.Apply(ThemeMode.System);
        var service = new TaskbarGlass(config);
        using var process = Process.GetCurrentProcess();
        var mapping = MemoryMappedFile.CreateOrOpen(TaskbarGlass.MappingName, TaskbarGlass.HeaderSize + 16 * 24);
        var state = mapping.CreateViewAccessor();
        var lines = new List<string>();
        var failures = 0;
        void Check(string name, bool value)
        {
            lines.Add($"[{(value ? "PASS" : "FAIL")}] {name}");
            if (!value) failures++;
        }
        bool Applied(bool expected) => Enumerable.Range(0, 16)
            .Where(i => state.ReadInt64(TaskbarGlass.HeaderSize + i * 24) != 0)
            .All(i => (state.ReadInt32(TaskbarGlass.HeaderSize + i * 24 + 12) != 0) == expected);
        void Screenshot(string name)
        {
            foreach (var taskbar in NativeMethods.FindTaskbarWindows())
            {
                if (!NativeMethods.GetWindowRect(taskbar, out var rect) || rect.Right <= rect.Left || rect.Bottom <= rect.Top) continue;
                using var bitmap = new System.Drawing.Bitmap(rect.Right - rect.Left, rect.Bottom - rect.Top);
                using var graphics = System.Drawing.Graphics.FromImage(bitmap);
                graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, bitmap.Size);
                bitmap.Save(Path.Combine(output, $"{name}-{taskbar}.png"));
            }
        }
        Screenshot("before");
        service.Refresh(false);
        var second = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            second++;
            lines.Add($"second={second}: {service.Status}");
            File.WriteAllLines(Path.Combine(output, "progress.txt"), lines);
            switch (second)
            {
                case 5:
                    Check("taskbar background identified", state.ReadInt32(TaskbarGlass.HeaderSize + 16) != 0);
                    Check("native taskbar background transparent", Applied(true));
                    Check("background windows pass mouse input through", service.HitTestPassesThrough());
                    Screenshot("glass");
                    LiquidGlassEngine.Current?.SaveDiagnostics(output);
                    using (var surface = new System.Drawing.Bitmap(Path.Combine(output, "surface-0.png")))
                        Check("all four corners are square and opaque", surface.GetPixel(0, 0).A == 255 &&
                            surface.GetPixel(surface.Width - 1, 0).A == 255 &&
                            surface.GetPixel(0, surface.Height - 1).A == 255 &&
                            surface.GetPixel(surface.Width - 1, surface.Height - 1).A == 255);
                    service.Refresh(true);
                    break;
                case 6:
                    Check("hidden mode restores native background", Applied(false));
                    service.Refresh(false);
                    break;
                case 8:
                    Check("glass returns after unhide", Applied(true));
                    config.TaskbarGlassEnabled = false;
                    service.Refresh(false);
                    break;
                case 9:
                    Check("disabling restores system brushes", Applied(false));
                    Screenshot("disabled");
                    config.TaskbarGlassEnabled = true;
                    service.Refresh(false);
                    break;
                case 12:
                    Check("re-enabling uses existing diagnostics connection", Applied(true));
                    Check("no native bridge error", state.ReadInt32(24) == 0);
                    service.Dispose();
                    break;
                case 13:
                    Check("dispose restores taskbar", Applied(false));
                    Screenshot("restored");
                    timer.Stop();
                    lines.Add($"failures={failures}");
                    File.WriteAllLines(Path.Combine(output, "results.txt"), lines);
                    state.Dispose(); mapping.Dispose();
                    LiquidGlassEngine.Shutdown();
                    Application.Current.Shutdown(failures == 0 ? 0 : 1);
                    break;
            }
        };
        timer.Start();
    }
}
