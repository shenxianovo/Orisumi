using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Orisumi.Platform;

public static partial class RunProcess
{
    private const string DotnetFileName = "dotnet.exe";
    private static string StopEvent(int pid) => $@"Local\Orisumi.Stop.{pid}";

    internal static partial IDisposable RegisterTermination(CancellationTokenSource cancellation) => new Termination(cancellation);

    private sealed class Termination : IDisposable
    {
        private readonly EventWaitHandle stop = new(false, EventResetMode.ManualReset, StopEvent(Environment.ProcessId));
        private readonly RegisteredWaitHandle wait;
        private readonly PosixSignalRegistration signal;

        internal Termination(CancellationTokenSource cancellation)
        {
            signal = PosixSignalRegistration.Create(PosixSignal.SIGTERM, _ => cancellation.Cancel());
            // Cancel off the wait callback: a continuation that runs inline could reach Dispose, which waits for this callback.
            wait = ThreadPool.RegisterWaitForSingleObject(stop, (_, _) => _ = cancellation.CancelAsync(), null, Timeout.Infinite, true);
        }

        public void Dispose()
        {
            signal.Dispose();
            using var drained = new ManualResetEvent(false);
            if (wait.Unregister(drained)) drained.WaitOne();
            stop.Dispose();
        }
    }

    // Same budget as launchd's ExitTimeOut. Callers that wait for a supervised stop must allow longer than this.
    internal static readonly TimeSpan ChildStopTimeout = TimeSpan.FromSeconds(30);

    internal static async Task StopChild(Process process, Task exited)
    {
        var elapsed = Stopwatch.StartNew();
        while (!exited.IsCompleted && elapsed.Elapsed < ChildStopTimeout)
        {
            // CaptureLifetime may still be initializing when the desired file is removed.
            if (EventWaitHandle.TryOpenExisting(StopEvent(process.Id), out var stop))
            {
                using (stop) stop.Set();
                break;
            }
            await Task.WhenAny(exited, Task.Delay(50));
        }
        var remaining = ChildStopTimeout - elapsed.Elapsed;
        if (remaining > TimeSpan.Zero) await Task.WhenAny(exited, Task.Delay(remaining));
    }

    private static partial Task WaitForChildShutdown(Process process, Task exited) => StopChild(process, exited);
}
