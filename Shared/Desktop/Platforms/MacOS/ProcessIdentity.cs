using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Orisumi.Platform;

[SupportedOSPlatform("macos")]
internal sealed record ProcessIdentity(int Pid, long Started)
{
    internal static ProcessIdentity? Of(int pid)
    {
        // PROC_PIDTBSDINFO gives the identity and zombie state in one kernel read.
        if (pid <= 0 || proc_pidinfo(pid, 3, 0, out var info, 136) != 136 || info.Status == 5) return null;
        return new(pid, info.StartSeconds * 1_000_000 + info.StartMicroseconds);
    }

    internal async Task Wait(CancellationToken token)
    {
        if (Of(Pid) != this) return;
        var timeout = Stopwatch.StartNew();
        while (Of(Pid) == this)
        {
            if (timeout.Elapsed > TimeSpan.FromSeconds(30)) throw new IOException($"采集进程 {Pid} 尚未退出。");
            await Task.Delay(100, token);
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = 136)]
    private struct DarwinProcess
    {
        [FieldOffset(4)] internal int Status;
        [FieldOffset(120)] internal long StartSeconds;
        [FieldOffset(128)] internal long StartMicroseconds;
    }

    [DllImport("/usr/lib/libSystem.B.dylib")]
    private static extern int proc_pidinfo(int pid, int flavor, ulong argument, out DarwinProcess buffer, int size);
}
