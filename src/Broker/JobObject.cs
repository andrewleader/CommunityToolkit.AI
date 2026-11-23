using System.Runtime.InteropServices;

namespace CommunityToolkit.AI.Broker;

/// <summary>
/// A Windows job object for model hosts: kill-on-close (hosts die with the broker) plus optional memory and CPU limits.
/// </summary>
internal sealed class JobObject : IDisposable
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const int JobObjectCpuRateControlInformation = 15;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const uint JobObjectLimitProcessMemory = 0x00000100;
    private const uint JobObjectLimitDieOnUnhandledException = 0x00000400;
    private const uint CpuRateControlEnable = 0x1;
    private const uint CpuRateControlHardCap = 0x4;
    private readonly IntPtr _handle;

    public JobObject(ulong processMemoryLimitBytes, int cpuPercent)
    {
        _handle = CreateJobObjectW(IntPtr.Zero, null);
        if (_handle == IntPtr.Zero)
        {
            throw new InvalidOperationException($"CreateJobObject failed: {Marshal.GetLastWin32Error()}");
        }

        var info = new JobObjectExtendedLimitInformationStruct();
        info.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose | JobObjectLimitDieOnUnhandledException;
        if (processMemoryLimitBytes > 0)
        {
            info.BasicLimitInformation.LimitFlags |= JobObjectLimitProcessMemory;
            info.ProcessMemoryLimit = (UIntPtr)processMemoryLimitBytes;
        }

        int size = Marshal.SizeOf<JobObjectExtendedLimitInformationStruct>();
        IntPtr ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            if (!SetInformationJobObject(_handle, JobObjectExtendedLimitInformation, ptr, (uint)size))
            {
                throw new InvalidOperationException($"SetInformationJobObject failed: {Marshal.GetLastWin32Error()}");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }

        if (cpuPercent is > 0 and < 100)
        {
            var cpu = new JobObjectCpuRateControlInformationStruct { ControlFlags = CpuRateControlEnable | CpuRateControlHardCap, CpuRate = (uint)(cpuPercent * 100) };
            int cpuSize = Marshal.SizeOf<JobObjectCpuRateControlInformationStruct>();
            IntPtr cpuPtr = Marshal.AllocHGlobal(cpuSize);
            try
            {
                Marshal.StructureToPtr(cpu, cpuPtr, false);
                SetInformationJobObject(_handle, JobObjectCpuRateControlInformation, cpuPtr, (uint)cpuSize);
            }
            finally
            {
                Marshal.FreeHGlobal(cpuPtr);
            }
        }
    }

    public void Assign(IntPtr processHandle)
    {
        if (!AssignProcessToJobObject(_handle, processHandle))
        {
            throw new InvalidOperationException($"AssignProcessToJobObject failed: {Marshal.GetLastWin32Error()}");
        }
    }

    public void Dispose() => CloseHandle(_handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformationStruct
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectCpuRateControlInformationStruct
    {
        public uint ControlFlags;
        public uint CpuRate;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}

internal static class MemoryInfo
{
    public static (ulong Total, ulong Available) Get()
    {
        var status = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status) ? (status.ullTotalPhys, status.ullAvailPhys) : (0, 0);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);
}
