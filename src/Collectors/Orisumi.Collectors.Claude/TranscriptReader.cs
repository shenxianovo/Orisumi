using System.Text.Json;
using Microsoft.Data.Sqlite;
using Orisumi.Collectors.Storage;

namespace Orisumi.Collectors.Claude;

// Cursor checks cover append-only transcripts, replacement, truncation and edits at
// the saved checkpoints. Arbitrary edits inside an appended prefix require a rescan.
internal sealed class TranscriptReader(SqliteConnection connection, SqliteTransaction transaction, long runId)
{
    internal long BytesRead { get; private set; }
    internal long RecordsRead { get; private set; }
    internal delegate void Observe(string sourceId, string path, int lineNumber, string line,
        JsonElement row, Func<string, int> nextOccurrence);

    internal void Read(string projects, IReadOnlyCollection<string>? changedPaths, Observe observe)
    {
        var cursors = LoadCursors();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        // File order is deterministic; it does not claim a cross-file event order.
        if (changedPaths?.Any(path => !File.Exists(path)) == true) changedPaths = null;
        var files = (changedPaths ?? Directory.EnumerateDirectories(projects)
            .SelectMany(project => Directory.EnumerateFiles(project, "*.jsonl")).ToArray())
            .Order(StringComparer.Ordinal)
            .Select(path =>
            {
                using var stream = AppendOnlyFile.Open(path);
                return (Path: path, Identity: AppendOnlyFile.Identity(stream));
            }).DistinctBy(file => file.Identity).ToArray();
        // A path replacement may also be a rotation; inspect all paths before reusing its stream identity.
        if (changedPaths is not null && files.Any(file => !cursors.Any(cursor => cursor.Identity == file.Identity)))
        {
            Read(projects, null, observe);
            return;
        }
        var identities = files.Select(file => file.Identity).ToHashSet(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var path = file.Path;
            using var stream = AppendOnlyFile.Open(path);
            var identity = AppendOnlyFile.Identity(stream);
            if (identity != file.Identity) throw new IOException($"Claude transcript was replaced during enumeration: {path}");
            var length = stream.Length;
            var modified = File.GetLastWriteTimeUtc(stream.SafeFileHandle).Ticks;
            var previous = cursors.FirstOrDefault(cursor => cursor.Identity == identity)
                ?? cursors.FirstOrDefault(cursor => cursor.Path == path && !identities.Contains(cursor.Identity));
            var sourceId = previous?.SourceId ?? Guid.NewGuid().ToString("N");
            if (!seen.Add(sourceId)) continue;
            var offset = previous?.Offset ?? 0;
            var lineNumber = previous?.LineNumber ?? 0;
            string? reason = previous is null ? "initial"
                : previous.Identity != identity ? "replaced"
                : length < offset ? "truncated"
                : !previous.Present ? "resumed"
                : previous.Path != path ? "moved" : null;
            var unchanged = previous is not null && previous.Identity == identity &&
                previous.Length == length && previous.Modified == modified;
            var reset = previous is null || reason is "replaced" or "truncated";
            if (!unchanged && !reset && previous is not null)
            {
                if (AppendOnlyFile.Checkpoint(stream, offset, true) != previous.Prefix ||
                    AppendOnlyFile.Checkpoint(stream, offset, false) != previous.Tail)
                {
                    reset = true;
                    reason = "rewritten";
                }
                else if (length == previous.Length)
                {
                    reset = true;
                    reason = "rechecked";
                }
            }
            if (reason is not null) FileEvent(sourceId, path, reason, identity, offset, length);
            if (unchanged && reason is null) continue;
            if (reset) { offset = 0; lineNumber = 0; }
            var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
            int NextOccurrence(string hash)
            {
                if (!occurrences.TryGetValue(hash, out var count))
                    count = reset ? 0 : Convert.ToInt32(Execute("SELECT COALESCE(MAX(hash_occurrence),0) FROM source_records WHERE source_id=$p0 AND record_hash=$p1", sourceId, hash));
                return occurrences[hash] = count + 1;
            }
            if (!unchanged || reset)
            {
                BytesRead += length - offset;
                foreach (var (line, end) in AppendOnlyFile.Lines(stream, offset, length))
                {
                    var number = ++lineNumber;
                    offset = end;
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        using var document = JsonDocument.Parse(line);
                        if (document.RootElement.ValueKind != JsonValueKind.Object)
                            throw new InvalidDataException($"Claude transcript record is not an object: {path}:{number}");
                        RecordsRead++;
                        observe(sourceId, path, number, line, document.RootElement, NextOccurrence);
                    }
                    catch (JsonException error) { throw new InvalidDataException($"Invalid Claude transcript JSON: {path}:{number}", error); }
                }
            }
            var prefixHash = AppendOnlyFile.Checkpoint(stream, offset, true);
            var tailHash = AppendOnlyFile.Checkpoint(stream, offset, false);
            Execute("""
                INSERT INTO _source_cursors VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7,$p8,1)
                ON CONFLICT(source_id) DO UPDATE SET file_identity=excluded.file_identity,source_path=excluded.source_path,
                byte_offset=excluded.byte_offset,line_number=excluded.line_number,observed_length=excluded.observed_length,
                modified_ticks=excluded.modified_ticks,prefix_hash=excluded.prefix_hash,tail_hash=excluded.tail_hash,present=1;
                """, sourceId, identity, path, offset, lineNumber, length, modified, prefixHash, tailHash);
        }
        foreach (var missing in cursors.Where(cursor => changedPaths is null && cursor.Present && !seen.Contains(cursor.SourceId)))
        {
            FileEvent(missing.SourceId, missing.Path, "missing", null, missing.Offset, null);
            Execute("UPDATE _source_cursors SET present=0 WHERE source_id=$p0", missing.SourceId);
        }
    }

    private List<Cursor> LoadCursors()
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT source_id,file_identity,source_path,byte_offset,line_number,observed_length,modified_ticks,prefix_hash,tail_hash,present FROM _source_cursors";
        using var reader = command.ExecuteReader();
        var cursors = new List<Cursor>();
        while (reader.Read()) cursors.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3),
            reader.GetInt32(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetString(7), reader.GetString(8), reader.GetBoolean(9)));
        return cursors;
    }

    private void FileEvent(string sourceId, string path, string kind, string? identity, long offset, long? length) =>
        Execute("INSERT INTO source_file_events(run_id,source_id,source_path,observed_at_utc_ms,kind,file_identity,previous_offset,observed_length) VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7)",
            runId, sourceId, path, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), kind, identity, offset, length);

    private object? Execute(string sql, params object?[] values)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        return command.Scalar(sql, values);
    }

    private sealed record Cursor(string SourceId, string Identity, string Path, long Offset, int LineNumber,
        long Length, long Modified, string Prefix, string Tail, bool Present);
}
