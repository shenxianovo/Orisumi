using Orisumi.Collectors.Storage;
using Microsoft.Data.Sqlite;

namespace Orisumi.Collectors.Codex;

public static partial class CodexCollector
{
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
        INSERT OR IGNORE INTO turns SELECT thread_id,turn_id,started_at * 1000 FROM source_history.thread_turns WHERE thread_id IN (SELECT thread_id FROM threads) AND ($thread IS NULL OR thread_id=$thread);
        -- The start time does not change; a later read may only fill it in.
        UPDATE turns SET started_at_utc_ms=(SELECT s.started_at * 1000 FROM source_history.thread_turns s WHERE s.thread_id=turns.thread_id AND s.turn_id=turns.turn_id)
         WHERE started_at_utc_ms IS NULL AND ($thread IS NULL OR thread_id=$thread)
         AND EXISTS (SELECT 1 FROM source_history.thread_turns s WHERE s.thread_id=turns.thread_id AND s.turn_id=turns.turn_id AND s.started_at IS NOT NULL);
        INSERT INTO turn_status_observations(thread_id,turn_id,run_id,status,completed_at_utc_ms) SELECT s.thread_id,s.turn_id,$run,s.status,s.completed_at * 1000
         FROM source_history.thread_turns s WHERE s.thread_id IN (SELECT thread_id FROM threads) AND ($thread IS NULL OR s.thread_id=$thread)
         AND (NOT EXISTS (SELECT 1 FROM turn_status_observations o WHERE o.thread_id=s.thread_id AND o.turn_id=s.turn_id)
         OR (s.status,s.completed_at * 1000) IS NOT (SELECT status,completed_at_utc_ms FROM turn_status_observations o WHERE o.thread_id=s.thread_id AND o.turn_id=s.turn_id ORDER BY observation_id DESC LIMIT 1));
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
        using var captureLock = Lock(store);
        return CaptureCore(store, threadId, "manual");
    }

    private static string CaptureCore(string store, string? threadId, string trigger)
    {
        var codexHome = SourceHome();
        var database = Path.GetFullPath(Path.Combine(store, "raw", "codex.sqlite"));
        using var connection = OpenDatabase(database);
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO capture_runs(started_at_utc_ms,scope,thread_filter,trigger) VALUES($started,CASE WHEN $thread IS NULL THEN 'all' ELSE 'thread' END,$thread,$trigger); SELECT last_insert_rowid();";
        command.Parameters.AddWithValue("$started", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$thread", (object?)threadId ?? DBNull.Value);
        command.Parameters.AddWithValue("$trigger", trigger);
        var runId = (long)command.ExecuteScalar()!;
        try
        {
            command.CommandText = "ATTACH DATABASE $state AS source_state; ATTACH DATABASE $history AS source_history;";
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$state", ReadOnlySourceUri(codexHome, "state_5.sqlite"));
            command.Parameters.AddWithValue("$history", ReadOnlySourceUri(codexHome, "thread_history_1.sqlite"));
            command.ExecuteNonQuery();
            command.Parameters.Clear();
            int threadsChanged, turnsChanged, messagesChanged, usageChanged, unavailable;
            long bytesRead;
            using (var transaction = connection.BeginTransaction())
            {
                command.Transaction = transaction;
                command.Parameters.AddWithValue("$thread", (object?)threadId ?? DBNull.Value);
                var observedStart = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
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
                command.Parameters.AddWithValue("$run", runId);
                var readCurrent = trigger != "usage_change";
                threadsChanged = turnsChanged = messagesChanged = 0;
                if (readCurrent)
                {
                    command.CommandText = WriteThreads;
                    threadsChanged = command.ExecuteNonQuery();
                    command.CommandText = WriteTurns;
                    turnsChanged = command.ExecuteNonQuery();
                    command.CommandText = WriteMessages;
                    messagesChanged = command.ExecuteNonQuery();
                }
                command.CommandText = "SELECT id, rollout_path FROM source_state.threads WHERE ($thread IS NULL OR id = $thread) AND id IN (SELECT thread_id FROM threads);";
                var rollouts = new List<(string ThreadId, string Path)>();
                using (var reader = command.ExecuteReader())
                    while (reader.Read()) rollouts.Add((reader.GetString(0), reader.GetString(1)));
                if (readCurrent)
                {
                    command.CommandText = "INSERT INTO database_observations VALUES($run,$from,$to)";
                    command.Parameters.AddWithValue("$from", observedStart);
                    command.Parameters.AddWithValue("$to", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    command.ExecuteNonQuery();
                }
                (usageChanged, unavailable, bytesRead) = CodexUsage.Capture(connection, transaction, codexHome, rollouts, runId);
                transaction.Commit();
            }
            Finish(connection, runId, null);
            var selection = threadId is null ? "Codex all threads" : $"Codex thread {threadId}";
            return $"{selection}: {threadsChanged} thread row(s), {turnsChanged} turn row(s), {messagesChanged} message row(s), " +
                $"{usageChanged} usage row(s), {bytesRead} rollout bytes read in {database}, {unavailable} rollouts unavailable";
        }
        catch (Exception error)
        {
            Finish(connection, runId, error.Message);
            if (error is SqliteException) throw new IOException($"could not capture Codex thread: {error.Message}", error);
            throw;
        }
    }

    private static void Finish(SqliteConnection connection, long runId, string? error)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE capture_runs SET stopped_at_utc_ms=$end,stop_reason=CASE WHEN $error IS NULL THEN 'completed' ELSE 'failed' END,error=$error WHERE run_id=$run";
        command.Parameters.AddWithValue("$end", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("$run", runId);
        command.ExecuteNonQuery();
    }

    private static SqliteConnection OpenDatabase(string path)
    {
        SqliteFile.CreateIfMissing(path);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = new Uri(path).AbsoluteUri, Mode = SqliteOpenMode.ReadWrite,
            ForeignKeys = true, Pooling = false,
        }.ToString());
        try { connection.Open(); SqliteSchema.EnsureCreated(connection); return connection; }
        catch { connection.Dispose(); throw; }
    }

    private static FileStream Lock(string store)
    {
        var directory = Path.GetFullPath(Path.Combine(store, "raw"));
        Directory.CreateDirectory(directory);
        return new FileStream(Path.Combine(directory, "codex.sqlite.capture.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    private static string SourceHome()
    {
        var configured = Environment.GetEnvironmentVariable("CODEX_HOME");
        return Path.GetFullPath(string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex") : configured);
    }

    private static string ReadOnlySourceUri(string directory, string filename)
    {
        var path = Path.GetFullPath(Path.Combine(directory, filename));
        if (!File.Exists(path)) throw new FileNotFoundException($"Codex source database not found: {path}");
        return new Uri(path).AbsoluteUri + "?mode=ro";
    }
}
