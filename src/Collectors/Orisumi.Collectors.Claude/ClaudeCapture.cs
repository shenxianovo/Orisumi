using System.Threading.Channels;
using System.Text;
using Microsoft.Data.Sqlite;
using Orisumi.Collectors.Storage;

namespace Orisumi.Collectors.Claude;

public static partial class ClaudeCollector
{
    // One capture round, for tests; the resident Watch holds the writer for its lifetime.
    public static string Capture(string store)
    {
        using var writer = new CaptureWriter(store, Source);
        return Capture(store, writer, null);
    }

    private static string Capture(string store, CaptureWriter writer, IReadOnlyCollection<string>? changedPaths) =>
        writer.Run((command, runId) =>
        {
            var projects = Projects();
            if (!Directory.Exists(projects)) throw new DirectoryNotFoundException($"Claude Code projects directory not found: {projects}");
            var written = Import(command.Connection!, command.Transaction!, runId, projects, changedPaths, out var bytes, out var records);
            command.Execute("UPDATE capture_runs SET bytes_read=$p0,records_read=$p1 WHERE run_id=$p2", bytes, records, runId);
            return $"Claude Code: {written} source records captured in {CollectorDatabase.PathFor(store, Source)}";
        });

    public static async Task Watch(string store, CancellationToken cancellation)
    {
        using var writer = new CaptureWriter(store, Source);
        var signals = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
        var projects = Projects();
        var home = Path.GetDirectoryName(projects)!;
        var gate = new object();
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
                // Only direct project JSONL files belong to the main-session source.
                if (!change.FullPath.EndsWith(".jsonl", StringComparison.Ordinal) ||
                    Path.GetDirectoryName(Path.GetDirectoryName(change.FullPath)) != projects) return;
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
                    Console.WriteLine(Capture(store, writer, changed));
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
        finally { watcher?.Dispose(); parentWatcher?.Dispose(); }
    }

    private static string Projects()
    {
        var home = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        return Path.GetFullPath(Path.Combine(string.IsNullOrWhiteSpace(home)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude") : home, "projects"));
    }
}
