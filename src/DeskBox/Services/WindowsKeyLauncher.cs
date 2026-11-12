using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace DeskBox.Services;

/// <summary>保留物理 Win 键按下与所有组合键，只替换单按时的松开事件。</summary>
internal sealed class WindowsKeyLauncher : IDisposable
{
    private const int LeftWin = 0x5B, RightWin = 0x5C;
    private const uint ExtendedKey = 0x01, KeyUp = 0x02;
    private static readonly UIntPtr ReplayTag = new(0x44425857);
    private readonly Dispatcher _dispatcher;
    private readonly Action _toggle;
    private readonly KeyboardProc _callback;
    private IntPtr _hook;
    private int _candidate;

    internal WindowsKeyLauncher(Dispatcher dispatcher, Action toggle)
    {
        _dispatcher = dispatcher;
        _toggle = toggle;
        _callback = OnKey;
    }

    internal bool Start()
    {
        if (_hook != IntPtr.Zero) return true;
        _hook = SetWindowsHookEx(13, _callback, Interop.NativeMethods.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
            Log.Warn("无法监听 Win 键: " + Marshal.GetLastWin32Error());
        return _hook != IntPtr.Zero;
    }

    private IntPtr OnKey(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0)
        {
            var key = Marshal.PtrToStructure<KeyEvent>(data);
            var msg = (int)message;
            if (key.Extra != ReplayTag && msg is 0x100 or 0x101 or 0x104 or 0x105)
            {
                var down = msg is 0x100 or 0x104;
                var vk = (int)key.VirtualKey;
                var winDown = down && vk is LeftWin or RightWin;
                if (Observe(ref _candidate, vk, down, winDown && Down(vk), winDown && AnyModifierDown()) &&
                    ReplayRelease((ushort)vk))
                {
                    _dispatcher.BeginInvoke(_toggle, DispatcherPriority.Input);
                    return new IntPtr(1);
                }
            }
        }
        return CallNextHookEx(_hook, code, message, data);
    }

    internal static bool Observe(ref int candidate, int key, bool down, bool repeated, bool modified)
    {
        if (key is not (LeftWin or RightWin))
        {
            candidate = 0;
            return false;
        }
        if (down)
        {
            if (!repeated) candidate = modified ? 0 : key;
            return false;
        }
        var tap = candidate == key;
        candidate = 0;
        return tap;
    }

    private static bool AnyModifierDown() =>
        Down(0x10) || Down(0x11) || Down(0x12) || Down(LeftWin) || Down(RightWin);

    private static bool Down(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;

    private static bool ReplayRelease(ushort vk)
    {
        // Windows sees a reserved dummy event before the matching key-up, so it
        // releases the real modifier without treating the sequence as Start.
        var events = new[] { Input.Release(0xFF, KeyUp), Input.Release(vk, KeyUp | ExtendedKey) };
        return SendInput((uint)events.Length, events, Marshal.SizeOf<Input>()) == events.Length;
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        _candidate = 0;
    }

    private delegate IntPtr KeyboardProc(int code, IntPtr message, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyEvent
    {
        public uint VirtualKey, ScanCode, Flags, Time;
        public UIntPtr Extra;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputData Data;

        public static Input Release(ushort key, uint flags) => new()
        {
            Type = 1,
            Data = new InputData { Keyboard = new KeyboardInput { Key = key, Flags = flags, Extra = ReplayTag } },
        };
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputData
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public MouseInput Mouse; // INPUT 的大小由完整 union 决定。
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort Key, Scan;
        public uint Flags, Time;
        public UIntPtr Extra;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X, Y;
        public uint Data, Flags, Time;
        public UIntPtr Extra;
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int id, KeyboardProc callback, IntPtr module, uint thread);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, [In] Input[] input, int size);
}
