using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Orisumi.Desktop;

internal static class LaunchCommand
{
    internal const string Name = "launch";
    internal const string Usage = "Usage: orisumi launch -- <executable> [args…]";

    [SupportedOSPlatform("macos")]
    internal static async Task<int> Run(string[] arguments)
    {
        if (arguments is not ["--", { Length: > 0 } executable, .. var childArguments])
            throw new ArgumentException(Usage);

        using var child = new Process
        {
            StartInfo = new ProcessStartInfo(executable, childArguments) { UseShellExecute = false },
        };
        var gate = new Lock();
        var started = false;
        var registrations = new List<PosixSignalRegistration>(4);
        try
        {
            lock (gate)
            {
                foreach (var signal in new[] { PosixSignal.SIGTERM, PosixSignal.SIGINT, PosixSignal.SIGHUP, PosixSignal.SIGQUIT })
                    registrations.Add(Forward(signal));
                if (!child.Start()) throw new IOException($"无法启动 {executable}。");
                started = true;
            }
            await child.WaitForExitAsync();
            return child.ExitCode;
        }
        finally
        {
            foreach (var registration in registrations) registration.Dispose();
        }

        PosixSignalRegistration Forward(PosixSignal signal) => PosixSignalRegistration.Create(signal, context =>
        {
            context.Cancel = true;
            lock (gate)
            {
                if (started && !child.HasExited)
                    child.Signal(signal);
            }
        });
    }
}
