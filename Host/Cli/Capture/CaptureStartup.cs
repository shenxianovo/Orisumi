using System.Runtime.InteropServices;
using Orisumi.Desktop;
using Orisumi.Platform;

namespace Orisumi.Cli.Capture;

internal static class CaptureStartup
{
    internal static async Task Start(Instance instance, ICollectorJobs jobs, TextWriter output, CancellationToken token,
        string? baseDirectory = null)
    {
        var store = instance.Store;
        using var guard = Store.WaitLock(store.AppLock);
        var programs = await CollectorPrograms.Read(store.Root, token, baseDirectory);
        if (programs.Any(program => program.Description.Name == "browser"))
            ChromeExtension.CheckConfiguration(ChromeExtension.DirectoryFor(instance), instance);
        await jobs.StopAll(token);

        try
        {
            foreach (var program in programs)
            {
                var registration = await jobs.Set(program.Description.Name, program.Job(store), token);
                output.WriteLine($"Started {registration}");
            }
        }
        catch
        {
            try { await jobs.StopAll(CancellationToken.None); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or COMException) { await output.WriteLineAsync($"Capture cleanup: {error.Message}"); }
            throw;
        }
    }

    internal static async Task Stop(Instance instance, ICollectorJobs jobs, TextWriter output, CancellationToken token)
    {
        using var guard = Store.WaitLock(instance.Store.AppLock);
        await jobs.StopAll(token);
        output.WriteLine($"Stopped capture for {instance.Name}");
    }
}
