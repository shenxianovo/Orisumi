using Microsoft.Data.Sqlite;

namespace Orisumi.Collectors.Codex;

public static class CodexCollector
{
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS threads (
            thread_id TEXT PRIMARY KEY NOT NULL,
            title TEXT NOT NULL,
            cwd TEXT NOT NULL,
            created_at_utc_ms INTEGER NOT NULL,
            updated_at_utc_ms INTEGER NOT NULL,
            archived INTEGER NOT NULL CHECK (archived IN (0, 1))
        );

        CREATE TABLE IF NOT EXISTS messages (
            thread_id TEXT NOT NULL REFERENCES threads(thread_id),
            turn_id TEXT NOT NULL,
            message_id TEXT NOT NULL,
            role TEXT NOT NULL CHECK (role IN ('user', 'assistant')),
            created_at_utc_ms INTEGER NOT NULL,
            rollout_ordinal INTEGER NOT NULL,
            content_json TEXT NOT NULL,
            PRIMARY KEY (thread_id, turn_id, message_id)
        );
        CREATE INDEX IF NOT EXISTS messages_by_thread_order ON messages (thread_id, rollout_ordinal);
        """;

    private const string WriteThreads = """
        INSERT INTO threads (thread_id, title, cwd, created_at_utc_ms, updated_at_utc_ms, archived)
        SELECT id, coalesce(nullif(name, ''), title), cwd,
            coalesce(created_at_ms, created_at * 1000), coalesce(updated_at_ms, updated_at * 1000), archived
        FROM source_state.threads
        WHERE $thread IS NULL OR id = $thread
        ON CONFLICT (thread_id) DO UPDATE SET
            title = excluded.title,
            cwd = excluded.cwd,
            created_at_utc_ms = excluded.created_at_utc_ms,
            updated_at_utc_ms = excluded.updated_at_utc_ms,
            archived = excluded.archived
        WHERE (threads.title, threads.cwd, threads.created_at_utc_ms, threads.updated_at_utc_ms, threads.archived)
            IS NOT (excluded.title, excluded.cwd, excluded.created_at_utc_ms, excluded.updated_at_utc_ms, excluded.archived);
        """;

    private const string WriteMessages = """
        INSERT INTO messages (thread_id, turn_id, message_id, role, created_at_utc_ms, rollout_ordinal, content_json)
        SELECT items.thread_id, items.turn_id, items.item_id,
            CASE items.item_type WHEN 'userMessage' THEN 'user' ELSE 'assistant' END,
            items.created_at_ms, items.rollout_ordinal, items.item_json
        FROM source_state.threads AS threads
        JOIN source_history.thread_items AS items ON items.thread_id = threads.id
        WHERE ($thread IS NULL OR threads.id = $thread) AND items.item_type IN ('userMessage', 'agentMessage')
        ON CONFLICT (thread_id, turn_id, message_id) DO UPDATE SET
            role = excluded.role,
            created_at_utc_ms = excluded.created_at_utc_ms,
            rollout_ordinal = excluded.rollout_ordinal,
            content_json = excluded.content_json
        WHERE (messages.role, messages.created_at_utc_ms, messages.rollout_ordinal, messages.content_json)
            IS NOT (excluded.role, excluded.created_at_utc_ms, excluded.rollout_ordinal, excluded.content_json);
        """;

    public static string Capture(string store, string? threadId)
    {
        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (string.IsNullOrWhiteSpace(codexHome))
            codexHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        var stateUri = ReadOnlySourceUri(codexHome, "state_5.sqlite");
        var historyUri = ReadOnlySourceUri(codexHome, "thread_history_1.sqlite");
        var database = Path.GetFullPath(Path.Combine(store, "raw", "codex.sqlite"));
        Directory.CreateDirectory(Path.GetDirectoryName(database)!);
        var fileOptions = new FileStreamOptions
        {
            Mode = FileMode.OpenOrCreate,
            Access = FileAccess.Write,
            Share = FileShare.ReadWrite,
        };
        if (!OperatingSystem.IsWindows())
            fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (File.Open(database, fileOptions)) { }

        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                // Open in URI mode so SQLite honors mode=ro on both ATTACH paths.
                DataSource = new Uri(database).AbsoluteUri,
                Mode = SqliteOpenMode.ReadWrite,
                ForeignKeys = true,
                Pooling = false,
            }.ToString());
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = "ATTACH DATABASE $state AS source_state; ATTACH DATABASE $history AS source_history;";
            command.Parameters.AddWithValue("$state", stateUri);
            command.Parameters.AddWithValue("$history", historyUri);
            command.ExecuteNonQuery();
            command.Parameters.Clear();

            using var transaction = connection.BeginTransaction();
            command.Transaction = transaction;
            command.Parameters.AddWithValue("$thread", (object?)threadId ?? DBNull.Value);
            command.CommandText = """
                SELECT count(*), count(*) FILTER (WHERE history_mode <> 'paginated')
                FROM source_state.threads WHERE $thread IS NULL OR id = $thread;
                """;
            using (var threads = command.ExecuteReader())
            {
                threads.Read();
                if (threadId is not null && threads.GetInt64(0) == 0)
                    throw new IOException("the Codex thread was not found in local storage");
                if (threads.GetInt64(1) > 0)
                    throw new IOException("the selected threads include history outside the local paginated message database");
            }

            command.CommandText = Schema;
            command.ExecuteNonQuery();
            command.CommandText = WriteThreads;
            var threadsChanged = command.ExecuteNonQuery();
            command.CommandText = WriteMessages;
            var messagesChanged = command.ExecuteNonQuery();
            transaction.Commit();

            var selection = threadId is null ? "Codex all threads" : $"Codex thread {threadId}";
            return $"{selection}: {threadsChanged} thread row(s), {messagesChanged} message row(s) written to {database}";
        }
        catch (SqliteException error)
        {
            throw new IOException($"could not capture Codex thread: {error.Message}", error);
        }
    }

    private static string ReadOnlySourceUri(string directory, string filename)
    {
        var path = Path.GetFullPath(Path.Combine(directory, filename));
        if (!File.Exists(path))
            throw new FileNotFoundException($"Codex source database not found: {path}");
        return new Uri(path).AbsoluteUri + "?mode=ro";
    }
}
