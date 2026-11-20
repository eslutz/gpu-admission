using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

sealed class ChildLifetime : IDisposable
{
    readonly SafeFileHandle job = CreateJobObject(IntPtr.Zero, null);

    public ChildLifetime()
    {
        if (job.IsInvalid) throw new Win32Exception();
        var limits = new ExtendedLimits { Basic = new BasicLimits { Flags = 0x2000 } };
        if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()))
        {
            var error = new Win32Exception();
            job.Dispose();
            throw error;
        }
    }

    public void Attach(Process process)
    {
        if (!AssignProcessToJobObject(job, process.SafeHandle)) throw new Win32Exception();
    }

    public void Dispose() => job.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    struct BasicLimits
    {
        public long PerProcess, PerJob;
        public uint Flags;
        public nuint MinWorkingSet, MaxWorkingSet;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ExtendedLimits
    {
        public BasicLimits Basic;
        public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes;
        public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetInformationJobObject(SafeFileHandle job, int kind, ref ExtendedLimits limits, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);
}
