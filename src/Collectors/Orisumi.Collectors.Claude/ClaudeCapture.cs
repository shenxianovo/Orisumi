using System.Threading.Channels;
using System.Text;
using Microsoft.Data.Sqlite;
using Orisumi.Collectors.Storage;

namespace Orisumi.Collectors.Claude;

public static partial class ClaudeCollector
{
    public static string Capture(string store) => Capture(store, null);

    private static string Capture(string store, IReadOnlyCollection<string>? changedPaths)
    {
        var database = Path.GetFullPath(Path.Combine(store, "raw", "claude.sqlite"));
        using var connection = OpenDatabase(database);
        SqliteSchema.EnsureCreated(connection);
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO capture_runs(started_at_utc_ms) VALUES($at); SELECT last_insert_rowid();";
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var runId = (long)command.ExecuteScalar()!;
        try
        {
            var projects = Projects();
            if (!Directory.Exists(projects)) throw new DirectoryNotFoundException($"Claude Code projects directory not found: {projects}");
            using var transaction = connection.BeginTransaction();
            var written = Import(connection, transaction, runId, projects, changedPaths, out var bytes, out var records);
            command.Transaction = transaction;
            command.Parameters.Clear();
            command.CommandText = "UPDATE capture_runs SET stopped_at_utc_ms=$at,bytes_read=$bytes,records_read=$records WHERE run_id=$run";
            command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$bytes", bytes);
            command.Parameters.AddWithValue("$records", records);
            command.Parameters.AddWithValue("$run", runId);
            command.ExecuteNonQuery();
            transaction.Commit();
            return $"Claude Code: {written} source records captured in {database}";
        }
        catch (Exception error) when (error is IOException or InvalidDataException or DecoderFallbackException or SqliteException or UnauthorizedAccessException)
        {
            command.Transaction = null;
            command.Parameters.Clear();
            command.CommandText = "UPDATE capture_runs SET stopped_at_utc_ms=$at,error=$error WHERE run_id=$run";
            command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$error", error.Message);
            command.Parameters.AddWithValue("$run", runId);
            command.ExecuteNonQuery();
            throw new IOException($"could not capture Claude Code transcripts: {error.Message}", error);
        }
    }

    public static async Task Watch(string store, CancellationToken cancellation)
    {
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
                    Console.WriteLine(Capture(store, changed));
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
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
