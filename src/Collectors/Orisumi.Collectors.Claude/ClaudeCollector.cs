using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Unicode;
using Microsoft.Data.Sqlite;
using Orisumi.Collectors.Storage;

namespace Orisumi.Collectors.Claude;

public static partial class ClaudeCollector
{
    private const string Source = "claude";

    private static readonly JsonSerializerOptions ContentJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    private static int Import(SqliteConnection connection, SqliteTransaction transaction, long runId,
        string projects, IReadOnlyCollection<string>? changedPaths, CancellationToken stop, out long bytesRead, out long recordsRead)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var recordsWritten = 0;
        var reader = new TranscriptReader(connection, transaction, runId, stop);
        reader.Read(projects, changedPaths, (sourceId, path, lineNumber, line, row, nextOccurrence) =>
        {
            var record = ReadRecord(row, TranscriptReader.AgentSource(projects, path));
            if (record is null) return;
            var sessionId = record.SessionId;
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(line)));
            var occurrence = nextOccurrence(hash);
            var observation = command.Scalar("""
                INSERT OR IGNORE INTO source_records(run_id,source_id,source_path,source_line,record_hash,hash_occurrence)
                VALUES($p0,$p1,$p2,$p3,$p4,$p5) RETURNING observation_id;
                """, runId, sourceId, path, lineNumber, hash, occurrence);
            if (observation is not long observationId) return;
            recordsWritten++;
            command.Scalar("""
                INSERT INTO sessions(session_id,parent_session_id) VALUES($p0,$p1)
                ON CONFLICT(session_id) DO UPDATE SET parent_session_id=excluded.parent_session_id
                WHERE sessions.parent_session_id IS NULL AND excluded.parent_session_id IS NOT NULL;
                """, sessionId, record.ParentSessionId);
            if (record.Title is { } title)
                command.Scalar("""
                    INSERT INTO session_title_observations(observation_id,session_id,kind,title)
                    SELECT $p0,$p1,$p2,$p3
                    WHERE $p3 IS NOT (SELECT title FROM session_title_observations
                        WHERE session_id=$p1 AND kind=$p2 ORDER BY observation_id DESC LIMIT 1);
                    """, observationId, sessionId, title.Kind, title.Value);
            if (record.Event is { } entry)
            {
                var inserted = command.Scalar("""
                    INSERT INTO transcript_events(
                        session_id,uuid,observation_id,parent_uuid,row_type,block_type,created_at_utc_ms,cwd,git_branch,
                        entrypoint,response_id,prompt_source,permission_mode,tool_use_id,is_sidechain,agent_id)
                    VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7,$p8,$p9,$p10,$p11,$p12,$p13,$p14,$p15)
                    ON CONFLICT(session_id,uuid) DO NOTHING RETURNING observation_id;
                    """, sessionId, entry.Uuid, observationId, entry.ParentUuid, entry.RowType, entry.BlockType, entry.CreatedAt,
                    entry.Cwd, entry.GitBranch, entry.Entrypoint, entry.ResponseId, entry.PromptSource, entry.PermissionMode,
                    entry.ToolUseId, entry.IsSidechain, entry.AgentId);
                if (inserted is not null && entry.Payload is not null)
                    command.Scalar("INSERT INTO event_contents(observation_id,payload_json) VALUES($p0,$p1)",
                        observationId, entry.Payload.ToJsonString(ContentJsonOptions));
            }
            if (record.Usage is { } usage)
                command.Scalar("""
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

    private static ParsedRecord? ReadRecord(JsonElement row, (string? SessionId, string? AgentId) agentSource)
    {
        var kind = ReadString(row, "type");
        var sessionId = ReadString(row, "sessionId");
        if (string.IsNullOrEmpty(sessionId) || kind is "queue-operation" or "agent-name" or "permission-mode" ||
            kind?.StartsWith("file-history-", StringComparison.Ordinal) == true ||
            kind?.StartsWith("api-request", StringComparison.Ordinal) == true)
            return null;

        var parentSession = ReadString(row, "parentSessionId") ?? agentSource.SessionId;
        if (parentSession == sessionId) parentSession = null;
        if (kind is "custom-title" or "ai-title")
        {
            var value = ReadString(row, kind == "custom-title" ? "customTitle" : "aiTitle");
            return value is null ? null : new(sessionId, parentSession, Title: new(kind == "custom-title" ? "custom" : "ai", value));
        }

        long? createdAt = DateTimeOffset.TryParse(ReadString(row, "timestamp"), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var timestamp) ? timestamp.ToUnixTimeMilliseconds() : null;
        var message = ReadValue(row, "message");
        var usage = kind == "assistant" ? ReadUsage(message, createdAt) : null;
        var uuid = ReadString(row, "uuid");
        if (string.IsNullOrEmpty(uuid))
            return usage is null ? null : new(sessionId, parentSession, Usage: usage);

        var content = ReadValue(message, "content");
        var block = content.ValueKind == JsonValueKind.Array ? content.EnumerateArray().FirstOrDefault() : default;
        var blockType = kind switch
        {
            "attachment" => ReadString(ReadValue(row, "attachment"), "type"),
            "system" => ReadString(row, "subtype"),
            "user" or "assistant" => content.ValueKind == JsonValueKind.String ? "text" : ReadString(block, "type"),
            _ => ReadString(block, "type"),
        };
        var visible = kind == "assistant" || kind == "user" && !IsTrue(row, "isMeta") && !IsTrue(row, "isCompactSummary") &&
            ReadString(ReadValue(row, "origin"), "kind") is null or "human";
        var humanPrompt = kind == "user" && visible && blockType is "text" or "image" or "document";
        var text = content.ValueKind == JsonValueKind.String ? content.GetString() : ReadString(block, "text");
        var slashCommand = text?.StartsWith("<command-name>", StringComparison.Ordinal) == true ||
            text?.StartsWith("<local-command-", StringComparison.Ordinal) == true;

        var toolUseId = (kind, blockType) switch
        {
            ("assistant", "tool_use") => ReadString(block, "id"),
            ("user", "tool_result") => ReadString(block, "tool_use_id"),
            _ => null,
        };
        var payload = (kind, blockType) switch
        {
            ("assistant", "tool_use") => Pick(block, "name", "input"),
            ("user", "tool_result") => ToolResult(block, ReadValue(row, "permissionDecision")),
            (_, "text") when visible && text is not null => new JsonObject { ["text"] = text },
            (_, "image") when visible => Pick(block, "source"),
            (_, "document") when visible => Pick(block, "source", "title", "context", "citations"),
            _ => null,
        };
        var parentUuid = ReadValue(row, "parentUuid").ValueKind == JsonValueKind.Null ? "" : ReadString(row, "parentUuid");
        var entry = new TranscriptEvent(uuid, parentUuid, kind, blockType, createdAt, ReadString(row, "cwd"),
            ReadString(row, "gitBranch"), ReadString(row, "entrypoint"), kind == "assistant" ? ReadString(message, "id") : null,
            humanPrompt && !slashCommand ? ReadString(row, "promptSource") : null,
            humanPrompt ? ReadString(row, "permissionMode") : null, toolUseId,
            IsTrue(row, "isSidechain") || agentSource.AgentId is not null, ReadString(row, "agentId") ?? agentSource.AgentId, payload);
        return new(sessionId, parentSession, Event: entry, Usage: usage);
    }

    private static JsonObject Pick(JsonElement value, params string[] keys)
    {
        var payload = new JsonObject();
        foreach (var key in keys)
            if (value.TryGetProperty(key, out var field))
                payload[key] = JsonNode.Parse(field.GetRawText());
        return payload;
    }

    private static JsonObject ToolResult(JsonElement block, JsonElement permission)
    {
        var isError = ReadValue(block, "is_error").ValueKind switch
        {
            JsonValueKind.True => (bool?)true,
            JsonValueKind.False => false,
            _ => null,
        };
        var content = ReadValue(block, "content");
        var text = content.ValueKind == JsonValueKind.String ? content.GetString() : null;
        var payload = new JsonObject
        {
            ["is_error"] = isError,
            ["permission_decision"] = ReadString(permission, "decision"),
            ["permission_source"] = ReadString(permission, "source"),
            ["content_length"] = content.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                ? (int?)null : text?.Length ?? content.GetRawText().Length,
        };
        if (isError == true)
        {
            if (content.ValueKind == JsonValueKind.Array)
                text = string.Join('\n', content.EnumerateArray().Where(part => ReadString(part, "type") == "text")
                    .Select(part => ReadString(part, "text")));
            if (text is not null) payload["error_text"] = text;
        }
        return payload;
    }

    private static Usage? ReadUsage(JsonElement message, long? recordedAt)
    {
        var responseId = ReadString(message, "id");
        var model = ReadString(message, "model");
        var counts = ReadValue(message, "usage");
        if (string.IsNullOrEmpty(responseId) || model == "<synthetic>" ||
            ReadCount(counts, "input_tokens") is not long uncached ||
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

    private static string? ReadString(JsonElement value, string key)
    {
        var field = ReadValue(value, key);
        return field.ValueKind == JsonValueKind.String ? field.GetString() : null;
    }

    private static JsonElement ReadValue(JsonElement value, string key) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var field) ? field : default;

    private static bool IsTrue(JsonElement value, string key) => ReadValue(value, key).ValueKind == JsonValueKind.True;

    private sealed record ParsedRecord(string SessionId, string? ParentSessionId, Title? Title = null,
        TranscriptEvent? Event = null, Usage? Usage = null);
    private sealed record Title(string Kind, string Value);
    private sealed record TranscriptEvent(string Uuid, string? ParentUuid, string? RowType, string? BlockType, long? CreatedAt,
        string? Cwd, string? GitBranch, string? Entrypoint, string? ResponseId, string? PromptSource, string? PermissionMode,
        string? ToolUseId, bool IsSidechain, string? AgentId, JsonObject? Payload);
    private sealed record Usage(string ResponseId, long? RecordedAt, string? Model, long Input, long Cached, long CacheWrite, long Output, long? Reasoning);
}
