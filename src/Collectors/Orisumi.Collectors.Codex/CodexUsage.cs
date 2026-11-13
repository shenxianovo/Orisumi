using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ZstdSharp;

namespace Orisumi.Collectors.Codex;

internal static class CodexUsage
{
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS response_token_usage_observations (
            observation_id INTEGER PRIMARY KEY,
            run_id INTEGER NOT NULL REFERENCES capture_runs(run_id),
            source_line INTEGER NOT NULL,
            thread_id TEXT NOT NULL REFERENCES threads(thread_id),
            response_id TEXT NOT NULL,
            turn_id TEXT,
            recorded_at_utc_ms INTEGER NOT NULL,
            model TEXT,
            total_input_tokens INTEGER NOT NULL,
            cache_read_input_tokens INTEGER,
            cache_write_input_tokens INTEGER,
            total_output_tokens INTEGER NOT NULL,
            reasoning_output_tokens INTEGER,
            UNIQUE (thread_id, source_line)
        );
        CREATE INDEX IF NOT EXISTS response_token_usage_by_turn ON response_token_usage_observations (thread_id, response_id, observation_id);
        CREATE VIEW IF NOT EXISTS response_token_usage AS SELECT o.* FROM response_token_usage_observations o
        WHERE observation_id=(SELECT MAX(n.observation_id) FROM response_token_usage_observations n WHERE n.thread_id=o.thread_id AND n.response_id=o.response_id);
        CREATE TABLE IF NOT EXISTS _token_usage_rollouts (
            thread_id TEXT PRIMARY KEY NOT NULL REFERENCES threads(thread_id),
            path TEXT NOT NULL,
            length INTEGER NOT NULL,
            modified_ticks INTEGER NOT NULL
        );
        """;

    private const string WriteUsage = """
        INSERT OR IGNORE INTO response_token_usage_observations
        (thread_id,response_id,turn_id,recorded_at_utc_ms,model,total_input_tokens,cache_read_input_tokens,
        cache_write_input_tokens,total_output_tokens,reasoning_output_tokens,run_id,source_line)
        VALUES ($thread,$response,$turn,$observed,$model,$input,$cached,$cacheWrite,$output,$reasoning,$run,$line);
        """;

    internal static (int Written, int Unavailable) Capture(SqliteConnection connection, SqliteTransaction transaction,
        string codexHome, IReadOnlyList<(string ThreadId, string Path)> rollouts, long runId)
    {
        using var state = connection.CreateCommand();
        state.Transaction = transaction;
        state.CommandText = Schema;
        state.ExecuteNonQuery();
        state.CommandText = "SELECT thread_id, path, length, modified_ticks FROM _token_usage_rollouts;";
        var imported = new Dictionary<string, (string Path, long Length, long Modified)>(StringComparer.Ordinal);
        using (var reader = state.ExecuteReader())
            while (reader.Read())
                imported.Add(reader.GetString(0), (reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3)));

        state.CommandText = """
            INSERT INTO _token_usage_rollouts VALUES ($thread, $path, $length, $modified)
            ON CONFLICT (thread_id) DO UPDATE SET path = excluded.path, length = excluded.length,
                modified_ticks = excluded.modified_ticks;
            """;
        foreach (var name in new[] { "$thread", "$path", "$length", "$modified" })
            state.Parameters.AddWithValue(name, DBNull.Value);

        using var write = connection.CreateCommand();
        write.Transaction = transaction;
        write.CommandText = WriteUsage;
        foreach (var name in new[] { "$thread", "$response", "$turn", "$observed", "$model", "$input", "$cached",
                     "$cacheWrite", "$output", "$reasoning" })
            write.Parameters.AddWithValue(name, DBNull.Value);

        write.Parameters.AddWithValue("$run", runId);
        write.Parameters.AddWithValue("$line", 0);
        Dictionary<string, string>? archivedPaths = null;
        var written = 0;
        var unavailable = 0;
        foreach (var (threadId, storedPath) in rollouts)
        {
            var path = storedPath;
            if (!File.Exists(path))
            {
                archivedPaths ??= FindRollouts(codexHome);
                if (!archivedPaths.TryGetValue(threadId, out path))
                {
                    unavailable++;
                    continue;
                }
            }

            try
            {
                var file = new FileInfo(path);
                var fingerprint = (Path: file.FullName, Length: file.Length, Modified: file.LastWriteTimeUtc.Ticks);
                if (imported.TryGetValue(threadId, out var previous) && previous == fingerprint)
                    continue;

                var (changes, complete) = ReadRollout(file, threadId, write);
                written += changes;
                file.Refresh();
                if (complete && file.Exists && file.Length == fingerprint.Length && file.LastWriteTimeUtc.Ticks == fingerprint.Modified)
                {
                    Bind(state, threadId, fingerprint.Path, fingerprint.Length, fingerprint.Modified);
                    state.ExecuteNonQuery();
                }
            }
            catch (IOException error) when (error is FileNotFoundException or DirectoryNotFoundException)
            {
                // A rollout can be moved to the archive between resolving and opening it.
                unavailable++;
            }
        }
        return (written, unavailable);
    }

    private static Dictionary<string, string> FindRollouts(string codexHome)
    {
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in new[] { "sessions", "archived_sessions" })
        {
            var root = Path.Combine(codexHome, directory);
            if (!Directory.Exists(root))
                continue;
            foreach (var path in Directory.EnumerateFiles(root, "rollout-*.jsonl*", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(path);
                if (name.EndsWith(".zst", StringComparison.Ordinal))
                    name = Path.GetFileNameWithoutExtension(name);
                if (!name.EndsWith(".jsonl", StringComparison.Ordinal))
                    continue;
                var stem = Path.GetFileNameWithoutExtension(name);
                if (stem.Length >= 36 && Guid.TryParseExact(stem.AsSpan(stem.Length - 36), "D", out var id))
                    paths.TryAdd(id.ToString(), path);
            }
        }
        return paths;
    }

    private static (int Written, bool Complete) ReadRollout(FileInfo file, string threadId, SqliteCommand write)
    {
        var compressed = file.Extension == ".zst";
        using var source = file.Open(FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var decoded = compressed ? new DecompressionStream(source) : null;
        using var reader = new StreamReader(decoded ?? (Stream)source, bufferSize: 64 * 1024);
        var models = new Dictionary<string, string>(StringComparer.Ordinal);
        var written = 0;
        var lineNumber = 0;
        while (reader.ReadLine() is { } line)
        {
            write.Parameters["$line"].Value = ++lineNumber;
            if (!line.Contains("\"token_usage_record\"", StringComparison.Ordinal) &&
                !line.Contains("\"turn_context\"", StringComparison.Ordinal))
                continue;
            try
            {
                using var document = JsonDocument.Parse(line);
                var entry = document.RootElement;
                if (!entry.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
                    continue;
                var turn = ReadString(payload, "turn_id");
                var kind = ReadString(entry, "type");
                if (kind == "turn_context" && turn is not null && ReadString(payload, "model") is { } model)
                    models[turn] = model;
                if (kind != "token_usage_record" || ReadString(payload, "thread_id") != threadId ||
                    ReadString(payload, "response_id") is not { Length: > 0 } response ||
                    !payload.TryGetProperty("usage", out var usage) ||
                    ReadCount(usage, "input_tokens") is not long input || ReadCount(usage, "output_tokens") is not long output ||
                    !DateTimeOffset.TryParse(ReadString(entry, "timestamp"), CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal, out var observed))
                    continue;

                Bind(write, threadId, response, turn, observed.ToUnixTimeMilliseconds(),
                    turn is null ? null : models.GetValueOrDefault(turn), input, ReadCount(usage, "cached_input_tokens"),
                    ReadCount(usage, "cache_write_input_tokens"), output, ReadCount(usage, "reasoning_output_tokens"));
                written += write.ExecuteNonQuery();
            }
            catch (JsonException error)
            {
                if (!compressed && reader.EndOfStream && !EndsAtLineBoundary(source))
                    return (written, false);
                throw new InvalidDataException($"invalid Codex usage event in {file.Name}", error);
            }
        }

        // A live writer can leave an incomplete last line that has not reached the type field yet.
        return (written, compressed || EndsAtLineBoundary(source));
    }

    private static bool EndsAtLineBoundary(FileStream source)
    {
        Span<byte> last = stackalloc byte[1];
        var length = source.Length;
        return length == 0 || (RandomAccess.Read(source.SafeFileHandle, last, length - 1) == 1 && last[0] == '\n');
    }

    private static void Bind(SqliteCommand command, params object?[] values)
    {
        for (var index = 0; index < values.Length; index++)
            command.Parameters[index].Value = values[index] ?? DBNull.Value;
    }

    private static string? ReadString(JsonElement value, string key) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var field) &&
        field.ValueKind == JsonValueKind.String ? field.GetString() : null;

    private static long? ReadCount(JsonElement value, string key) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var field) &&
        field.ValueKind == JsonValueKind.Number && field.TryGetInt64(out var count) ? count : null;
}
