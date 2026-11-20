using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Orisumi.Platform;

internal sealed record ProcessIdentity(int Pid, long Started)
{
    internal static ProcessIdentity? Of(int pid)
    {
        // PROC_PIDTBSDINFO gives the identity and zombie state in one kernel read.
        if (pid <= 0 || proc_pidinfo(pid, 3, 0, out var info, 136) != 136 || info.Status == 5) return null;
        return new(pid, info.StartSeconds * 1_000_000 + info.StartMicroseconds);
    }

    internal void Write(string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, string.Create(CultureInfo.InvariantCulture, $"{Pid} {Started}"));
    }

    internal static ProcessIdentity? Read(string file)
    {
        try
        {
            return File.ReadAllText(file).Split(' ') is [var pid, var started] &&
                int.TryParse(pid, CultureInfo.InvariantCulture, out var number) &&
                long.TryParse(started, CultureInfo.InvariantCulture, out var ticks) ? new(number, ticks) : null;
        }
        catch (FileNotFoundException) { return null; }
    }

    internal async Task Wait(CancellationToken token, bool terminate = false)
    {
        if (Of(Pid) != this) return;
        // A process that exits meanwhile is not found or not signalled.
        if (terminate && Process.TryGetProcessById(Pid, out var process))
            using (process) process.Signal(PosixSignal.SIGTERM);
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
