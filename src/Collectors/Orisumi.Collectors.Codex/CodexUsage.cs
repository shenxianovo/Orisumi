using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ZstdSharp;

namespace Orisumi.Collectors.Codex;

internal static class CodexUsage
{
    private sealed record Cursor(string Path, string FileId, long Length, long Modified, long Offset,
        int Line, string Prefix, string Tail, Dictionary<string, string> Models);

    internal static (int Written, int Unavailable, long BytesRead) Capture(SqliteConnection connection, SqliteTransaction transaction,
        string codexHome, IReadOnlyList<(string ThreadId, string Path)> rollouts, long runId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        object? Execute(string sql, params object?[] values)
        {
            command.CommandText = sql; command.Parameters.Clear();
            for (var i = 0; i < values.Length; i++) command.Parameters.AddWithValue($"$p{i}", values[i] ?? DBNull.Value);
            return command.ExecuteScalar();
        }
        var cursors = new Dictionary<string, Cursor>(StringComparer.Ordinal);
        command.CommandText = "SELECT thread_id,path,file_id,length,modified_ticks,byte_offset,line_number,prefix_hash,tail_hash,models_json FROM _token_usage_rollouts";
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                using var models = JsonDocument.Parse(reader.GetString(9));
                cursors[reader.GetString(0)] = new(reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetInt64(4),
                    reader.GetInt64(5), reader.GetInt32(6), reader.GetString(7), reader.GetString(8),
                    models.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal));
            }
        void Availability(string thread, string path, bool available)
        {
            Execute("""
                INSERT INTO usage_source_events(run_id,thread_id,source_path,observed_at_utc_ms,available)
                SELECT $p0,$p1,$p2,$p3,$p4 WHERE ($p2,$p4) IS NOT
                (SELECT source_path,available FROM usage_source_events WHERE thread_id=$p1 ORDER BY event_id DESC LIMIT 1)
                """, runId, thread, path, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), available ? 1 : 0);
        }

        Dictionary<string, string>? archivedPaths = null;
        var written = 0; var unavailable = 0; long bytesRead = 0;
        foreach (var (threadId, storedPath) in rollouts)
        {
            var path = storedPath;
            if (!File.Exists(path))
            {
                archivedPaths ??= FindRollouts(codexHome);
                if (!archivedPaths.TryGetValue(threadId, out path))
                {
                    unavailable++; Availability(threadId, storedPath, false); continue;
                }
            }
            try
            {
                using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var fileId = RolloutFile.Identity(source, path);
                var length = source.Length;
                var modified = File.GetLastWriteTimeUtc(source.SafeFileHandle).Ticks;
                Availability(threadId, path, true);
                cursors.TryGetValue(threadId, out var previous);
                if (previous is not null && previous.FileId == fileId && previous.Length == length && previous.Modified == modified)
                {
                    if (previous.Path != path) Execute("UPDATE _token_usage_rollouts SET path=$p1 WHERE thread_id=$p0", threadId, path);
                    continue;
                }

                var compressed = path.EndsWith(".zst", StringComparison.Ordinal);
                string? reset = previous is null ? "initial" :
                    compressed || previous.Path.EndsWith(".zst", StringComparison.Ordinal) ? "archive_version" :
                    previous.FileId != fileId ? "source_replaced" :
                    length < previous.Offset ? "source_truncated" :
                    length == previous.Length || RolloutFile.Checkpoint(source, previous.Offset, true) != previous.Prefix ||
                    RolloutFile.Checkpoint(source, previous.Offset, false) != previous.Tail ? "source_rewritten" : null;
                var offset = reset is null ? previous!.Offset : 0;
                var firstOffset = offset;
                var lineNumber = reset is null ? previous!.Line : 0;
                var firstLine = lineNumber + 1;
                var models = reset is null ? previous!.Models : new Dictionary<string, string>(StringComparer.Ordinal);
                var occurrences = new Dictionary<string, long>(StringComparer.Ordinal);
                var started = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                void Record(string line)
                {
                    lineNumber++;
                    if (!line.Contains("\"token_usage_record\"", StringComparison.Ordinal) &&
                        !line.Contains("\"turn_context\"", StringComparison.Ordinal)) return;
                    using var document = JsonDocument.Parse(line);
                    var entry = document.RootElement;
                    if (entry.ValueKind != JsonValueKind.Object) throw new JsonException("Codex rollout entries must be JSON objects");
                    if (!entry.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object) return;
                    var turn = ReadString(payload, "turn_id");
                    var kind = ReadString(entry, "type");
                    if (kind == "turn_context" && turn is not null && ReadString(payload, "model") is { } model)
                        models[turn] = model;
                    if (kind != "token_usage_record" || ReadString(payload, "thread_id") != threadId ||
                        ReadString(payload, "response_id") is not { Length: > 0 } response ||
                        !payload.TryGetProperty("usage", out var usage) ||
                        ReadCount(usage, "input_tokens") is not long input || ReadCount(usage, "output_tokens") is not long output ||
                        !DateTimeOffset.TryParse(ReadString(entry, "timestamp"), CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal, out var observed)) return;
                    var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(line)));
                    if (!occurrences.TryGetValue(hash, out var occurrence))
                        occurrence = reset is null ? Convert.ToInt64(Execute(
                            "SELECT COALESCE(MAX(occurrence),0) FROM response_token_usage_observations WHERE thread_id=$p0 AND record_hash=$p1", threadId, hash)) : 0;
                    occurrences[hash] = ++occurrence;
                    written += Convert.ToInt32(Execute("""
                        INSERT OR IGNORE INTO response_token_usage_observations
                        (run_id,source_line,record_hash,occurrence,thread_id,response_id,turn_id,recorded_at_utc_ms,model,
                         total_input_tokens,cache_read_input_tokens,cache_write_input_tokens,total_output_tokens,reasoning_output_tokens)
                        VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7,$p8,$p9,$p10,$p11,$p12,$p13);
                        SELECT changes();
                        """, runId, lineNumber, hash, occurrence, threadId, response, turn, observed.ToUnixTimeMilliseconds(),
                        turn is null ? null : models.GetValueOrDefault(turn), input, ReadCount(usage, "cached_input_tokens"),
                        ReadCount(usage, "cache_write_input_tokens"), output, ReadCount(usage, "reasoning_output_tokens")));
                }

                long readBytes;
                try
                {
                    if (compressed)
                    {
                        using var decoded = new DecompressionStream(source);
                        using var reader = new StreamReader(decoded);
                        while (reader.ReadLine() is { } line) Record(line);
                        offset = length;
                        readBytes = source.Position;
                    }
                    else
                    {
                        foreach (var line in RolloutFile.Lines(source, offset, length))
                        {
                            Record(line.Text);
                            offset = line.End;
                        }
                        readBytes = source.Position - firstOffset;
                    }
                }
                catch (JsonException error) { throw new InvalidDataException($"invalid Codex usage event in {Path.GetFileName(path)} at line {lineNumber}", error); }
                bytesRead += readBytes;
                Execute("""
                    INSERT INTO usage_reads(run_id,thread_id,source_path,started_at_utc_ms,stopped_at_utc_ms,
                      start_offset,end_offset,bytes_read,first_line,last_line,reset_reason)
                    VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7,$p8,$p9,$p10)
                    """, runId, threadId, path, started, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), firstOffset, offset,
                    readBytes, firstLine, lineNumber, reset);
                Execute("""
                    INSERT INTO _token_usage_rollouts VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7,$p8,$p9)
                    ON CONFLICT(thread_id) DO UPDATE SET path=excluded.path,file_id=excluded.file_id,length=excluded.length,
                      modified_ticks=excluded.modified_ticks,byte_offset=excluded.byte_offset,line_number=excluded.line_number,
                      prefix_hash=excluded.prefix_hash,tail_hash=excluded.tail_hash,models_json=excluded.models_json
                    """, threadId, path, fileId, length, modified, offset, lineNumber,
                    compressed ? "" : RolloutFile.Checkpoint(source, offset, true),
                    compressed ? "" : RolloutFile.Checkpoint(source, offset, false), ModelJson(models));
            }
            catch (IOException error) when (error is FileNotFoundException or DirectoryNotFoundException)
            {
                unavailable++; Availability(threadId, path, false);
            }
        }
        return (written, unavailable, bytesRead);
    }

    private static string ModelJson(Dictionary<string, string> models)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var pair in models) writer.WriteString(pair.Key, pair.Value);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static Dictionary<string, string> FindRollouts(string codexHome)
    {
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in new[] { "sessions", "archived_sessions" })
        {
            var root = Path.Combine(codexHome, directory);
            if (!Directory.Exists(root)) continue;
            foreach (var path in Directory.EnumerateFiles(root, "rollout-*.jsonl*", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(path);
                if (name.EndsWith(".zst", StringComparison.Ordinal)) name = Path.GetFileNameWithoutExtension(name);
                if (!name.EndsWith(".jsonl", StringComparison.Ordinal)) continue;
                var stem = Path.GetFileNameWithoutExtension(name);
                if (stem.Length >= 36 && Guid.TryParseExact(stem.AsSpan(stem.Length - 36), "D", out var id))
                    paths.TryAdd(id.ToString(), path);
            }
        }
        return paths;
    }

    private static long? ReadCount(JsonElement value, string key) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var field) &&
        field.ValueKind == JsonValueKind.Number && field.TryGetInt64(out var count) ? count : null;
    private static string? ReadString(JsonElement value, string key) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
}
