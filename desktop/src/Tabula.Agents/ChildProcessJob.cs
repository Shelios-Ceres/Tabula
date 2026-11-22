using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Tabula.Agents;

/// <summary>
/// A Windows job object with KILL_ON_JOB_CLOSE: agent processes (and everything they start, such as npx's node or the MCP bridge)
/// join it, so they end with the client even when it crashes instead of lingering in the background.
/// </summary>
public static class ChildProcessJob
{
    private const int ExtendedLimitInformation = 9;
    private const uint KillOnJobClose = 0x2000;
    private static readonly Lazy<IntPtr> Job = new(Create);

    /// <summary>Best effort: a process that cannot join (already exited, or a job that forbids it) still runs and is cleaned up normally.</summary>
    public static void Add(Process process)
    {
        try
        {
            if (Job.Value != IntPtr.Zero && !process.HasExited) AssignProcessToJobObject(Job.Value, process.Handle);
        }
        catch (InvalidOperationException) { }
    }

    private static IntPtr Create()
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero) return IntPtr.Zero;
        var info = new ExtendedLimit { Basic = new BasicLimit { LimitFlags = KillOnJobClose } };
        var size = Marshal.SizeOf<ExtendedLimit>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, buffer, false);
            if (!SetInformationJobObject(job, ExtendedLimitInformation, buffer, (uint)size)) return IntPtr.Zero;
        }
        finally { Marshal.FreeHGlobal(buffer); }
        // The handle is deliberately never closed: closing it is what kills the members, which happens when the client exits.
        return job;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimit
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimit
    {
        public BasicLimit Basic;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
}
