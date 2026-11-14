using System.Runtime.InteropServices;

namespace Orisumi.Cli.Capture;

internal static class CaptureLifetime
{
    internal static async Task Run(Func<CancellationToken, Task> watch, CancellationToken cancellation)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        using var termination = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGTERM, signal =>
        {
            signal.Cancel = true;
            stop.Cancel();
        });
        try { await watch(stop.Token); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }
}
