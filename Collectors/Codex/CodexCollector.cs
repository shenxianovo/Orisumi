using Microsoft.Data.Sqlite;
using Orisumi.Capture.Hosting;
using Orisumi.Capture.Storage;
using Orisumi.Core;

namespace Orisumi.Collectors.Codex;

public static partial class CodexCollector
{
    private const string Source = "codex";
    internal static readonly Collector Collector = new(Source, typeof(CodexCollector).Assembly);
    private static readonly string[] RolloutDirectories = ["sessions", "archived_sessions"];

    // One capture round, for tests; the resident Watch holds the writer for its lifetime.
    public static string Capture(string store)
    {
        using var writer = new CaptureWriter(store, Collector);
        return Capture(store, writer, SourceHome(), reconcile: true);
    }

    private static string Capture(string store, CaptureWriter writer, string codexHome, bool reconcile, CancellationToken stop = default)
    {
        var state = Path.Combine(codexHome, "state_5.sqlite");
        var hasState = File.Exists(state);
        var (events, usage, bytesRead) = writer.Run((command, runId) => new RolloutReader(command, runId, reconcile, stop).Read(codexHome));
        stop.ThrowIfCancellationRequested();
        try
        {
            writer.Run((command, runId) =>
            {
                CaptureCurrent(command, runId, hasState);
                return 0;
            }, hasState ? [("source_state", state)] : [], confirm: false, failureScope: "current_state");
        }
        catch (IOException) { } // The round records its own failure; rollout facts are already committed.
        return $"Codex: {events} rollout events, {usage} usage events, {bytesRead} rollout bytes read in {new Store(store).CurrentDatabase(Source)}";
    }

    private static void CaptureCurrent(SqliteCommand command, long runId, bool hasState)
    {
        var started = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        command.Execute(hasState ? """
            CREATE TEMP VIEW thread_snapshot AS
            SELECT t.thread_id,CASE WHEN s.id IS NOT NULL THEN COALESCE(s.name,'') END AS name,s.archived,s.is_pinned
            FROM threads t LEFT JOIN source_state.threads s ON s.id=t.thread_id;
            """ : """
            CREATE TEMP VIEW thread_snapshot AS
            SELECT thread_id,NULL AS name,NULL AS archived,NULL AS is_pinned FROM threads;
            """);
        var observation = command.Scalar("""
            INSERT INTO database_observations(run_id,started_at_utc_ms,stopped_at_utc_ms)
            VALUES($p0,$p1,$p1) RETURNING observation_id
            """, runId, started);
        int Observe(string table, string column) => command.Execute($"""
            INSERT INTO {table}(thread_id,database_observation_id,{column})
            SELECT s.thread_id,$p0,s.{column} FROM thread_snapshot s
            WHERE s.{column} IS NOT (
                SELECT o.{column} FROM {table} o WHERE o.thread_id=s.thread_id ORDER BY observation_id DESC LIMIT 1
            )
            """, observation);
        var changes = Observe("thread_name_observations", "name") +
            Observe("thread_archive_observations", "archived") + Observe("thread_pin_observations", "is_pinned");
        if (changes > 0)
            command.Execute("UPDATE database_observations SET stopped_at_utc_ms=$p0 WHERE observation_id=$p1",
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), observation);
        else
            command.Execute("DELETE FROM database_observations WHERE observation_id=$p0", observation);
    }

    private static string SourceHome()
    {
        var configured = Environment.GetEnvironmentVariable("CODEX_HOME");
        return Path.GetFullPath(string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex") : configured);
    }
}
