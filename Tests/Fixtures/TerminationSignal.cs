using System.Diagnostics;
using System.Runtime.InteropServices;

internal static class TerminationSignal
{
    internal static bool Send(Process process)
    {
        if (!OperatingSystem.IsWindows()) return process.Signal(PosixSignal.SIGTERM);
        if (!EventWaitHandle.TryOpenExisting($@"Local\Orisumi.Stop.{process.Id}", out var stop)) return false;
        using (stop) return stop.Set();
    }
}
