using System.Runtime.InteropServices;
using System.Text;

namespace Orisumi.Collectors.Network;

internal static class MacProcessIdentity
{
    internal static ProcessIdentity Read(int pid, DateTimeOffset sampleStarted)
    {
        var started = StartTime(pid);
        // Do not attach the new owner's metadata to a nettop row whose PID was reused.
        if (started is null || started > (sampleStarted.UtcTicks - DateTime.UnixEpoch.Ticks) / 10)
            return new(null, null);
        var buffer = new byte[4096]; // PROC_PIDPATHINFO_MAXSIZE
        var length = proc_pidpath(pid, buffer, (uint)buffer.Length);
        var end = length > 0 ? Array.IndexOf(buffer, (byte)0) : 0;
        var path = end > 0 ? Encoding.UTF8.GetString(buffer, 0, end) : null;
        return StartTime(pid) == started ? new(started, path) : new(null, null);
    }

    private static long? StartTime(int pid)
    {
        // Public Darwin SDK: sys/sysctl.h kinfo_proc starts with extern_proc
        // (sys/proc.h). Its LP64 prefix is timeval { int64 sec; int32 usec; pad },
        // two pointers, flags, status/padding, pid. Read only that prefix; ask
        // sysctl for the full buffer size instead of hard-coding sizeof(kinfo_proc).
        int[] name = [1, 14, 1, pid]; // CTL_KERN, KERN_PROC, KERN_PROC_PID
        nuint size = 0;
        if (sysctl(name, 4, null, ref size, IntPtr.Zero, 0) != 0 || size < 44 || size > int.MaxValue)
            return null;
        var buffer = new byte[(int)size];
        if (sysctl(name, 4, buffer, ref size, IntPtr.Zero, 0) != 0 || size < 44 ||
            BitConverter.ToInt32(buffer, 40) != pid) return null;
        var seconds = BitConverter.ToInt64(buffer, 0);
        var microseconds = BitConverter.ToInt32(buffer, 8);
        return seconds > 0 && seconds <= (long.MaxValue - 999999) / 1000000 && microseconds is >= 0 and < 1000000
            ? seconds * 1000000 + microseconds : null;
    }

    [DllImport("/usr/lib/libSystem.B.dylib")]
    private static extern int sysctl([In] int[] name, uint nameLength, [Out] byte[]? oldValue,
        ref nuint oldLength, IntPtr newValue, nuint newLength);

    [DllImport("/usr/lib/libproc.dylib")]
    private static extern int proc_pidpath(int pid, [Out] byte[] buffer, uint bufferSize);
}
