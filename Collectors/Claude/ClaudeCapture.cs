using System.Threading.Channels;
using Orisumi.Capture.Storage;
using Orisumi.Platform;

namespace Orisumi.Collectors.Claude;

public static partial class ClaudeCollector
{
    // One capture round, for tests; the resident Watch holds the writer for its lifetime.
    public static string Capture(string store)
    {
        using var writer = new CaptureWriter(store, Collector);
        return Capture(store, writer, null);
    }

    private static string Capture(string store, CaptureWriter writer, IReadOnlyCollection<string>? changedPaths,
        CancellationToken stop = default) =>
        writer.Run((command, runId) =>
        {
            var projects = Projects();
            if (!Directory.Exists(projects)) throw new DirectoryNotFoundException($"Claude Code projects directory not found: {projects}");
            var written = Import(command.Connection!, command.Transaction!, runId, projects, changedPaths, stop, out var bytes, out var records);
            return $"Claude Code: {written} source records captured in {new Store(store).CurrentDatabase(Source)}, " +
                $"{records} records parsed, {bytes} transcript bytes read";
        });

    public static async Task Watch(string store, CancellationToken cancellation)
    {
        var projects = Projects();
        using var writer = new CaptureWriter(store, Collector);
        var signals = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
        var home = Path.GetDirectoryName(projects)!;
        var gate = new Lock();
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var scan = true;
        var rebind = true;
        FileSystemWatcher? watcher = null;
        FileSystemWatcher? parentWatcher = null;

        void RequestScan(bool bind = false)
        {
            lock (gate) { scan = true; rebind |= bind; }
            signals.Writer.TryWrite(true);
        }
        void Changed(object sender, FileSystemEventArgs change)
        {
            if (change.FullPath != projects && !change.FullPath.StartsWith(projects + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return;
            if (change.ChangeType == WatcherChangeTypes.Changed)
            {
                if (!TranscriptReader.IsTranscript(projects, change.FullPath)) return;
                lock (gate) paths.Add(change.FullPath);
            }
            else { lock (gate) scan = true; }
            signals.Writer.TryWrite(true);
        }
        void Bind()
        {
            watcher?.Dispose(); watcher = null;
            parentWatcher?.Dispose(); parentWatcher = null;
            var parent = Path.GetDirectoryName(home)!;
            if (Directory.Exists(parent))
            {
                parentWatcher = new FileSystemWatcher(parent, Path.GetFileName(home)) { NotifyFilter = NotifyFilters.DirectoryName };
                parentWatcher.Created += (_, _) => RequestScan(true);
                parentWatcher.Deleted += (_, _) => RequestScan(true);
                parentWatcher.Renamed += (_, _) => RequestScan(true);
                parentWatcher.Error += (_, _) => RequestScan(true);
                parentWatcher.EnableRaisingEvents = true;
            }
            if (!Directory.Exists(home)) return;
            watcher = new FileSystemWatcher(home)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            watcher.Changed += Changed; watcher.Created += Changed; watcher.Deleted += Changed;
            watcher.Renamed += (sender, change) =>
            {
                Changed(sender, change);
                if (change.OldFullPath.StartsWith(projects, StringComparison.Ordinal)) RequestScan();
            };
            watcher.Error += (_, _) => RequestScan(true);
            watcher.EnableRaisingEvents = true;
        }

        signals.Writer.TryWrite(true);
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                using var reconcile = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                reconcile.CancelAfter(TimeSpan.FromMinutes(5));
                try { await signals.Reader.ReadAsync(reconcile.Token); }
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { RequestScan(true); }
                await Task.Delay(100, cancellation);
                while (signals.Reader.TryRead(out _)) { }
                string[]? changed;
                bool bind;
                lock (gate)
                {
                    changed = scan ? null : paths.ToArray();
                    bind = rebind;
                    scan = false; rebind = false; paths.Clear();
                }
                try
                {
                    if (bind) Bind();
                    Console.WriteLine(Capture(store, writer, changed, cancellation));
                }
                catch (IOException error)
                {
                    Console.Error.WriteLine(error.Message);
                    await Task.Delay(TimeSpan.FromSeconds(5), cancellation);
                    RequestScan(true);
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error) { writer.Stop(error); throw; }
        finally { watcher?.Dispose(); parentWatcher?.Dispose(); }
    }

    private static string Projects()
    {
        var home = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        return Path.GetFullPath(Path.Combine(string.IsNullOrWhiteSpace(home)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude") : home, "projects"));
    }
}
