using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Orisumi.Capture.Storage;
using ZstdSharp;

namespace Orisumi.Collectors.Codex;

public static partial class CodexCollector
{
    private sealed class RolloutReader(SqliteCommand command, long runId, bool reconcile, CancellationToken stop)
    {
        private int events;
        private int usage;
        private long bytesRead;
        private sealed record Cursor(long RolloutId, string? ThreadId, JsonlCursor File,
            long SourceFileEventId, long ReadVersionId, bool Present);

        internal (int Events, int Usage, long BytesRead) Read(string codexHome)
        {
            var cursors = LoadCursors();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var directory in RolloutDirectories)
            {
                var root = Path.Combine(codexHome, directory);
                if (!Directory.Exists(root)) continue;
                var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = false };
                foreach (var path in Directory.EnumerateFiles(root, "rollout-*.jsonl*", options)
                    .Where(IsRollout).OrderBy(IsCompressed).ThenBy(path => path, StringComparer.Ordinal))
                {
                    stop.ThrowIfCancellationRequested();
                    // A move or compression keeps the rollout name. Distinct rollouts can share a thread ID.
                    var name = Path.GetFileName(path);
                    var rolloutId = IsCompressed(path) ? name[..^".jsonl.zst".Length] : name[..^".jsonl".Length];
                    if (seen.Contains(rolloutId)) continue;
                    try
                    {
                        ReadFile(rolloutId, path, cursors.GetValueOrDefault(rolloutId));
                        seen.Add(rolloutId);
                    }
                    catch (FileNotFoundException) { } // A concurrent move can expose the archive on this or the next scan.
                    catch (DirectoryNotFoundException) { }
                }
            }
            foreach (var (id, cursor) in cursors)
            {
                if (!cursor.Present || seen.Contains(id)) continue;
                FileEvent(cursor.RolloutId, cursor.File.Path, cursor.File.Identity, "missing", cursor.File.Offset, null);
                command.Execute("UPDATE _rollout_cursors SET present=0 WHERE rollout_id=$p0", cursor.RolloutId);
            }
            return (events, usage, bytesRead);
        }

        private void ReadFile(string name, string path, Cursor? previous)
        {
            var archive = IsCompressed(path);
            using var reader = new JsonlReader(path, archive ? DecodeArchive : null, trackCommittedPrefix: !archive, stop: stop);
            var plan = reader.Plan(previous?.File, reconcile, previous is not null && IsCompressed(previous.File.Path));
            var reason = plan.Resets ? plan.Change.ToString().ToLowerInvariant()
                : !previous!.Present ? "resumed"
                : previous.File.Path != path ? "moved" : null;
            if (plan.Change == JsonlChange.Unchanged && reason is null) return;

            var rolloutId = previous?.RolloutId ?? (long)command.Scalar("""
                INSERT INTO rollouts(name) VALUES($p0) RETURNING rollout_id
                """, name)!;
            var sourceFileEventId = reason is null ? previous!.SourceFileEventId
                : FileEvent(rolloutId, path, reader.Identity, reason, previous?.File.Offset, reader.Length);
            var readVersionId = plan.Resets ? sourceFileEventId : previous!.ReadVersionId;
            var threadId = plan.Resets ? null : previous!.ThreadId;
            JsonlCursor file;
            try { file = plan.Change == JsonlChange.Unchanged ? previous!.File with { Path = path } : ReadRecords(); }
            catch (ZstdException error) { throw new InvalidDataException($"Invalid Zstandard archive: {path}", error); }
            command.Execute("""
                INSERT INTO _rollout_cursors VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7,$p8,$p9,$p10,1)
                ON CONFLICT(rollout_id) DO UPDATE SET thread_id=excluded.thread_id,source_file_event_id=excluded.source_file_event_id,
                  read_version_id=excluded.read_version_id,byte_offset=excluded.byte_offset,line_number=excluded.line_number,
                  observed_length=excluded.observed_length,modified_ticks=excluded.modified_ticks,
                  prefix_hash=excluded.prefix_hash,tail_hash=excluded.tail_hash,committed_hash=excluded.committed_hash,present=1
                """, rolloutId, threadId, sourceFileEventId, readVersionId, file.Offset, file.LineNumber,
                file.Length, file.Modified, file.Prefix, file.Tail, file.CommittedHash);

            JsonlCursor ReadRecords()
            {
                var occurrences = new Dictionary<string, long>(StringComparer.Ordinal);
                var (cursor, readBytes) = reader.Read(plan, (lineNumber, line) =>
                {
                    if (string.IsNullOrWhiteSpace(line)) return;
                    try
                    {
                        using var document = JsonDocument.Parse(line);
                        var row = document.RootElement;
                        if (row.ValueKind != JsonValueKind.Object) throw new JsonException("Rollout records must be JSON objects");
                        var payload = Field(row, "payload");
                        var type = String(row, "type");
                        // Subagent rollouts can include another session's metadata after their own.
                        if (type == "session_meta" && String(payload, "id") is { Length: > 0 } id &&
                            (threadId is null || threadId == id))
                        {
                            Thread(id, name, payload);
                            threadId = id;
                        }
                        if (threadId is null) return;
                        var turnId = String(payload, "turn_id");
                        var kind = type switch
                        {
                            "event_msg" => String(payload, "type") switch
                            {
                                "task_started" => "turn_started",
                                "task_complete" => "turn_completed",
                                "turn_aborted" => "turn_aborted",
                                "item_completed" => "item_completed",
                                _ => null,
                            },
                            "turn_context" or "token_usage_record" => type,
                            "response_item" when String(payload, "type") == "web_search_call" => "web_search_call",
                            _ => null,
                        };
                        if (kind is null) return;
                        if (kind == "web_search_call")
                            turnId ??= String(Field(payload, "internal_chat_message_metadata_passthrough"), "turn_id");

                        var attributedThread = String(payload, "thread_id");
                        if (attributedThread is { Length: > 0 } && attributedThread != threadId) return;
                        var lifecycle = kind is "turn_started" or "turn_completed" or "turn_aborted";
                        long? start = lifecycle ? Milliseconds(payload, "started_at") : Integer(payload, "started_at_ms");
                        long? end = lifecycle ? Milliseconds(payload, "completed_at") : Integer(payload, "completed_at_ms");
                        if (turnId is { Length: > 0 }) Turn(threadId, turnId, lifecycle ? start : null);
                        else turnId = null;

                        var recorded = Timestamp(row, "timestamp");
                        var ordinal = Integer(row, "ordinal");
                        if (kind == "token_usage_record")
                        {
                            if (String(payload, "response_id") is not { Length: > 0 } responseId) return;
                            var tokens = Field(payload, "usage");
                            usage += command.Execute("""
                                INSERT INTO response_token_usage_events
                                (run_id,thread_id,turn_id,response_id,kind,recorded_at_utc_ms,rollout_id,source_file_event_id,source_line,
                                 rollout_ordinal,total_input_tokens,cache_read_input_tokens,cache_write_input_tokens,total_output_tokens,reasoning_output_tokens,total_tokens)
                                VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7,$p8,$p9,$p10,$p11,$p12,$p13,$p14,$p15)
                                ON CONFLICT(thread_id,response_id) DO NOTHING
                                """, runId, threadId, turnId, responseId, kind, recorded, rolloutId, sourceFileEventId, lineNumber, ordinal,
                                Integer(tokens, "input_tokens"), Integer(tokens, "cached_input_tokens"), Integer(tokens, "cache_write_input_tokens"),
                                Integer(tokens, "output_tokens"), Integer(tokens, "reasoning_output_tokens"), Integer(tokens, "total_tokens"));
                            return;
                        }

                        var item = kind == "web_search_call" ? payload : Field(payload, "item");
                        var itemType = ItemType(item);
                        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(line));
                        var hashKey = Convert.ToHexString(hash);
                        // A shortened version can have fewer occurrences than the historical maximum.
                        if (!occurrences.TryGetValue(hashKey, out var occurrence))
                            occurrence = plan.Resets ? 0 : (long)command.Scalar("""
                                SELECT COALESCE(MAX(hash_occurrence),0) FROM rollout_events WHERE rollout_id=$p0 AND record_hash=$p1
                                  AND _read_version_id=$p2
                                """, rolloutId, hash, readVersionId)!;
                        occurrences[hashKey] = ++occurrence;
                        var eventId = command.Scalar("""
                            INSERT INTO rollout_events
                            (run_id,thread_id,turn_id,kind,item_id,item_type,call_id,recorded_at_utc_ms,started_at_utc_ms,completed_at_utc_ms,
                             rollout_id,source_file_event_id,source_line,rollout_ordinal,record_hash,hash_occurrence,_read_version_id)
                            VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7,$p8,$p9,$p10,$p11,$p12,$p13,$p14,$p15,$p16)
                            ON CONFLICT(rollout_id,record_hash,hash_occurrence) DO NOTHING RETURNING event_id
                            """, runId, threadId, turnId, kind, String(item, "id"), itemType,
                            String(item, "call_id") ?? String(payload, "call_id"), recorded, start, end, rolloutId, sourceFileEventId,
                            lineNumber, ordinal, hash, occurrence, readVersionId);
                        if (eventId is null)
                        {
                            command.Execute("""
                                UPDATE rollout_events SET _read_version_id=$p0
                                WHERE rollout_id=$p1 AND record_hash=$p2 AND hash_occurrence=$p3
                                """, readVersionId, rolloutId, hash, occurrence);
                            return;
                        }

                        events++;
                        var content = EventContent(kind, payload, item, itemType);
                        if (content is not null)
                            command.Execute("INSERT INTO event_contents VALUES($p0,$p1)", eventId, content);
                    }
                    catch (JsonException error)
                    {
                        throw new InvalidDataException($"Invalid Codex rollout JSON: {path}:{lineNumber}", error);
                    }
                }, previous?.File);
                bytesRead += readBytes;
                return cursor;
            }
        }

        private void Thread(string id, string rolloutId, JsonElement meta)
        {
            var git = Field(meta, "git");
            var source = Field(meta, "source");
            var spawn = Field(Field(source, "subagent"), "thread_spawn");
            var parent = String(spawn, "parent_thread_id") ?? String(meta, "forked_from_id") ?? FilenameParent(rolloutId, id);
            command.Execute("""
                INSERT INTO threads VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7,$p8)
                ON CONFLICT(thread_id) DO UPDATE SET
                    created_at_utc_ms=COALESCE(threads.created_at_utc_ms,excluded.created_at_utc_ms),
                    cwd=COALESCE(threads.cwd,excluded.cwd),git_branch=COALESCE(threads.git_branch,excluded.git_branch),
                    git_sha=COALESCE(threads.git_sha,excluded.git_sha),git_origin_url=COALESCE(threads.git_origin_url,excluded.git_origin_url),
                    source=COALESCE(threads.source,excluded.source),originator=COALESCE(threads.originator,excluded.originator),
                    parent_thread_id=COALESCE(threads.parent_thread_id,excluded.parent_thread_id)
                """, id, Timestamp(meta, "timestamp"), String(meta, "cwd"), String(git, "branch"), String(git, "commit_hash"),
                String(git, "repository_url"), source.ValueKind == JsonValueKind.Object ? source.GetRawText() : String(meta, "source"),
                String(meta, "originator"), parent == id ? null : parent);
        }

        private void Turn(string threadId, string turnId, long? started) => command.Execute("""
            INSERT INTO turns VALUES($p0,$p1,$p2)
            ON CONFLICT(thread_id,turn_id) DO UPDATE SET started_at_utc_ms=excluded.started_at_utc_ms
            WHERE turns.started_at_utc_ms IS NULL AND excluded.started_at_utc_ms IS NOT NULL
            """, threadId, turnId, started);

        private static string? FilenameParent(string rollout, string threadId)
        {
            const int idLength = 36;
            if (rollout.Length < 2 * idLength + 1 || rollout[^(idLength + 1)] != '_') return null;
            var child = rollout[^idLength..];
            var parent = rollout.Substring(rollout.Length - 2 * idLength - 1, idLength);
            return child == threadId && Guid.TryParseExact(child, "D", out _) && Guid.TryParseExact(parent, "D", out _)
                ? parent : null;
        }

        private Dictionary<string, Cursor> LoadCursors()
        {
            command.Parameters.Clear();
            command.CommandText = """
                SELECT r.name,c.rollout_id,c.thread_id,f.file_identity,f.source_path,c.byte_offset,c.line_number,
                       c.observed_length,c.modified_ticks,c.prefix_hash,c.tail_hash,c.committed_hash,c.source_file_event_id,c.read_version_id,c.present
                FROM _rollout_cursors c JOIN rollouts r USING(rollout_id)
                JOIN source_file_events f ON f.event_id=c.source_file_event_id
                """;
            using var reader = command.ExecuteReader();
            var cursors = new Dictionary<string, Cursor>(StringComparer.Ordinal);
            while (reader.Read())
                cursors.Add(reader.GetString(0), new(reader.GetInt64(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                    new(reader.GetString(3), reader.GetString(4), reader.GetInt64(5), reader.GetInt32(6),
                        reader.GetInt64(7), reader.GetInt64(8), reader.GetString(9), reader.GetString(10),
                        reader.IsDBNull(11) ? null : reader.GetString(11)),
                    reader.GetInt64(12), reader.GetInt64(13), reader.GetBoolean(14)));
            return cursors;
        }

        private long FileEvent(long rolloutId, string path, string identity, string kind, long? previousOffset, long? length) =>
            (long)command.Scalar("""
                INSERT INTO source_file_events(run_id,rollout_id,source_path,file_identity,observed_at_utc_ms,kind,previous_offset,observed_length)
                VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7) RETURNING event_id
                """, runId, rolloutId, path, identity, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), kind, previousOffset, length)!;
    }

    private static Stream DecodeArchive(Stream stream) =>
        new DecompressionStream(stream, checkEndOfStream: true, leaveOpen: true);

    private static bool IsCompressed(string path) => path.EndsWith(".jsonl.zst", StringComparison.Ordinal);
    private static bool IsRollout(string path) =>
        Path.GetFileName(path).StartsWith("rollout-", StringComparison.Ordinal) &&
        (path.EndsWith(".jsonl", StringComparison.Ordinal) || IsCompressed(path));

    private static JsonElement Field(JsonElement value, string key) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var field) ? field : default;

    private static string? String(JsonElement value, string key) =>
        Field(value, key) is { ValueKind: JsonValueKind.String } field ? field.GetString() : null;

    private static long? Integer(JsonElement value, string key) =>
        Field(value, key) is { ValueKind: JsonValueKind.Number } field && field.TryGetInt64(out var count) ? count : null;

    private static long? Milliseconds(JsonElement value, string key) => Integer(value, key) * 1000;

    private static long? Timestamp(JsonElement value, string key) =>
        DateTimeOffset.TryParse(String(value, key), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time)
            ? time.ToUnixTimeMilliseconds() : null;
}
