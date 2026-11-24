using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DeskBox.Services;

internal static class SystemPower
{
    internal static async Task ShutDownAsync(bool restart)
    {
        // /t 0 不隐含 /f，Windows 可以提示用户保存未完成的工作。
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe"),
            restart ? "/r /t 0" : "/s /t 0")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(info) ?? throw new InvalidOperationException("无法启动 Windows 关机命令。");
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new Win32Exception(process.ExitCode);
    }

    internal static Task SleepAsync() => Task.Run(() =>
    {
        const uint tokenQuery = 0x0008, tokenAdjustPrivileges = 0x0020, privilegeEnabled = 0x0002;
        using var process = Process.GetCurrentProcess();
        if (!OpenProcessToken(process.Handle, tokenQuery | tokenAdjustPrivileges, out var token))
            throw new Win32Exception();
        using (token)
        {
            if (!LookupPrivilegeValue(null, "SeShutdownPrivilege", out var luid)) throw new Win32Exception();
            var requested = new TokenPrivileges { Count = 1, Luid = luid, Attributes = privilegeEnabled };
            if (!AdjustTokenPrivileges(token, false, ref requested, Marshal.SizeOf<TokenPrivileges>(), out var previous, out _) ||
                Marshal.GetLastPInvokeError() != 0) throw new Win32Exception();
            try
            {
                if (!SetSuspendState(false, false, false)) throw new Win32Exception();
            }
            finally
            {
                AdjustTokenPrivileges(token, false, ref previous, 0, IntPtr.Zero, IntPtr.Zero);
            }
        }
    });

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint Count;
        public Luid Luid;
        public uint Attributes;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LookupPrivilegeValue(string? system, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(SafeAccessTokenHandle token, bool disableAll,
        ref TokenPrivileges requested, int bufferLength, out TokenPrivileges previous, out int returnedLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(SafeAccessTokenHandle token, bool disableAll,
        ref TokenPrivileges requested, int bufferLength, IntPtr previous, IntPtr returnedLength);

    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool SetSuspendState([MarshalAs(UnmanagedType.U1)] bool hibernate,
        [MarshalAs(UnmanagedType.U1)] bool force, [MarshalAs(UnmanagedType.U1)] bool disableWakeEvents);
}
