using System.Text.Json;
using Microsoft.Data.Sqlite;
using Orisumi.Capture.Storage;

namespace Orisumi.Collectors.Claude;

// Tracks Claude's project files and their source IDs across moves and replacements.
internal sealed class TranscriptReader(SqliteConnection connection, SqliteTransaction transaction, long runId,
    CancellationToken stop)
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
            .SelectMany(ProjectLogs).ToArray())
            .Order(StringComparer.Ordinal)
            .Select(path => (Path: path, Identity: JsonlReader.Identify(path))).DistinctBy(file => file.Identity).ToArray();
        // A path replacement may also be a rotation; inspect all paths before reusing its stream identity.
        if (changedPaths is not null && files.Any(file => !cursors.Any(cursor => cursor.File.Identity == file.Identity)))
        {
            Read(projects, null, observe);
            return;
        }
        var identities = files.Select(file => file.Identity).ToHashSet(StringComparer.Ordinal);
        foreach (var file in files)
        {
            stop.ThrowIfCancellationRequested();
            var path = file.Path;
            using var reader = new JsonlReader(path, stop: stop);
            var identity = reader.Identity;
            if (identity != file.Identity) throw new IOException($"Claude transcript was replaced during enumeration: {path}");
            var previous = cursors.FirstOrDefault(cursor => cursor.File.Identity == identity)
                ?? cursors.FirstOrDefault(cursor => cursor.File.Path == path && !identities.Contains(cursor.File.Identity));
            var sourceId = previous?.SourceId ?? Guid.NewGuid().ToString("N");
            if (!seen.Add(sourceId)) continue;
            var plan = reader.Plan(previous?.File);
            string? reason = plan.Resets ? plan.Change.ToString().ToLowerInvariant()
                : !previous!.Present ? "resumed"
                : previous.File.Path != path ? "moved" : null;
            if (plan.Change == JsonlChange.Unchanged && reason is null) continue;
            var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
            int NextOccurrence(string hash)
            {
                if (!occurrences.TryGetValue(hash, out var count))
                    count = plan.Resets ? 0 : Convert.ToInt32(Execute("SELECT COALESCE(MAX(hash_occurrence),0) FROM source_records WHERE source_id=$p0 AND record_hash=$p1", sourceId, hash));
                return occurrences[hash] = count + 1;
            }
            JsonlCursor cursor;
            if (plan.Change == JsonlChange.Unchanged)
            {
                cursor = previous!.File with { Path = path };
            }
            else
            {
                var read = reader.Read(plan, (number, line) =>
                {
                    if (string.IsNullOrWhiteSpace(line)) return;
                    try
                    {
                        using var document = JsonDocument.Parse(line);
                        if (document.RootElement.ValueKind != JsonValueKind.Object)
                            throw new InvalidDataException($"Claude transcript record is not an object: {path}:{number}");
                        RecordsRead++;
                        observe(sourceId, path, number, line, document.RootElement, NextOccurrence);
                    }
                    catch (JsonException error) { throw new InvalidDataException($"Invalid Claude transcript JSON: {path}:{number}", error); }
                });
                cursor = read.Cursor;
                BytesRead += read.BytesRead;
            }
            if (reason is not null) FileEvent(sourceId, path, reason, identity, previous?.File.Offset ?? 0, cursor.Length);
            Execute("""
                INSERT INTO _source_cursors VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7,$p8,1)
                ON CONFLICT(source_id) DO UPDATE SET file_identity=excluded.file_identity,source_path=excluded.source_path,
                byte_offset=excluded.byte_offset,line_number=excluded.line_number,observed_length=excluded.observed_length,
                modified_ticks=excluded.modified_ticks,prefix_hash=excluded.prefix_hash,tail_hash=excluded.tail_hash,present=1;
                """, sourceId, identity, path, cursor.Offset, cursor.LineNumber, cursor.Length, cursor.Modified, cursor.Prefix, cursor.Tail);
        }
        foreach (var missing in cursors.Where(cursor => changedPaths is null && cursor.Present && !seen.Contains(cursor.SourceId)))
        {
            FileEvent(missing.SourceId, missing.File.Path, "missing", null, missing.File.Offset, null);
            Execute("UPDATE _source_cursors SET present=0 WHERE source_id=$p0", missing.SourceId);
        }
    }

    private static IEnumerable<string> ProjectLogs(string project) =>
        Directory.EnumerateFiles(project, "*.jsonl").Concat(Directory.EnumerateDirectories(project)
            .Select(session => Path.Combine(session, "subagents"))
            .Where(Directory.Exists)
            .SelectMany(subagents => Directory.EnumerateFiles(subagents, "*.jsonl")));

    internal static bool IsTranscript(string projects, string path)
    {
        if (!path.EndsWith(".jsonl", StringComparison.Ordinal)) return false;
        var parts = Path.GetRelativePath(projects, path).Split(Path.DirectorySeparatorChar);
        return parts.Length == 2 || parts is [_, _, "subagents", _];
    }

    internal static (string? SessionId, string? AgentId) AgentSource(string projects, string path)
    {
        var parts = Path.GetRelativePath(projects, path).Split(Path.DirectorySeparatorChar);
        if (parts is not [_, var sessionId, "subagents", var name]) return default;
        var id = Path.GetFileNameWithoutExtension(name);
        return (sessionId, id.StartsWith("agent-", StringComparison.Ordinal) ? id["agent-".Length..] : id);
    }

    private List<Cursor> LoadCursors()
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT source_id,file_identity,source_path,byte_offset,line_number,observed_length,modified_ticks,prefix_hash,tail_hash,present FROM _source_cursors";
        using var reader = command.ExecuteReader();
        var cursors = new List<Cursor>();
        while (reader.Read()) cursors.Add(new(reader.GetString(0), new(reader.GetString(1), reader.GetString(2), reader.GetInt64(3),
            reader.GetInt32(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetString(7), reader.GetString(8)), reader.GetBoolean(9)));
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

    private sealed record Cursor(string SourceId, JsonlCursor File, bool Present);
}
