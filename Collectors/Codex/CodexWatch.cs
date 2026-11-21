using System.Threading.Channels;
using Orisumi.Capture.Storage;

namespace Orisumi.Collectors.Codex;

public static partial class CodexCollector
{
    public static Task Watch(string store, CancellationToken cancellation) =>
        Watch(store, cancellation, TimeSpan.FromSeconds(60));

    internal static async Task Watch(string store, CancellationToken cancellation, TimeSpan reconcileInterval)
    {
        var source = SourceHome();
        using var writer = new CaptureWriter(store, Collector);
        var changes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
        FileSystemWatcher? watcher = null;
        var interrupted = 0;

        bool IsSource(string path)
        {
            var relative = Path.GetRelativePath(source, path);
            if (relative is "state_5.sqlite" or "state_5.sqlite-wal" or "state_5.sqlite-journal") return true;
            return RolloutDirectories.Any(directory => relative == directory ||
                relative.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        }
        void Changed(object sender, FileSystemEventArgs change)
        {
            if (IsSource(change.FullPath)) changes.Writer.TryWrite(true);
        }
        void Interrupt()
        {
            Interlocked.Exchange(ref interrupted, 1);
            changes.Writer.TryWrite(true);
        }
        // The parent watcher wakes the loop when CODEX_HOME is replaced, even if its old watcher is silent.
        var parent = Path.GetDirectoryName(source);
        using var parentWatcher = Directory.Exists(parent) ? new FileSystemWatcher(parent!, Path.GetFileName(source))
        {
            NotifyFilter = NotifyFilters.DirectoryName,
        } : null;
        if (parentWatcher is not null)
        {
            parentWatcher.Created += (_, _) => Interrupt();
            parentWatcher.Deleted += (_, _) => Interrupt();
            parentWatcher.Renamed += (_, _) => Interrupt();
            parentWatcher.Error += (_, _) => Interrupt();
            parentWatcher.EnableRaisingEvents = true;
        }
        var clock = TimeProvider.System;
        var lastReconciled = clock.GetTimestamp();
        var reconcile = true;
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                if (Interlocked.Exchange(ref interrupted, 0) != 0 || watcher is null || !Directory.Exists(source))
                {
                    reconcile = true;
                    watcher?.Dispose(); watcher = null;
                    if (Directory.Exists(source))
                    {
                        watcher = new FileSystemWatcher(source)
                        {
                            IncludeSubdirectories = true,
                            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                        };
                        watcher.Changed += Changed; watcher.Created += Changed; watcher.Deleted += Changed;
                        watcher.Renamed += (_, change) =>
                        {
                            if (IsSource(change.OldFullPath) || IsSource(change.FullPath)) changes.Writer.TryWrite(true);
                        };
                        watcher.Error += (_, _) => Interrupt();
                        watcher.EnableRaisingEvents = true;
                    }
                }
                var failed = false;
                var started = clock.GetTimestamp();
                reconcile |= clock.GetElapsedTime(lastReconciled, started) >= reconcileInterval;
                try
                {
                    Console.WriteLine(Capture(store, writer, source, reconcile, cancellation));
                    if (reconcile) lastReconciled = started;
                    reconcile = false;
                }
                catch (IOException error)
                {
                    failed = true;
                    Console.Error.WriteLine($"Codex capture failed: {error.Message}");
                }
                using var wake = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                // Notifications do not move the deadline for full prefix reconciliation.
                var wait = failed ? TimeSpan.FromSeconds(5) : reconcileInterval - clock.GetElapsedTime(lastReconciled);
                if (wait <= TimeSpan.Zero) continue;
                wake.CancelAfter(wait);
                try
                {
                    await changes.Reader.ReadAsync(wake.Token);
                    await Task.Delay(150, cancellation);
                    while (changes.Reader.TryRead(out _)) { }
                }
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error) { writer.Stop(error); throw; }
        finally { watcher?.Dispose(); }
    }
}
