using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Orisumi.Platform;

public static partial class RunProcess
{
    private const string DotnetFileName = "dotnet";

    internal static partial IDisposable RegisterTermination(CancellationTokenSource cancellation) =>
        PosixSignalRegistration.Create(PosixSignal.SIGTERM, signal =>
        {
            signal.Cancel = true;
            cancellation.Cancel();
        });

    private static partial Task WaitForChildShutdown(Process process, Task exited)
    {
        process.Signal(PosixSignal.SIGTERM);
        return Task.WhenAny(exited, Task.Delay(TimeSpan.FromSeconds(5)));
    }
}
