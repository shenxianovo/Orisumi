using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Orisumi.Collectors.Storage;

// The only writer of one source in one store while it exists; a second one fails instead of waiting.
// Each capture round is a capture_runs row: started with the host observation, completed in the round's
// transaction together with its facts, or marked failed with the error after the facts are rolled back.
internal sealed class CaptureWriter(string store, string source) : IDisposable
{
    private readonly FileStream writerLock = Lock(store, source);

    // columns are the source's own run columns known at start; readOnlySources are attached by alias for the round.
    internal T Run<T>(Func<SqliteCommand, long, T> capture, (string Column, object? Value)[]? columns = null,
        (string Alias, string Path)[]? readOnlySources = null)
    {
        using var connection = CollectorDatabase.Open(store, source);
        using var command = connection.CreateCommand();
        long runId;
        using (var start = connection.BeginTransaction())
        {
            command.Transaction = start;
            columns ??= [];
            object?[] values = [DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), HostObservation.Record(command, store), .. columns.Select(column => column.Value)];
            var names = string.Concat(columns.Select(column => "," + column.Column));
            var parameters = string.Join(',', Enumerable.Range(0, values.Length).Select(index => $"$p{index}"));
            runId = (long)command.Scalar($"INSERT INTO capture_runs(started_at_utc_ms,host_observation_id{names}) VALUES({parameters}) RETURNING run_id", values)!;
            start.Commit();
        }
        try
        {
            foreach (var (alias, path) in readOnlySources ?? [])
            {
                if (!File.Exists(path)) throw new FileNotFoundException($"{source} source database not found: {path}");
                command.Transaction = null;
                command.Execute($"ATTACH DATABASE $p0 AS {alias}", new Uri(Path.GetFullPath(path)).AbsoluteUri + "?mode=ro");
            }
            using var round = connection.BeginTransaction();
            command.Transaction = round;
            var result = capture(command, runId);
            command.Transaction = round;
            command.Execute("UPDATE capture_runs SET stopped_at_utc_ms=$p0,stop_reason='completed' WHERE run_id=$p1",
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), runId);
            round.Commit();
            return result;
        }
        catch (Exception error)
        {
            command.Transaction = null;
            command.Execute("UPDATE capture_runs SET stopped_at_utc_ms=$p0,stop_reason='failed',error=$p1 WHERE run_id=$p2",
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), error.Message, runId);
            // Source and storage failures surface as IOException so a resident loop can retry; other errors are bugs.
            if (error is SqliteException or InvalidDataException or UnauthorizedAccessException or DecoderFallbackException or JsonException)
                throw new IOException($"could not capture {source}: {error.Message}", error);
            throw;
        }
    }

    public void Dispose() => writerLock.Dispose();

    private static FileStream Lock(string store, string source)
    {
        var path = CollectorDatabase.PathFor(store, source) + ".capture.lock";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException error) { throw new IOException($"{source} already has a capture writer for {Path.GetFullPath(store)}", error); }
    }
}
