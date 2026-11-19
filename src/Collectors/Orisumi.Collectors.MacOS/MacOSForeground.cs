using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Orisumi.Platform;

namespace Orisumi.Collectors.MacOS;

public static class MacOSForeground
{
    public static async Task<int> Run(string store, int? seconds, bool captureWindows, int? afkThresholdSeconds,
        bool captureInput, bool captureMicrophone, bool captureAudioOutput, bool captureCamera)
    {
        var helper = Path.Combine(AppContext.BaseDirectory, "orisumi-macos-foreground");
        var location = new Store(store);
        var (database, _) = location.WritableDatabase("macos");
        var start = new ProcessStartInfo(helper)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
        };
        start.ArgumentList.Add(database);
        // The helper records the host it is given instead of reading it again.
        var host = HostIdentity.Read();
        start.ArgumentList.Add("--host=" + JsonSerializer.Serialize(new
        {
            host.Platform, host.HardwareUuid, host.HostName, host.Model, host.OsVersion, host.OsBuild, host.BootId,
            host.UserId, host.UserName, StorePath = location.Root,
        }, JsonSerializerOptions.Web));
        if (seconds is int duration)
            start.ArgumentList.Add(duration.ToString(CultureInfo.InvariantCulture));
        if (captureWindows)
            start.ArgumentList.Add("--windows");
        if (afkThresholdSeconds is int threshold)
            start.ArgumentList.Add($"--afk-threshold={threshold.ToString(CultureInfo.InvariantCulture)}");

        if (captureInput)
            start.ArgumentList.Add("--input");
        if (captureMicrophone)
            start.ArgumentList.Add("--microphone");
        if (captureAudioOutput)
            start.ArgumentList.Add("--audio-output");
        if (captureCamera)
            start.ArgumentList.Add("--camera");

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
