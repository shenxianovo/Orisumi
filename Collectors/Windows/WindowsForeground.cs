using System.Threading.Channels;
using Orisumi.Capture.Hosting;
using Orisumi.Capture.Storage;

namespace Orisumi.Collectors.Windows;

internal static class WindowsForeground
{
    internal static readonly Collector Collector = new("windows", typeof(WindowsForeground).Assembly);

    internal static async Task<int> Run(string store, int? seconds, bool windows, bool input, int threshold, CancellationToken cancellation)
    {
        using var lifetime = new CaptureLifetime(cancellation);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        if (seconds is { } duration) stop.CancelAfter(TimeSpan.FromSeconds(duration));
        using var writer = new CaptureWriter(store, Collector, (command, run) => command.Execute(
            "INSERT INTO capture_run_configuration VALUES($p0,$p1,$p2,$p3)", run, threshold * 1000L, windows, input));
        var batches = Channel.CreateUnbounded<Batch>(new() { SingleReader = true, SingleWriter = true });
        var thread = new Thread(() =>
        {
            try
            {
                using var source = new DesktopSource();
                var observations = new WindowsObservations(source, windows, threshold * 1000L, input);
                source.ReadStatus = observations.ReadStatus;
                new DesktopLoop(source, observations, windows, input, batch => batches.Writer.TryWrite(batch)).Run(stop.Token);
                batches.Writer.Complete();
            }
            catch (Exception error) { batches.Writer.Complete(error); }
        }) { Name = "Windows observation" };
        thread.Start();
        try
        {
            await foreach (var batch in batches.Reader.ReadAllAsync()) batch.Commit(writer);
            writer.Stop();
            return 0;
        }
        catch (Exception error)
        {
            writer.Stop(error);
            Console.Error.WriteLine($"orisumi: Windows foreground capture failed: {error.Message}");
            return 1;
        }
        finally { stop.Cancel(); thread.Join(); }
    }
}
