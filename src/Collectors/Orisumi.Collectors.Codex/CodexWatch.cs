using System.Threading.Channels;
using Orisumi.Collectors.Storage;

namespace Orisumi.Collectors.Codex;

public static partial class CodexCollector
{
    public static async Task Watch(string store, string? threadId, CancellationToken cancellation)
    {
        using var captureLock = CollectorDatabase.Lock(store, Source);
        var source = SourceHome();
        var changes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
        FileSystemWatcher? watcher = null;
        var interrupted = 0;
        var changedSources = 0;
        var trigger = "startup";
        int SourceKind(string path)
        {
            var name = Path.GetFileName(path);
            if (Path.GetDirectoryName(path) == source && name is
                "state_5.sqlite" or "state_5.sqlite-wal" or "state_5.sqlite-journal" or
                "thread_history_1.sqlite" or "thread_history_1.sqlite-wal" or "thread_history_1.sqlite-journal") return 1;
            var relative = Path.GetRelativePath(source, path);
            return name.EndsWith(".jsonl", StringComparison.Ordinal) || name.EndsWith(".jsonl.zst", StringComparison.Ordinal) ||
                relative is "sessions" or "archived_sessions" ||
                relative.StartsWith("sessions" + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                relative.StartsWith("archived_sessions" + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? 2 : 0;
        }
        void Signal(int kind)
        {
            if (kind != 0)
            {
                Interlocked.Or(ref changedSources, kind);
                changes.Writer.TryWrite(true);
            }
        }
        void Changed(object sender, FileSystemEventArgs change) => Signal(SourceKind(change.FullPath));
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                if (watcher is null || !Directory.Exists(source) || Interlocked.Exchange(ref interrupted, 0) != 0)
                {
                    watcher?.Dispose(); watcher = null;
                    if (Directory.Exists(source))
                    {
                        watcher = new FileSystemWatcher(source)
                        {
                            IncludeSubdirectories = true,
                            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                        };
                        watcher.Changed += Changed; watcher.Created += Changed; watcher.Deleted += Changed;
                        watcher.Renamed += (_, change) => Signal(SourceKind(change.OldFullPath) | SourceKind(change.FullPath));
                        watcher.Error += (_, _) => { Interlocked.Exchange(ref interrupted, 1); changes.Writer.TryWrite(true); };
                        watcher.EnableRaisingEvents = true;
                    }
                }
                var failed = false;
                try { Console.WriteLine(CaptureCore(store, threadId, trigger)); }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    failed = true;
                    Console.Error.WriteLine($"Codex capture failed: {error.Message}");
                }
                using var wake = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                // Notifications wake normal imports. Reconciliation also discovers missed events
                // and archived/replaced sources; failures retry without a busy loop.
                wake.CancelAfter(TimeSpan.FromSeconds(failed ? 5 : 60));
                try
                {
                    await changes.Reader.ReadAsync(wake.Token);
                    await Task.Delay(150, cancellation);
                    while (changes.Reader.TryRead(out _)) { }
                    var changed = Interlocked.Exchange(ref changedSources, 0);
                    trigger = Volatile.Read(ref interrupted) != 0 ? "watcher_error" : (changed & 1) != 0 ? "change" : "usage_change";
                }
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
                {
                    trigger = failed ? "retry" : "reconcile";
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally { watcher?.Dispose(); }
    }
}
