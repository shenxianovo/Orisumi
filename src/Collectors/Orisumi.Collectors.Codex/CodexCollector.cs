using Microsoft.Data.Sqlite;

namespace Orisumi.Collectors.Codex;

public static class CodexCollector
{
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS capture_runs (
         run_id INTEGER PRIMARY KEY, started_at_utc_ms INTEGER NOT NULL, stopped_at_utc_ms INTEGER,
         thread_filter TEXT
        );
        CREATE TABLE IF NOT EXISTS threads (thread_id TEXT PRIMARY KEY NOT NULL, created_at_utc_ms INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS thread_title_observations (
         observation_id INTEGER PRIMARY KEY, thread_id TEXT NOT NULL REFERENCES threads(thread_id),
         run_id INTEGER NOT NULL REFERENCES capture_runs(run_id), title TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS thread_title_observations_by_thread ON thread_title_observations(thread_id, observation_id);
        CREATE TABLE IF NOT EXISTS thread_cwd_observations (
         observation_id INTEGER PRIMARY KEY, thread_id TEXT NOT NULL REFERENCES threads(thread_id),
         run_id INTEGER NOT NULL REFERENCES capture_runs(run_id), cwd TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS thread_cwd_observations_by_thread ON thread_cwd_observations(thread_id, observation_id);
        CREATE TABLE IF NOT EXISTS thread_archive_observations (
         observation_id INTEGER PRIMARY KEY, thread_id TEXT NOT NULL REFERENCES threads(thread_id),
         run_id INTEGER NOT NULL REFERENCES capture_runs(run_id), archived INTEGER NOT NULL);
        CREATE INDEX IF NOT EXISTS thread_archive_observations_by_thread ON thread_archive_observations(thread_id, observation_id);
        CREATE VIEW IF NOT EXISTS current_threads AS SELECT t.*,(SELECT title FROM thread_title_observations o WHERE o.thread_id=t.thread_id ORDER BY observation_id DESC LIMIT 1) AS title,(SELECT cwd FROM thread_cwd_observations o WHERE o.thread_id=t.thread_id ORDER BY observation_id DESC LIMIT 1) AS cwd,(SELECT archived FROM thread_archive_observations o WHERE o.thread_id=t.thread_id ORDER BY observation_id DESC LIMIT 1) AS archived FROM threads t;
        CREATE TABLE IF NOT EXISTS turns (thread_id TEXT NOT NULL REFERENCES threads(thread_id), turn_id TEXT NOT NULL, PRIMARY KEY(thread_id,turn_id));
        CREATE TABLE IF NOT EXISTS turn_timing_observations (observation_id INTEGER PRIMARY KEY, thread_id TEXT NOT NULL,turn_id TEXT NOT NULL,
         run_id INTEGER NOT NULL REFERENCES capture_runs(run_id),started_at_utc_ms INTEGER, completed_at_utc_ms INTEGER, FOREIGN KEY(thread_id,turn_id) REFERENCES turns(thread_id,turn_id));
         CREATE INDEX IF NOT EXISTS turn_timing_observations_by_turn ON turn_timing_observations(thread_id,turn_id,observation_id);
        CREATE TABLE IF NOT EXISTS turn_status_observations (observation_id INTEGER PRIMARY KEY, thread_id TEXT NOT NULL,turn_id TEXT NOT NULL,
         run_id INTEGER NOT NULL REFERENCES capture_runs(run_id),status TEXT NOT NULL, FOREIGN KEY(thread_id,turn_id) REFERENCES turns(thread_id,turn_id));
         CREATE INDEX IF NOT EXISTS turn_status_observations_by_turn ON turn_status_observations(thread_id,turn_id,observation_id);
        CREATE VIEW IF NOT EXISTS current_turns AS SELECT t.*,(SELECT started_at_utc_ms FROM turn_timing_observations o WHERE o.thread_id=t.thread_id AND o.turn_id=t.turn_id ORDER BY observation_id DESC LIMIT 1) AS started_at_utc_ms,(SELECT completed_at_utc_ms FROM turn_timing_observations o WHERE o.thread_id=t.thread_id AND o.turn_id=t.turn_id ORDER BY observation_id DESC LIMIT 1) AS completed_at_utc_ms,(SELECT status FROM turn_status_observations o WHERE o.thread_id=t.thread_id AND o.turn_id=t.turn_id ORDER BY observation_id DESC LIMIT 1) AS status FROM turns t;
        CREATE TABLE IF NOT EXISTS messages (
         thread_id TEXT NOT NULL REFERENCES threads(thread_id), turn_id TEXT NOT NULL,message_id TEXT NOT NULL,
         role TEXT NOT NULL CHECK(role IN ('user','assistant')),created_at_utc_ms INTEGER NOT NULL, rollout_ordinal INTEGER NOT NULL,
         PRIMARY KEY(thread_id,turn_id,message_id));
        CREATE TABLE IF NOT EXISTS message_content_observations (
         observation_id INTEGER PRIMARY KEY, thread_id TEXT NOT NULL,turn_id TEXT NOT NULL,message_id TEXT NOT NULL,
         run_id INTEGER NOT NULL REFERENCES capture_runs(run_id),item_json TEXT NOT NULL,
         FOREIGN KEY(thread_id,turn_id,message_id) REFERENCES messages(thread_id,turn_id,message_id));
        CREATE INDEX IF NOT EXISTS message_content_by_message ON message_content_observations(thread_id,turn_id,message_id,observation_id);
        CREATE VIEW IF NOT EXISTS current_messages AS SELECT m.*,
         (SELECT item_json FROM message_content_observations o WHERE (o.thread_id,o.turn_id,o.message_id)=(m.thread_id,m.turn_id,m.message_id) ORDER BY observation_id DESC LIMIT 1) AS item_json FROM messages m;
        """;

    private const string WriteThreads = """
        CREATE TEMP VIEW selected_threads AS SELECT id AS thread_id, coalesce(nullif(name, ''), title) AS title, cwd,
         coalesce(created_at_ms, created_at * 1000) AS created_at_utc_ms, archived FROM source_state.threads;
        INSERT OR IGNORE INTO threads SELECT thread_id, created_at_utc_ms FROM selected_threads WHERE $thread IS NULL OR thread_id=$thread;
        INSERT INTO thread_title_observations(thread_id,run_id,title)
         SELECT s.thread_id,$run,s.title FROM selected_threads s WHERE ($thread IS NULL OR s.thread_id=$thread)
         AND (NOT EXISTS (SELECT 1 FROM thread_title_observations o WHERE o.thread_id=s.thread_id)
         OR s.title IS NOT (SELECT title FROM thread_title_observations o WHERE o.thread_id=s.thread_id ORDER BY observation_id DESC LIMIT 1));
        INSERT INTO thread_cwd_observations(thread_id,run_id,cwd)
         SELECT s.thread_id,$run,s.cwd FROM selected_threads s WHERE ($thread IS NULL OR s.thread_id=$thread)
         AND (NOT EXISTS (SELECT 1 FROM thread_cwd_observations o WHERE o.thread_id=s.thread_id)
         OR s.cwd IS NOT (SELECT cwd FROM thread_cwd_observations o WHERE o.thread_id=s.thread_id ORDER BY observation_id DESC LIMIT 1));
        INSERT INTO thread_archive_observations(thread_id,run_id,archived)
         SELECT s.thread_id,$run,s.archived FROM selected_threads s WHERE ($thread IS NULL OR s.thread_id=$thread)
         AND (NOT EXISTS (SELECT 1 FROM thread_archive_observations o WHERE o.thread_id=s.thread_id)
         OR s.archived IS NOT (SELECT archived FROM thread_archive_observations o WHERE o.thread_id=s.thread_id ORDER BY observation_id DESC LIMIT 1));
        """;

    private const string WriteTurns = """
        INSERT OR IGNORE INTO turns SELECT thread_id,turn_id FROM source_history.thread_turns WHERE thread_id IN (SELECT thread_id FROM threads) AND ($thread IS NULL OR thread_id=$thread);
        INSERT INTO turn_timing_observations(thread_id,turn_id,run_id,started_at_utc_ms,completed_at_utc_ms) SELECT s.thread_id,s.turn_id,$run,s.started_at * 1000,s.completed_at * 1000
         FROM source_history.thread_turns s WHERE s.thread_id IN (SELECT thread_id FROM threads) AND ($thread IS NULL OR s.thread_id=$thread)
         AND (NOT EXISTS (SELECT 1 FROM turn_timing_observations o WHERE o.thread_id=s.thread_id AND o.turn_id=s.turn_id)
         OR (s.started_at * 1000,s.completed_at * 1000) IS NOT (SELECT started_at_utc_ms,completed_at_utc_ms FROM turn_timing_observations o WHERE o.thread_id=s.thread_id AND o.turn_id=s.turn_id ORDER BY observation_id DESC LIMIT 1));
        INSERT INTO turn_status_observations(thread_id,turn_id,run_id,status) SELECT s.thread_id,s.turn_id,$run,s.status
         FROM source_history.thread_turns s WHERE s.thread_id IN (SELECT thread_id FROM threads) AND ($thread IS NULL OR s.thread_id=$thread)
         AND (NOT EXISTS (SELECT 1 FROM turn_status_observations o WHERE o.thread_id=s.thread_id AND o.turn_id=s.turn_id)
         OR (s.status) IS NOT (SELECT status FROM turn_status_observations o WHERE o.thread_id=s.thread_id AND o.turn_id=s.turn_id ORDER BY observation_id DESC LIMIT 1));
        """;

    private const string WriteMessages = """
        INSERT OR IGNORE INTO messages SELECT thread_id,turn_id,item_id, CASE item_type WHEN 'userMessage' THEN 'user' ELSE 'assistant' END,
         created_at_ms,rollout_ordinal FROM source_history.thread_items WHERE thread_id IN (SELECT thread_id FROM threads)
         AND ($thread IS NULL OR thread_id=$thread) AND item_type IN ('userMessage','agentMessage');
        INSERT INTO message_content_observations(thread_id,turn_id,message_id,run_id,item_json)
         SELECT s.thread_id,s.turn_id,s.item_id,$run,s.item_json FROM source_history.thread_items s
         WHERE s.thread_id IN (SELECT thread_id FROM threads) AND ($thread IS NULL OR s.thread_id=$thread) AND s.item_type IN ('userMessage','agentMessage')
         AND s.item_json IS NOT (SELECT item_json FROM message_content_observations o WHERE (o.thread_id,o.turn_id,o.message_id)=(s.thread_id,s.turn_id,s.item_id) ORDER BY observation_id DESC LIMIT 1);
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
            command.CommandText = "INSERT INTO capture_runs(started_at_utc_ms,thread_filter) VALUES ($started,$thread); SELECT last_insert_rowid();";
            command.Parameters.AddWithValue("$started", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var runId = (long)command.ExecuteScalar()!;
            command.Parameters.AddWithValue("$run", runId);
            command.CommandText = WriteThreads;
            var threadsChanged = command.ExecuteNonQuery();
            command.CommandText = WriteTurns;
            var turnsChanged = command.ExecuteNonQuery();
            command.CommandText = WriteMessages;
            var messagesChanged = command.ExecuteNonQuery();
            command.CommandText = "SELECT id, rollout_path FROM source_state.threads WHERE $thread IS NULL OR id = $thread;";
            var rollouts = new List<(string ThreadId, string Path)>();
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                    rollouts.Add((reader.GetString(0), reader.GetString(1)));
            var (usageChanged, unavailable) = CodexUsage.Capture(connection, transaction, codexHome, rollouts, runId);
            command.CommandText = "UPDATE capture_runs SET stopped_at_utc_ms=$stopped WHERE run_id=$run";
            command.Parameters.AddWithValue("$stopped", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            command.ExecuteNonQuery();
            transaction.Commit();

            var selection = threadId is null ? "Codex all threads" : $"Codex thread {threadId}";
            return $"{selection}: {threadsChanged} thread row(s), {turnsChanged} turn row(s), {messagesChanged} message row(s), " +
                $"{usageChanged} usage row(s) written to {database}, {unavailable} rollouts unavailable";
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
