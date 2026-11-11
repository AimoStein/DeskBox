using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using Microsoft.Win32;

// Installed in Program Files. The user-writable DeskBox executable is only opened with a verified
// medium-integrity token from this same interactive account and session.
internal static class FastStartLauncher
{
    private const uint TokenRights = 0x000B; // QUERY | DUPLICATE | ASSIGN_PRIMARY
    private const int TokenLinkedToken = 19;
    private const int TokenIntegrityLevel = 25;
    private const uint CreateUnicodeEnvironment = 0x400;
    private const uint CreateSuspended = 4;

    private static int Main()
    {
        try
        {
            string executable;
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\DeskBox\FastStart"))
                executable = key == null ? null : key.GetValue("Executable") as string;
            if (String.IsNullOrEmpty(executable)) return 0;
            if (executable.Length < 4 || !Char.IsLetter(executable[0]) || executable[1] != ':' ||
                executable[2] != '\\' || executable.IndexOf('"') >= 0 ||
                !String.Equals(Path.GetExtension(executable), ".exe", StringComparison.OrdinalIgnoreCase)) return 87;

            using (var user = WindowsIdentity.GetCurrent())
            {
                if (IsDesktopToken(user.Token, user.User))
                {
                    using (Process.Start(new ProcessStartInfo(executable, "--startup") {
                        UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)
                    })) { }
                    return 0;
                }

                IntPtr linked;
                int size;
                if (GetTokenInformation(user.Token, TokenLinkedToken, out linked, IntPtr.Size, out size))
                {
                    try
                    {
                        if (IsDesktopToken(linked, user.User) && StartWithToken(linked, user.User, executable)) return 0;
                    }
                    finally { CloseHandle(linked); }
                }

                // Some policy combinations omit the linked token. Use this session's shell token
                // as soon as it exists, ahead of Explorer's startup-item queue.
                var error = 1314;
                for (int i = 0; i < 400; i++)
                {
                    error = StartFromShell(user.User, executable);
                    if (error == 0) return 0;
                    Thread.Sleep(50);
                }
                return error;
            }
        }
        catch { return 1; }
    }

    private static int StartFromShell(SecurityIdentifier user, string executable)
    {
        uint id;
        if (GetWindowThreadProcessId(GetShellWindow(), out id) == 0) return 1168;
        IntPtr process = OpenProcess(0x1080, false, id); // QUERY_LIMITED_INFORMATION | CREATE_PROCESS
        if (process == IntPtr.Zero) return Marshal.GetLastWin32Error();
        var list = IntPtr.Zero;
        var parent = IntPtr.Zero;
        var environment = IntPtr.Zero;
        var initialized = false;
        try
        {
            IntPtr token;
            if (!OpenProcessToken(process, 0x000A, out token)) return Marshal.GetLastWin32Error();
            try
            {
                if (!IsDesktopToken(token, user)) return 1349;
                if (!CreateEnvironmentBlock(out environment, token, false)) return Marshal.GetLastWin32Error();
            }
            finally { CloseHandle(token); }

            IntPtr bytes = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref bytes);
            list = Marshal.AllocHGlobal(bytes);
            initialized = InitializeProcThreadAttributeList(list, 1, 0, ref bytes);
            if (!initialized) return Marshal.GetLastWin32Error();
            parent = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(parent, process);
            if (!UpdateProcThreadAttribute(list, 0, new IntPtr(0x00020000), parent, new IntPtr(IntPtr.Size),
                IntPtr.Zero, IntPtr.Zero)) return Marshal.GetLastWin32Error();

            var startup = new StartupInfoEx();
            startup.Info.Size = Marshal.SizeOf(typeof(StartupInfoEx));
            startup.Info.Desktop = @"winsta0\default";
            startup.Attributes = list;
            ProcessInformation child;
            if (!CreateProcessW(executable, Command(executable), IntPtr.Zero, IntPtr.Zero, false,
                0x00080000 | CreateUnicodeEnvironment | CreateSuspended, environment,
                Path.GetDirectoryName(executable), ref startup, out child)) return Marshal.GetLastWin32Error();
            return ResumeDesktopChild(child, user) ? 0 : 1349;
        }
        finally
        {
            if (initialized) DeleteProcThreadAttributeList(list);
            if (parent != IntPtr.Zero) Marshal.FreeHGlobal(parent);
            if (list != IntPtr.Zero) Marshal.FreeHGlobal(list);
            if (environment != IntPtr.Zero) DestroyEnvironmentBlock(environment);
            CloseHandle(process);
        }
    }

    private static bool IsDesktopToken(IntPtr token, SecurityIdentifier account)
    {
        int length;
        GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out length);
        if (length == 0) return false;
        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, length, out length)) return false;
            var sid = Marshal.ReadIntPtr(buffer);
            int subAuthorities = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
            if (subAuthorities == 0) return false;
            int integrity = Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)subAuthorities - 1));
            using (var identity = new WindowsIdentity(token))
                return integrity >= 0x2000 && integrity < 0x3000 && account.Equals(identity.User);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static bool StartWithToken(IntPtr token, SecurityIdentifier user, string executable)
    {
        IntPtr primary;
        if (!DuplicateTokenEx(token, TokenRights, IntPtr.Zero, 2, 1, out primary)) return false;
        IntPtr environment = IntPtr.Zero;
        try
        {
            if (!CreateEnvironmentBlock(out environment, primary, false)) return false;
            var startup = new StartupInfo();
            startup.Size = Marshal.SizeOf(typeof(StartupInfo));
            startup.Desktop = @"winsta0\default";
            ProcessInformation child;
            var flags = CreateUnicodeEnvironment | CreateSuspended;
            var directory = Path.GetDirectoryName(executable);
            if (!CreateProcessWithTokenW(primary, 0, executable, Command(executable), flags,
                environment, directory, ref startup, out child) &&
                !CreateProcessAsUserW(primary, executable, Command(executable), IntPtr.Zero, IntPtr.Zero, false,
                    flags, environment, directory, ref startup, out child)) return false;
            return ResumeDesktopChild(child, user);
        }
        finally
        {
            if (environment != IntPtr.Zero) DestroyEnvironmentBlock(environment);
            CloseHandle(primary);
        }
    }

    private static StringBuilder Command(string executable)
    {
        return new StringBuilder("\"" + executable + "\" --startup");
    }

    private static bool ResumeDesktopChild(ProcessInformation child, SecurityIdentifier user)
    {
        var resumed = false;
        try
        {
            IntPtr token;
            if (!OpenProcessToken(child.Process, 8, out token)) return false;
            try { if (!IsDesktopToken(token, user)) return false; }
            finally { CloseHandle(token); }
            resumed = ResumeThread(child.Thread) != uint.MaxValue;
            return resumed;
        }
        finally
        {
            if (!resumed) TerminateProcess(child.Process, 1349);
            CloseHandle(child.Thread);
            CloseHandle(child.Process);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        internal int Size;
        internal string Reserved, Desktop, Title;
        internal int X, Y, XSize, YSize, XChars, YChars, Fill, Flags;
        internal short Show, ReservedSize;
        internal IntPtr ReservedPointer, Input, Output, Error;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        internal IntPtr Process, Thread;
        internal uint ProcessId, ThreadId;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfoEx
    {
        internal StartupInfo Info;
        internal IntPtr Attributes;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint id);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr process, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr bytes);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, int flags, IntPtr attribute, IntPtr value,
        IntPtr size, IntPtr previous, IntPtr returnedSize);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool CreateProcessW(string application, StringBuilder command, IntPtr processAttributes,
        IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string directory,
        ref StartupInfoEx startup, out ProcessInformation process);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int type, IntPtr data, int length, out int needed);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int type, out IntPtr data, int length, out int needed);
    [DllImport("advapi32.dll")] private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);
    [DllImport("advapi32.dll")] private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(IntPtr token, uint access, IntPtr attributes,
        int impersonationLevel, int tokenType, out IntPtr duplicate);
    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);
    [DllImport("userenv.dll")] private static extern bool DestroyEnvironmentBlock(IntPtr environment);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool CreateProcessWithTokenW(IntPtr token, uint logonFlags, string application,
        StringBuilder command, uint flags, IntPtr environment, string directory, ref StartupInfo startup,
        out ProcessInformation process);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool CreateProcessAsUserW(IntPtr token, string application, StringBuilder command,
        IntPtr processAttributes, IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment,
        string directory, ref StartupInfo startup, out ProcessInformation process);
}
