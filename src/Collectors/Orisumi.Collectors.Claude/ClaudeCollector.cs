using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using Microsoft.Data.Sqlite;
using Orisumi.Collectors.Storage;

namespace Orisumi.Collectors.Claude;

public static partial class ClaudeCollector
{
    private static readonly JsonSerializerOptions ContentJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    private static int Import(SqliteConnection connection, SqliteTransaction transaction, long runId,
        string projects, IReadOnlyCollection<string>? changedPaths, out long bytesRead, out long recordsRead)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        object? Execute(string sql, params object?[] values)
        {
            command.CommandText = sql;
            command.Parameters.Clear();
            for (var i = 0; i < values.Length; i++) command.Parameters.AddWithValue($"$p{i}", values[i] ?? DBNull.Value);
            return command.ExecuteScalar();
        }
        var recordsWritten = 0;
        var reader = new TranscriptReader(connection, transaction, runId);
        reader.Read(projects, changedPaths, (sourceId, path, lineNumber, line, row, nextOccurrence) =>
        {
            var record = ReadRecord(row);
            if (record is null || record is { Title: null, Message: null, Usage: null }) return;
            var sessionId = record.SessionId;
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(line)));
            var occurrence = nextOccurrence(hash);
            var observation = Execute("""
                INSERT OR IGNORE INTO source_records(run_id,source_id,source_path,source_line,record_hash,hash_occurrence)
                VALUES($p0,$p1,$p2,$p3,$p4,$p5) RETURNING observation_id;
                """, runId, sourceId, path, lineNumber, hash, occurrence);
            if (observation is not long observationId) return;
            recordsWritten++;
            Execute("INSERT OR IGNORE INTO sessions VALUES($p0)", sessionId);
            if (record.Title is { } title)
                Execute("""
                    INSERT INTO session_title_observations SELECT $p0,$p1,$p2
                    WHERE $p2 IS NOT (SELECT title FROM session_title_observations WHERE session_id=$p1 ORDER BY observation_id DESC LIMIT 1);
                    """, observationId, sessionId, title);
            if (record.Message is { } message)
            {
                Execute("INSERT OR IGNORE INTO messages VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6)",
                    sessionId, message.Uuid, message.ParentUuid, message.Role, message.CreatedAt, message.Cwd, message.ResponseId);
                Execute("""
                    INSERT INTO message_content_observations SELECT $p0,$p1,$p2,$p3
                    WHERE $p3 IS NOT (SELECT content_json FROM message_content_observations
                        WHERE session_id=$p1 AND message_uuid=$p2 ORDER BY observation_id DESC LIMIT 1);
                    """, observationId, sessionId, message.Uuid, message.ContentJson);
            }
            if (record.Usage is { } usage)
                Execute("""
                    INSERT INTO response_token_usage_observations SELECT $p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7,$p8,$p9
                    WHERE ($p4,$p5,$p6,$p7,$p8,$p9) IS NOT
                      (SELECT model,total_input_tokens,cache_read_input_tokens,cache_write_input_tokens,total_output_tokens,reasoning_output_tokens
                       FROM response_token_usage WHERE session_id=$p1 AND response_id=$p2);
                    """, observationId, sessionId, usage.ResponseId, usage.RecordedAt, usage.Model, usage.Input,
                    usage.Cached, usage.CacheWrite, usage.Output, usage.Reasoning);
        });
        bytesRead = reader.BytesRead;
        recordsRead = reader.RecordsRead;
        return recordsWritten;
    }

    private static ParsedRecord? ReadRecord(JsonElement row)
    {
        var kind = ReadString(row, "type");
        var sessionId = ReadString(row, "sessionId");
        if (sessionId is null || kind is not ("custom-title" or "user" or "assistant") || IsTrue(row, "isSidechain"))
            return null;

        if (kind == "custom-title")
            return new ParsedRecord(sessionId, Title: ReadString(row, "customTitle"));

        // A missing source time stays unknown; the record itself is still kept.
        long? createdAt = DateTimeOffset.TryParse(ReadString(row, "timestamp"), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var timestamp) ? timestamp.ToUnixTimeMilliseconds() : null;
        var cwd = ReadString(row, "cwd");

        if (!row.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
            return null;
        var record = new ParsedRecord(sessionId, Usage: kind == "assistant" ? ReadUsage(message, createdAt) : null);
        if (ReadString(row, "uuid") is not { } uuid || !message.TryGetProperty("content", out var content))
            return record;
        if (kind == "user" && (IsTrue(row, "isMeta") || IsTrue(row, "isCompactSummary") ||
            (row.TryGetProperty("origin", out var origin) && ReadString(origin, "kind") is { } author && author != "human")))
            return record;

        string contentJson;
        if (content.ValueKind == JsonValueKind.String)
        {
            contentJson = JsonSerializer.Serialize(new[] { new { type = "text", text = content.GetString() } }, ContentJsonOptions);
        }
        else if (content.ValueKind == JsonValueKind.Array)
        {
            var visible = content.EnumerateArray()
                .Where(block => ReadString(block, "type") is "text" or "image" or "document").ToArray();
            if (visible.Length == 0)
                return record;
            contentJson = JsonSerializer.Serialize(visible, ContentJsonOptions);
        }
        else
        {
            return record;
        }

        // JSON null marks the first message of a chain; a missing field is unknown.
        var parentUuid = row.TryGetProperty("parentUuid", out var parent)
            ? parent.ValueKind == JsonValueKind.Null ? "" : ReadString(row, "parentUuid")
            : null;
        return record with { Message = new Message(uuid, parentUuid, kind, createdAt, cwd,
            kind == "assistant" ? ReadString(message, "id") : null, contentJson) };
    }

    private static Usage? ReadUsage(JsonElement message, long? recordedAt)
    {
        var responseId = ReadString(message, "id");
        var model = ReadString(message, "model");
        if (string.IsNullOrEmpty(responseId) || model == "<synthetic>" ||
            !message.TryGetProperty("usage", out var counts) || ReadCount(counts, "input_tokens") is not long uncached ||
            ReadCount(counts, "output_tokens") is not long output)
            return null;

        var cached = ReadCount(counts, "cache_read_input_tokens") ?? 0;
        var cacheWrite = ReadCount(counts, "cache_creation_input_tokens") ?? 0;
        var reasoning = counts.TryGetProperty("output_tokens_details", out var details)
            ? ReadCount(details, "thinking_tokens") : null;
        return new Usage(responseId, recordedAt, string.IsNullOrEmpty(model) ? null : model, uncached + cached + cacheWrite, cached, cacheWrite, output, reasoning);
    }

    private static long? ReadCount(JsonElement value, string key) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var field) &&
        field.ValueKind == JsonValueKind.Number && field.TryGetInt64(out var count) ? count : null;

    private static string? ReadString(JsonElement value, string key) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var field) &&
        field.ValueKind == JsonValueKind.String ? field.GetString() : null;

    private static bool IsTrue(JsonElement value, string key) =>
        value.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.True;

    private static SqliteConnection OpenDatabase(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        SqliteFile.CreateIfMissing(path);

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            ForeignKeys = true,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private sealed record ParsedRecord(string SessionId, string? Title = null, Message? Message = null, Usage? Usage = null);
    private sealed record Message(string Uuid, string? ParentUuid, string Role, long? CreatedAt, string? Cwd,
        string? ResponseId, string ContentJson);
    private sealed record Usage(string ResponseId, long? RecordedAt, string? Model, long Input, long Cached, long CacheWrite, long Output, long? Reasoning);
}
