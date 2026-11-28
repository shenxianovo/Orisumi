using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Orisumi.Capture.Hosting;
using Orisumi.Core;

namespace Orisumi.Capture.Storage;

// One run and one writer lock for the lifetime of a resident collector or browser profile connection.
public sealed class CaptureWriter : IDisposable
{
    private readonly string source;
    private readonly FileStream writerLock;
    private readonly SqliteConnection connection;
    private readonly long runId;
    private bool stopped;

    public CaptureWriter(string store, Collector collector, Action<SqliteCommand, long>? configure = null, string? lockIdentity = null)
    {
        source = collector.Source;
        writerLock = Lock(store, lockIdentity is null ? source : source + "." + lockIdentity);
        try
        {
            connection = CollectorDatabase.Open(store, collector);
            try
            {
                using var command = connection.CreateCommand();
                using var start = connection.BeginTransaction();
                command.Transaction = start;
                runId = (long)command.Scalar("""
                    INSERT INTO capture_runs(started_at_utc_ms,host_observation_id,collector_version)
                    VALUES($p0,$p1,$p2) RETURNING run_id
                    """, Now(), HostObservation.Record(command, store), collector.Version)!;
                configure?.Invoke(command, runId);
                start.Commit();
            }
            catch { connection.Dispose(); throw; }
        }
        catch { writerLock.Dispose(); throw; }
    }

    public T Run<T>(Func<SqliteCommand, long, T> capture, (string Alias, string Path)[]? readOnlySources = null,
        long? observedThrough = null, bool confirm = true, string? failureScope = null)
    {
        ObjectDisposedException.ThrowIf(stopped, this);
        try
        {
            // Attachments and their temporary views belong only to this round.
            using var scoped = readOnlySources is null ? null : new SqliteConnection(connection.ConnectionString);
            scoped?.Open();
            var database = scoped ?? connection;
            using var command = database.CreateCommand();
            foreach (var (alias, path) in readOnlySources ?? [])
            {
                if (!File.Exists(path)) throw new FileNotFoundException($"{source} source database not found: {path}");
                command.Execute($"ATTACH DATABASE $p0 AS {alias}", new Uri(Path.GetFullPath(path)).AbsoluteUri + "?mode=ro");
            }
            using var round = database.BeginTransaction();
            command.Transaction = round;
            var result = capture(command, runId);
            if (confirm)
                command.Execute("UPDATE capture_runs SET confirmed_until_utc_ms=$p0 WHERE run_id=$p1", observedThrough ?? Now(), runId);
            round.Commit();
            return result;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // The storage failure may also prevent writing its diagnostic.
            try { RecordReadFailure(error, failureScope); }
            catch (Exception) { }
            // Source and storage failures surface as IOException so a resident loop can retry.
            if (error is SqliteException or InvalidDataException or UnauthorizedAccessException or DecoderFallbackException or JsonException)
                throw new IOException($"could not capture {source}: {error.Message}", error);
            throw;
        }
    }

    public void RecordReadFailure(Exception error, string? scope = null)
    {
        using var command = connection.CreateCommand();
        command.Execute("INSERT INTO capture_read_failures(run_id,observed_at_utc_ms,error,scope) VALUES($p0,$p1,$p2,$p3)",
            runId, Now(), error.Message, scope);
    }

    public void Stop(Exception? error = null)
    {
        if (stopped) return;
        stopped = true;
        using var command = connection.CreateCommand();
        command.Execute("UPDATE capture_runs SET stopped_at_utc_ms=$p0,stop_reason=$p1,error=$p2 WHERE run_id=$p3",
            Now(), error is null ? "completed" : "failed", error?.Message, runId);
    }

    public void Dispose()
    {
        try { Stop(); }
        finally
        {
            connection.Dispose();
            writerLock.Dispose();
        }
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static FileStream Lock(string store, string source)
    {
        var path = new Store(store).LockFile(source + ".writer");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException error) { throw new IOException($"{source} already has a capture writer for {Path.GetFullPath(store)}", error); }
    }
}
