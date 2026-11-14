using Orisumi.Collectors.Storage;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using Microsoft.Data.Sqlite;

namespace Orisumi.Collectors.Claude;

public static class ClaudeCollector
{
    private static readonly JsonSerializerOptions ContentJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    public static string Capture(string store)
    {
        var claudeHome = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (string.IsNullOrWhiteSpace(claudeHome))
            claudeHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        var projects = Path.Combine(claudeHome, "projects");
        if (!Directory.Exists(projects))
            throw new DirectoryNotFoundException($"Claude Code projects directory not found: {projects}");

        var database = Path.GetFullPath(Path.Combine(store, "raw", "claude.sqlite"));
        try
        {
            using var connection = OpenDatabase(database);
            SqliteSchema.EnsureCreated(connection);
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            object? Execute(string sql, params object?[] values)
            {
                command.CommandText = sql;
                command.Parameters.Clear();
                for (var i = 0; i < values.Length; i++) command.Parameters.AddWithValue($"$p{i}", values[i] ?? DBNull.Value);
                return command.ExecuteScalar();
            }
            var runId = (long)Execute("INSERT INTO capture_runs(started_at_utc_ms) VALUES($p0); SELECT last_insert_rowid();", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())!;
            var recordsWritten = 0;
            ReadRecords(projects, (path, lineNumber, line, row) =>
            {
                var sessions = new Dictionary<string, Session>(StringComparer.Ordinal);
                Observe(row, sessions);
                foreach (var (sessionId, session) in sessions)
                {
                    if (session.Title is null && session.Messages.Count == 0 && session.Usage.Count == 0) continue;
                    var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(line)));
                    var observation = Execute("""
                        INSERT OR IGNORE INTO source_records(run_id,source_path,source_line,record_hash) VALUES($p0,$p1,$p2,$p3)
                        RETURNING observation_id;
                        """, runId, path, lineNumber, hash);
                    if (observation is not long observationId) continue;
                    recordsWritten++;
                    Execute("INSERT OR IGNORE INTO sessions VALUES($p0)", sessionId);
                    if (session.Title is { } title)
                        Execute("""
                            INSERT INTO session_title_observations SELECT $p0,$p1,$p2
                            WHERE $p2 IS NOT (SELECT title FROM session_title_observations WHERE session_id=$p1 ORDER BY observation_id DESC LIMIT 1);
                            """, observationId, sessionId, title);
                    foreach (var (messageId, message) in session.Messages)
                    {
                        Execute("INSERT OR IGNORE INTO messages VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6)",
                            sessionId, messageId, message.ParentUuid, message.Role, message.Observed, message.Cwd, message.ResponseId);
                        Execute("""
                            INSERT INTO message_content_observations SELECT $p0,$p1,$p2,$p3
                            WHERE $p3 IS NOT (SELECT content_json FROM message_content_observations
                                WHERE session_id=$p1 AND message_uuid=$p2 ORDER BY observation_id DESC LIMIT 1);
                            """, observationId, sessionId, messageId, message.ContentJson);
                    }
                    foreach (var (responseId, usage) in session.Usage)
                        Execute("""
                            INSERT INTO response_token_usage_observations SELECT $p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7,$p8,$p9
                            WHERE ($p4,$p5,$p6,$p7,$p8,$p9) IS NOT
                              (SELECT model,total_input_tokens,cache_read_input_tokens,cache_write_input_tokens,total_output_tokens,reasoning_output_tokens
                               FROM response_token_usage WHERE session_id=$p1 AND response_id=$p2);
                            """, observationId, sessionId, responseId, usage.Observed, usage.Model, usage.Input,
                            usage.Cached, usage.CacheWrite, usage.Output, usage.Reasoning);
                }
            });
            Execute("UPDATE capture_runs SET stopped_at_utc_ms=$p0 WHERE run_id=$p1", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), runId);

            transaction.Commit();
            return $"Claude Code: {recordsWritten} source records captured in {database}";
        }
        catch (SqliteException error)
        {
            throw new IOException($"could not save Claude Code capture: {error.Message}", error);
        }
    }

    private static void ReadRecords(string projects, Action<string, int, string, JsonElement> observe)
    {
        var transcripts = Directory.EnumerateDirectories(projects)
            .SelectMany(project => Directory.EnumerateFiles(project, "*.jsonl"))
            .Select(path => new FileInfo(path))
            .OrderBy(file => file.LastWriteTimeUtc)
            .ThenBy(file => file.FullName, StringComparer.Ordinal);

        foreach (var transcript in transcripts)
        {
            using var stream = transcript.Open(FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var lineNumber = 0;
            while (reader.ReadLine() is { } line)
            {
                lineNumber++;
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                try
                {
                    using var document = JsonDocument.Parse(line);
                    observe(transcript.FullName, lineNumber, line, document.RootElement);
                }
                catch (JsonException error)
                {
                    if (reader.EndOfStream && HasUnterminatedLastLine(stream))
                        break;
                    throw new InvalidDataException($"invalid Claude Code JSON in {transcript.Name} at line {lineNumber}", error);
                }
            }
        }
    }

    private static bool HasUnterminatedLastLine(FileStream stream)
    {
        Span<byte> last = stackalloc byte[1];
        var length = stream.Length;
        return length > 0 && RandomAccess.Read(stream.SafeFileHandle, last, length - 1) == 1 && last[0] != '\n';
    }

    private static void Observe(JsonElement row, Dictionary<string, Session> sessions)
    {
        var kind = ReadString(row, "type");
        var sessionId = ReadString(row, "sessionId");
        if (sessionId is null || kind is not ("custom-title" or "user" or "assistant") || IsTrue(row, "isSidechain"))
            return;

        if (!sessions.TryGetValue(sessionId, out var session))
            sessions.Add(sessionId, session = new Session());

        if (kind == "custom-title")
        {
            session.Title = ReadString(row, "customTitle") ?? session.Title;
            return;
        }

        if (!DateTimeOffset.TryParse(ReadString(row, "timestamp"), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var timestamp))
            return;

        var observed = timestamp.ToUnixTimeMilliseconds();
        var cwd = ReadString(row, "cwd");

        if (!row.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
            return;
        if (kind == "assistant")
            ObserveUsage(message, observed, session.Usage);
        if (ReadString(row, "uuid") is not { } uuid || !message.TryGetProperty("content", out var content))
            return;
        if (kind == "user" && (IsTrue(row, "isMeta") || IsTrue(row, "isCompactSummary") ||
            (row.TryGetProperty("origin", out var origin) && ReadString(origin, "kind") is { } author && author != "human")))
            return;

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
                return;
            contentJson = JsonSerializer.Serialize(visible, ContentJsonOptions);
        }
        else
        {
            return;
        }

        session.Messages[uuid] = new Message(ReadString(row, "parentUuid"), kind, observed, cwd,
            ReadString(message, "id"), contentJson);
    }

    private static void ObserveUsage(JsonElement message, long observed, Dictionary<string, Usage> usage)
    {
        var responseId = ReadString(message, "id");
        var model = ReadString(message, "model");
        if (string.IsNullOrEmpty(responseId) || string.IsNullOrEmpty(model) || model == "<synthetic>" ||
            !message.TryGetProperty("usage", out var counts) || ReadCount(counts, "input_tokens") is not long uncached ||
            ReadCount(counts, "output_tokens") is not long output)
            return;
        if (usage.TryGetValue(responseId, out var previous) && previous.Observed > observed)
            return;

        var cached = ReadCount(counts, "cache_read_input_tokens") ?? 0;
        var cacheWrite = ReadCount(counts, "cache_creation_input_tokens") ?? 0;
        var reasoning = counts.TryGetProperty("output_tokens_details", out var details)
            ? ReadCount(details, "thinking_tokens") : null;
        usage[responseId] = new Usage(observed, model, uncached + cached + cacheWrite, cached, cacheWrite, output, reasoning);
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
        var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.Write, Share = FileShare.ReadWrite };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (File.Open(path, options)) { }

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

    private sealed class Session
    {
        public string? Title { get; set; }
        public Dictionary<string, Message> Messages { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Usage> Usage { get; } = new(StringComparer.Ordinal);
    }

    private sealed record Message(string? ParentUuid, string Role, long Observed, string? Cwd,
        string? ResponseId, string ContentJson);
    private sealed record Usage(long Observed, string Model, long Input, long Cached, long CacheWrite, long Output, long? Reasoning);
}
