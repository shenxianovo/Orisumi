using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Orisumi.Platform;

public static partial class RunProcess
{
    private const string DotnetFileName = "dotnet.exe";

    internal static partial IDisposable RegisterTermination(CancellationTokenSource cancellation) =>
        PosixSignalRegistration.Create(PosixSignal.SIGTERM, _ => cancellation.Cancel());

    // Console children receive Ctrl+C directly from Windows. Give them time to flush before forced exit.
    private static partial Task WaitForChildShutdown(Process process, Task exited) =>
        Task.WhenAny(exited, Task.Delay(TimeSpan.FromSeconds(5)));
}
