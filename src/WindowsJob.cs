using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

sealed class WindowsJob : IDisposable
{
    readonly ChildLifetime lifetime = new();
    SafeProcessHandle? process;
    SafeWaitHandle? thread;
    public int ExitCode { get; private set; } = 1;

    public WindowsJob(string exe, IReadOnlyList<string> arguments)
    {
        nuint length = 0;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref length);
        if (length == 0) throw new Win32Exception();
        var list = Marshal.AllocHGlobal(checked((int)length));
        var value = Marshal.AllocHGlobal(IntPtr.Size);
        bool initialized = false;
        try
        {
            if (!InitializeProcThreadAttributeList(list, 1, 0, ref length)) throw new Win32Exception();
            initialized = true;
            Marshal.WriteIntPtr(value, lifetime.Handle.DangerousGetHandle());
            if (!UpdateProcThreadAttribute(list, 0, 0x0002000d, value, (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception();
            var startup = new StartupEx { Info = new() { Size = Marshal.SizeOf<StartupEx>() }, Attributes = list };
            var command = new StringBuilder(string.Join(" ", new[] { exe }.Concat(arguments).Select(Quote)));
            const uint createSuspended = 0x4, extendedStartupInfo = 0x80000, noWindow = 0x08000000;
            if (!CreateProcess(exe, command, IntPtr.Zero, IntPtr.Zero, false,
                createSuspended | extendedStartupInfo | noWindow, IntPtr.Zero, Path.GetDirectoryName(exe)!,
                ref startup, out var info)) throw new Win32Exception();
            process = new SafeProcessHandle(info.Process, ownsHandle: true);
            thread = new SafeWaitHandle(info.Thread, ownsHandle: true);
        }
        catch { lifetime.Dispose(); process?.Dispose(); thread?.Dispose(); throw; }
        finally
        {
            if (initialized) DeleteProcThreadAttributeList(list);
            Marshal.FreeHGlobal(value);
            Marshal.FreeHGlobal(list);
        }
    }

    // The executable is passed separately to CreateProcess. Quote argv for Windows CRT parsing.
    internal static string Quote(string argument)
    {
        var result = new StringBuilder("\"");
        int slashes = 0;
        foreach (char c in argument)
        {
            if (c == '\\') { slashes++; continue; }
            result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
            result.Append(c);
            slashes = 0;
        }
        result.Append('\\', slashes * 2).Append('"');
        return result.ToString();
    }

    public void Start()
    {
        if (thread is null || ResumeThread(thread) == uint.MaxValue) throw new Win32Exception();
        thread.Dispose();
        thread = null;
    }

    public bool IsEmpty()
    {
        if (lifetime.ActiveCount != 0) return false;
        if (process is { } handle)
        {
            // Job accounting can reach zero before Windows signals the root process.
            const uint waitTimeout = 258;
            uint waitResult = WaitForSingleObject(handle, 0);
            if (waitResult == waitTimeout) return false;
            if (waitResult != 0 || !GetExitCodeProcess(handle, out uint code))
                throw new Win32Exception();
            ExitCode = unchecked((int)code);
            handle.Dispose();
            process = null;
        }
        return true;
    }

    public void Stop()
    {
        if (!TerminateJobObject(lifetime.Handle, 1)) throw new Win32Exception();
        thread?.Dispose();
        thread = null;
    }

    public void Dispose()
    {
        thread?.Dispose();
        process?.Dispose();
        lifetime.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Startup
    {
        public int Size;
        public IntPtr Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XChars, YChars, Fill, Flags;
        public short Show, ReservedLength;
        public IntPtr Reserved2, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct StartupEx { public Startup Info; public IntPtr Attributes; }
    [StructLayout(LayoutKind.Sequential)]
    struct ProcessInfo { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref nuint bytes);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, nuint key, IntPtr value, nuint size, IntPtr previous, IntPtr bytes);
    [DllImport("kernel32.dll")]
    static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateProcess(string exe, StringBuilder command, IntPtr security, IntPtr threadSecurity,
        bool inheritHandles, uint flags, IntPtr environment, string directory, ref StartupEx startup, out ProcessInfo info);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint ResumeThread(SafeWaitHandle thread);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint code);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool TerminateJobObject(SafeFileHandle job, uint code);
}
