using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DeskBox.Interop;
using DeskBox.Models;
using DeskBox.Rendering;

namespace DeskBox.Services;

/// <summary>Windows 11 taskbar glass, behind the real taskbar's unchanged controls.</summary>
internal sealed class TaskbarGlass : IDisposable
{
    internal const int HeaderSize = 40, SlotSize = 24, SlotCount = 16;
    internal static string MappingName => $"Local\\DeskBox.TaskbarGlass.v2.{Process.GetCurrentProcess().SessionId}";
    private readonly AppConfig _config;
    private readonly Dictionary<nint, BackingWindow> _windows = new();
    private readonly DispatcherTimer _timer;
    private readonly HashSet<uint> _attached = new();
    private readonly HashSet<uint> _attaching = new();
    private MemoryMappedFile? _mapping;
    private MemoryMappedViewAccessor? _state;
    private AttachDelegate? _attach;
    // A diagnostics DLL stays mapped in Explorer until Explorer exits. Keep our
    // local copy loaded too; never unload a DLL while an attach worker uses it.
    private static nint _library;
    private bool _disposed;
    private bool _hidden;
    private long _nextAttach;
    private long _readyAt;
    private uint _bridgeId;
    private string? _failure;
    internal string UserStatus => !_config.TaskbarGlassEnabled ? "关闭后恢复系统外观" : _failure ??
        (_hidden ? "已随任务栏隐藏" : _windows.Count > 0 && _windows.Values.All(w =>
            _state?.ReadInt32(HeaderSize + w.Slot * SlotSize + 12) == 1)
            ? "已启用 · 直角玻璃背景" : "正在等待任务栏和壁纸画面…");
    internal event Action<string>? StatusChanged;
    private string? _lastStatus;
    internal string Status => _state is null ? "off" :
        $"windows={_windows.Count}; callbacks={_state.ReadInt32(28)}; error=0x{_state.ReadInt32(24):X8}; " +
        string.Join("; ", _windows.Values.Select(w => $"hwnd={w.Taskbar}; available={w.Backdrop.IsAvailable}; slot={w.Slot}; known={_state.ReadInt32(HeaderSize + w.Slot * SlotSize + 16)}; applied={_state.ReadInt32(HeaderSize + w.Slot * SlotSize + 12)}"));

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int AttachDelegate(uint process);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint BridgeIdDelegate();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(POINT point);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint window, int index, nint value);

    internal TaskbarGlass(AppConfig config)
    {
        _config = config;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) => Tick();
    }

    internal void Refresh(bool hidden)
    {
        _hidden = hidden;
        if (_disposed) return;
        if (!_config.TaskbarGlassEnabled) { Stop(); return; }
        if (_state is null)
        {
            try
            {
                if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) ||
                    RuntimeInformation.ProcessArchitecture != Architecture.X64)
                    throw new PlatformNotSupportedException("任务栏玻璃当前支持 Windows 11 x64。");
                LoadBridge();
                _mapping = MemoryMappedFile.CreateOrOpen(MappingName, HeaderSize + SlotCount * SlotSize);
                _state = _mapping.CreateViewAccessor();
                _state.Write(16, Environment.TickCount64);
                _state.Write(8, Environment.ProcessId);
                _state.Write(12, 0);
                _state.Write(24, 0);
                _state.Write(28, 0);
                for (var i = 0; i < SlotCount; i++) ClearSlot(i);
                _state.Write(32, _bridgeId);
                _state.Write(4, 2);
                _state.Write(0, 0x47425444);
                // Give a previous bridge build time to restore its brushes first.
                _readyAt = Environment.TickCount64 + 250;
                _failure = null;
                _timer.Start();
            }
            catch (Exception ex)
            {
                Log.Warn("任务栏玻璃初始化失败，保留系统外观: " + ex.Message);
                _failure = "未能启用，已保留系统外观：" + ex.Message;
                Stop();
                return;
            }
        }
        Tick();
        foreach (var window in _windows.Values) window.Backdrop.Refresh();
    }

    private void LoadBridge()
    {
        if (_library == 0)
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("DeskBox.Taskbar.dll")
                ?? throw new FileNotFoundException("缺少任务栏背景组件。");
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            var bytes = buffer.ToArray();
            var hash = Convert.ToHexString(SHA256.HashData(bytes))[..20];
            var directory = Path.Combine(ConfigStore.Directory, "native", hash);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "DeskBox.Taskbar.dll");
            if (!File.Exists(path)) File.WriteAllBytes(path, bytes);
            // Content-addressed filename allows updates while Explorer retains a previous DLL.
            _library = NativeLibrary.Load(path);
        }
        _attach = Marshal.GetDelegateForFunctionPointer<AttachDelegate>(NativeLibrary.GetExport(_library, "AttachTaskbar"));
        _bridgeId = Marshal.GetDelegateForFunctionPointer<BridgeIdDelegate>(NativeLibrary.GetExport(_library, "GetBridgeId"))();
    }

    private void Tick()
    {
        if (_state is null || _disposed) return;
        try
        {
            _state.Write(16, Environment.TickCount64);
            _state.Write(12, _config.TaskbarGlassEnabled && !_hidden && Environment.TickCount64 >= _readyAt ? 1 : 0);
            var taskbars = NativeMethods.FindTaskbarWindows();
            foreach (var stale in _windows.Keys.Where(h => !taskbars.Contains(h)).ToArray())
            {
                var window = _windows[stale];
                ClearSlot(window.Slot);
                window.Close();
                _windows.Remove(stale);
            }
            foreach (var taskbar in taskbars)
            {
                if (!_windows.TryGetValue(taskbar, out var window))
                {
                    var slot = Enumerable.Range(0, SlotCount).FirstOrDefault(i => _windows.Values.All(w => w.Slot != i), -1);
                    if (slot < 0) break;
                    window = new BackingWindow(_config, taskbar, slot);
                    _windows.Add(taskbar, window);
                    _state.Write(HeaderSize + slot * SlotSize, taskbar.ToInt64());
                }
                window.Sync(_hidden);
                _state.Write(HeaderSize + window.Slot * SlotSize + 8,
                    !_hidden && window.IsVisible && window.Backdrop.IsAvailable ? 1 : 0);
                GetWindowThreadProcessId(taskbar, out var process);
                if (!_attached.Contains(process) && !_attaching.Contains(process) && Environment.TickCount64 >= _nextAttach)
                    Attach(process);
            }
            PublishStatus();
        }
        catch (Exception ex)
        {
            Log.Warn("任务栏玻璃更新失败，恢复系统外观: " + ex.Message);
            _failure = "未能启用，已保留系统外观：" + ex.Message;
            Stop();
        }
    }

    private async void Attach(uint process)
    {
        _attaching.Add(process);
        try
        {
            var attach = _attach!;
            var result = await Task.Run(() => attach(process));
            if (result >= 0)
            {
                _attached.Add(process);
                _failure = null;
                Log.Info($"任务栏玻璃已接入 Explorer {process}。");
            }
            else
            {
                _nextAttach = Environment.TickCount64 + 30000;
                _failure = $"连接失败（0x{result:X8}），稍后自动重试";
                Log.Warn($"任务栏玻璃连接失败 0x{result:X8}，30 秒后重试。");
            }
        }
        catch (Exception ex)
        {
            _nextAttach = Environment.TickCount64 + 30000;
            _failure = "连接失败，稍后自动重试";
            Log.Warn("任务栏玻璃连接失败: " + ex.Message);
        }
        finally { _attaching.Remove(process); }
    }

    private void ClearSlot(int slot)
    {
        if (_state is null) return;
        var offset = HeaderSize + slot * SlotSize;
        _state.Write(offset + 8, 0);
        _state.Write(offset, 0L);
        _state.Write(offset + 12, 0);
        _state.Write(offset + 16, 0);
    }

    private void Stop()
    {
        _timer.Stop();
        // Stop transparency before removing the backing windows. Explorer's
        // UI-thread timer restores the original brushes within 100 ms.
        _state?.Write(12, 0);
        foreach (var window in _windows.Values) window.Close();
        _windows.Clear();
        _state?.Dispose(); _state = null;
        _mapping?.Dispose(); _mapping = null;
        PublishStatus();
    }

    private void PublishStatus()
    {
        var status = UserStatus;
        if (status == _lastStatus) return;
        _lastStatus = status;
        StatusChanged?.Invoke(status);
    }

    internal bool HitTestPassesThrough()
    {
        return _windows.Values.Where(w => w.IsVisible).All(w =>
        {
            if (!NativeMethods.GetWindowRect(w.Handle, out var rect)) return false;
            var hit = WindowFromPoint(new POINT((rect.Left + rect.Right) / 2, (rect.Top + rect.Bottom) / 2));
            return hit != w.Handle;
        });
    }

    public void Dispose() { if (_disposed) return; Stop(); _disposed = true; }

    private sealed class BackingWindow : Window
    {
        internal nint Taskbar { get; }
        internal int Slot { get; }
        internal nint Handle { get; private set; }
        internal GlassBackdrop Backdrop { get; }
        private RECT _bounds;
        internal BackingWindow(AppConfig config, nint taskbar, int slot)
        {
            Taskbar = taskbar; Slot = slot;
            Title = "DeskBox 任务栏玻璃背景";
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            Focusable = false;
            IsHitTestVisible = false;
            Width = 1; Height = 1;
            Backdrop = new GlassBackdrop { Config = config, CornerRadiusOverride = 0,
                EnabledOverride = () => config.TaskbarGlassEnabled };
            Content = Backdrop;
            SourceInitialized += (_, _) =>
            {
                Handle = new WindowInteropHelper(this).Handle;
                var style = GetWindowLongPtr(Handle, NativeMethods.GWL_EXSTYLE).ToInt64();
                SetWindowLongPtr(Handle, NativeMethods.GWL_EXSTYLE,
                    new nint(style | NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW));
                HwndSource.FromHwnd(Handle)?.AddHook(Hook);
            };
            Closed += (_, _) => Backdrop.Dispose();
        }

        private nint Hook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
        {
            if (message == 0x0084) { handled = true; return new nint(-1); } // HTTRANSPARENT
            if (message == 0x0021) { handled = true; return new nint(3); } // MA_NOACTIVATE
            return 0;
        }

        internal void Sync(bool hidden)
        {
            var visible = !hidden && NativeMethods.IsWindowVisible(Taskbar) &&
                NativeMethods.GetWindowRect(Taskbar, out var rect) && rect.Right > rect.Left && rect.Bottom > rect.Top;
            if (!visible) { if (IsVisible) Hide(); return; }
            NativeMethods.GetWindowRect(Taskbar, out var bounds);
            // Auto-hide taskbars can leave a one-pixel reveal strip on the screen.
            // Clipping to the monitor prevents a full-height backing window leaking above it.
            var monitor = System.Windows.Forms.Screen.FromHandle(Taskbar).Bounds;
            bounds.Left = Math.Max(bounds.Left, monitor.Left);
            bounds.Top = Math.Max(bounds.Top, monitor.Top);
            bounds.Right = Math.Min(bounds.Right, monitor.Right);
            bounds.Bottom = Math.Min(bounds.Bottom, monitor.Bottom);
            if (bounds.Right <= bounds.Left || bounds.Bottom - bounds.Top <= 2) { if (IsVisible) Hide(); return; }
            if (!IsVisible) Show();
            var moved = !_bounds.Equals(bounds);
            if (moved || NativeMethods.GetWindow(Taskbar, NativeMethods.GW_HWNDNEXT) != Handle)
            {
                NativeMethods.SetWindowPos(Handle, Taskbar, bounds.Left, bounds.Top,
                    bounds.Right - bounds.Left, bounds.Bottom - bounds.Top, NativeMethods.SWP_NOACTIVATE);
                _bounds = bounds;
            }
        }
    }
}
