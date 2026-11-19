using Orisumi.Collectors.Storage;
using Orisumi.Platform;
using Microsoft.Data.Sqlite;

namespace Orisumi.Collectors.Codex;

public static partial class CodexCollector
{
    private const string Source = "codex";

    private const string WriteThreads = """
        CREATE TEMP VIEW selected_threads AS SELECT id AS thread_id, coalesce(nullif(name, ''), title) AS title, cwd,
         coalesce(created_at_ms, created_at * 1000) AS created_at_utc_ms, archived FROM source_state.threads;
        INSERT OR IGNORE INTO threads SELECT thread_id, created_at_utc_ms FROM selected_threads;
        INSERT INTO thread_title_observations(thread_id,run_id,title)
         SELECT s.thread_id,$run,s.title FROM selected_threads s
         WHERE (NOT EXISTS (SELECT 1 FROM thread_title_observations o WHERE o.thread_id=s.thread_id)
         OR s.title IS NOT (SELECT title FROM thread_title_observations o WHERE o.thread_id=s.thread_id ORDER BY observation_id DESC LIMIT 1));
        INSERT INTO thread_cwd_observations(thread_id,run_id,cwd)
         SELECT s.thread_id,$run,s.cwd FROM selected_threads s
         WHERE (NOT EXISTS (SELECT 1 FROM thread_cwd_observations o WHERE o.thread_id=s.thread_id)
         OR s.cwd IS NOT (SELECT cwd FROM thread_cwd_observations o WHERE o.thread_id=s.thread_id ORDER BY observation_id DESC LIMIT 1));
        INSERT INTO thread_archive_observations(thread_id,run_id,archived)
         SELECT s.thread_id,$run,s.archived FROM selected_threads s
         WHERE (NOT EXISTS (SELECT 1 FROM thread_archive_observations o WHERE o.thread_id=s.thread_id)
         OR s.archived IS NOT (SELECT archived FROM thread_archive_observations o WHERE o.thread_id=s.thread_id ORDER BY observation_id DESC LIMIT 1));
        """;

    private const string WriteTurns = """
        INSERT OR IGNORE INTO turns SELECT thread_id,turn_id,started_at * 1000 FROM source_history.thread_turns WHERE thread_id IN (SELECT thread_id FROM threads);
        -- The start time does not change; a later read may only fill it in.
        UPDATE turns SET started_at_utc_ms=(SELECT s.started_at * 1000 FROM source_history.thread_turns s WHERE s.thread_id=turns.thread_id AND s.turn_id=turns.turn_id)
         WHERE started_at_utc_ms IS NULL
         AND EXISTS (SELECT 1 FROM source_history.thread_turns s WHERE s.thread_id=turns.thread_id AND s.turn_id=turns.turn_id AND s.started_at IS NOT NULL);
        INSERT INTO turn_status_observations(thread_id,turn_id,run_id,status,completed_at_utc_ms) SELECT s.thread_id,s.turn_id,$run,s.status,s.completed_at * 1000
         FROM source_history.thread_turns s WHERE s.thread_id IN (SELECT thread_id FROM threads)
         AND (NOT EXISTS (SELECT 1 FROM turn_status_observations o WHERE o.thread_id=s.thread_id AND o.turn_id=s.turn_id)
         OR (s.status,s.completed_at * 1000) IS NOT (SELECT status,completed_at_utc_ms FROM turn_status_observations o WHERE o.thread_id=s.thread_id AND o.turn_id=s.turn_id ORDER BY observation_id DESC LIMIT 1));
        """;

    private const string WriteMessages = """
        INSERT OR IGNORE INTO messages SELECT thread_id,turn_id,item_id, CASE item_type WHEN 'userMessage' THEN 'user' ELSE 'assistant' END,
         created_at_ms,rollout_ordinal FROM source_history.thread_items WHERE thread_id IN (SELECT thread_id FROM threads)
         AND item_type IN ('userMessage','agentMessage');
        INSERT INTO message_content_observations(thread_id,turn_id,message_id,run_id,item_json)
         SELECT s.thread_id,s.turn_id,s.item_id,$run,s.item_json FROM source_history.thread_items s
         WHERE s.thread_id IN (SELECT thread_id FROM threads) AND s.item_type IN ('userMessage','agentMessage')
         AND s.item_json IS NOT (SELECT item_json FROM message_content_observations o WHERE (o.thread_id,o.turn_id,o.message_id)=(s.thread_id,s.turn_id,s.item_id) ORDER BY observation_id DESC LIMIT 1);
        """;

    // One capture round, for tests; the resident Watch holds the writer for its lifetime.
    public static string Capture(string store)
    {
        using var writer = new CaptureWriter(store, Source);
        return Capture(store, writer, "startup");
    }

    private static string Capture(string store, CaptureWriter writer, string trigger)
    {
        var codexHome = SourceHome();
        var (threadsChanged, turnsChanged, messagesChanged, usageChanged, unavailable, bytesRead) = writer.Run((command, runId) =>
        {
            var observedStart = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (command.Scalar("SELECT count(*) FROM source_state.threads WHERE history_mode <> 'paginated'") is not 0L)
                throw new IOException("Codex threads include history outside the local paginated message database");
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$run", runId);
            var readCurrent = trigger != "usage_change";
            int threadsChanged = 0, turnsChanged = 0, messagesChanged = 0;
            if (readCurrent)
            {
                command.CommandText = WriteThreads;
                threadsChanged = command.ExecuteNonQuery();
                command.CommandText = WriteTurns;
                turnsChanged = command.ExecuteNonQuery();
                command.CommandText = WriteMessages;
                messagesChanged = command.ExecuteNonQuery();
            }
            command.CommandText = "SELECT id, rollout_path FROM source_state.threads WHERE id IN (SELECT thread_id FROM threads);";
            var rollouts = new List<(string ThreadId, string Path)>();
            using (var reader = command.ExecuteReader())
                while (reader.Read()) rollouts.Add((reader.GetString(0), reader.GetString(1)));
            if (readCurrent)
                command.Execute("INSERT INTO database_observations VALUES($p0,$p1,$p2)", runId, observedStart, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var (usageChanged, unavailable, bytesRead) = CodexUsage.Capture(command.Connection!, command.Transaction!, codexHome, rollouts, runId);
            return (threadsChanged, turnsChanged, messagesChanged, usageChanged, unavailable, bytesRead);
        }, [("trigger", trigger)],
        [("source_state", Path.Combine(codexHome, "state_5.sqlite")), ("source_history", Path.Combine(codexHome, "thread_history_1.sqlite"))]);
        return $"Codex: {threadsChanged} thread row(s), {turnsChanged} turn row(s), {messagesChanged} message row(s), " +
            $"{usageChanged} usage row(s), {bytesRead} rollout bytes read in {new Store(store).CurrentDatabase(Source)}, {unavailable} rollouts unavailable";
    }

    private static string SourceHome()
    {
        var configured = Environment.GetEnvironmentVariable("CODEX_HOME");
        return Path.GetFullPath(string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex") : configured);
    }
}
