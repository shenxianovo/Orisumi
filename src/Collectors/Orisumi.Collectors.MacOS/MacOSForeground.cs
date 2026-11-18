using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Orisumi.Collectors.MacOS;

public static class MacOSForeground
{
    public static async Task<int> Run(string store, int? seconds, bool captureWindows, int? afkThresholdSeconds, bool captureInput)
    {
        var helper = Path.Combine(AppContext.BaseDirectory, "orisumi-macos-foreground");
        var database = MacOSStore.GetDatabasePath(store);
        Directory.CreateDirectory(Path.GetDirectoryName(database)!);
        var start = new ProcessStartInfo(helper)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
        };
        start.ArgumentList.Add(database);
        if (seconds is int duration)
            start.ArgumentList.Add(duration.ToString(CultureInfo.InvariantCulture));
        if (captureWindows)
            start.ArgumentList.Add("--windows");
        if (afkThresholdSeconds is int threshold)
            start.ArgumentList.Add($"--afk-threshold={threshold.ToString(CultureInfo.InvariantCulture)}");

        if (captureInput)
            start.ArgumentList.Add("--input");

        var interrupted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ConsoleCancelEventHandler onInterrupt = (_, signal) =>
        {
            signal.Cancel = true;
            interrupted.TrySetResult();
        };
        using var termination = PosixSignalRegistration.Create(PosixSignal.SIGTERM, signal =>
        {
            signal.Cancel = true;
            interrupted.TrySetResult();
        });
        Console.CancelKeyPress += onInterrupt;
        try
        {
            using var process = Process.Start(start) ?? throw new IOException("could not start the macOS foreground observer");
            var exited = process.WaitForExitAsync();
            if (await Task.WhenAny(exited, interrupted.Task) == interrupted.Task)
                process.StandardInput.Close();
            await exited;
            return process.ExitCode;
        }
        finally
        {
            Console.CancelKeyPress -= onInterrupt;
        }
    }
}
