#:project ../src/Collectors/Orisumi.Collectors.Claude/Orisumi.Collectors.Claude.csproj
#:property PublishAot=false
#:property JsonSerializerIsReflectionEnabledByDefault=true

using System.Text.Json;
using Microsoft.Data.Sqlite;
using Orisumi.Collectors.Claude;
using Orisumi.Platform;

var originalHome = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
try
{
    using var fixture = new Fixture();
    Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", fixture.Home);
    var titleA = Title("A");
    File.WriteAllText(fixture.Log, titleA);
    ClaudeCollector.Capture(fixture.Store);
    Equal(fixture.Number("SELECT COUNT(*) FROM source_records"), 0L, "An unterminated record must wait for its newline");
    File.AppendAllText(fixture.Log, "\n");
    ClaudeCollector.Capture(fixture.Store);
    Equal(fixture.Number("SELECT COUNT(*) FROM source_records"), 1L, "Completed line imported once");
    File.AppendAllText(fixture.Log, Title("B") + "\n" + titleA + "\n");
    ClaudeCollector.Capture(fixture.Store);
    Equal(fixture.Text("SELECT GROUP_CONCAT(title, ',') FROM session_title_observations ORDER BY observation_id"), "A,B,A", "A to B to A preserved");
    var offset = fixture.Number("SELECT byte_offset FROM _source_cursors");
    Equal(offset, new FileInfo(fixture.Log).Length, "Cursor at last complete newline");
    ClaudeCollector.Capture(fixture.Store);
    Equal(fixture.Number("SELECT bytes_read FROM capture_runs ORDER BY run_id DESC LIMIT 1"), 0L, "Unchanged file is not read");

    var renamed = Path.Combine(Path.GetDirectoryName(fixture.Log)!, "moved.jsonl");
    File.Move(fixture.Log, renamed);
    ClaudeCollector.Capture(fixture.Store);
    Equal(fixture.Number("SELECT COUNT(*) FROM source_records"), 3L, "Rename does not replay facts");
    Equal(fixture.Number("SELECT COUNT(*) FROM _source_cursors"), 1L, "Rename keeps physical file identity");
    var replacement = renamed + ".tmp";
    File.Copy(renamed, replacement);
    File.Move(replacement, renamed, overwrite: true);
    ClaudeCollector.Capture(fixture.Store);
    Equal(fixture.Number("SELECT COUNT(*) FROM source_records"), 3L, "Same-path replacement deduplicates copied history");
    Equal(fixture.Number("SELECT COUNT(*) FROM source_file_events WHERE kind='replaced'"), 1L, "Replacement recorded");

    File.WriteAllText(renamed, titleA + "\n");
    ClaudeCollector.Capture(fixture.Store);
    Equal(fixture.Number("SELECT COUNT(*) FROM source_records"), 3L, "Truncated historical prefix does not reapply old title");
    Equal(fixture.Number("SELECT COUNT(*) FROM source_file_events WHERE kind='truncated'"), 1L, "Truncation recorded");
    File.AppendAllText(renamed, Title("C") + "\n{broken}\n");
    ExpectFailure(() => ClaudeCollector.Capture(fixture.Store));
    Equal(fixture.Number("SELECT COUNT(*) FROM source_records"), 3L, "Failed import rolls back earlier records");
    Equal(fixture.Number("SELECT byte_offset FROM _source_cursors"), (long)System.Text.Encoding.UTF8.GetByteCount(titleA + "\n"), "Failed import retains committed cursor");
    Equal(fixture.Number("SELECT COUNT(*) FROM capture_coverage WHERE evidence='read_failed'"), 1L, "Failure evidence survives rollback");
    File.WriteAllText(renamed, titleA + "\n" + Title("C") + "\n");
    ClaudeCollector.Capture(fixture.Store);
    Equal(fixture.Number("SELECT COUNT(*) FROM source_records"), 4L, "Retry imports the valid new record once");

    fixture.Execute("CREATE TRIGGER reject_cursor BEFORE UPDATE ON _source_cursors BEGIN SELECT RAISE(ABORT,'cursor failure'); END");
    File.AppendAllText(renamed, Title("D") + "\n");
    ExpectFailure(() => ClaudeCollector.Capture(fixture.Store));
    Equal(fixture.Number("SELECT COUNT(*) FROM source_records"), 4L, "Cursor commit failure rolls back facts");
    fixture.Execute("DROP TRIGGER reject_cursor");
    ClaudeCollector.Capture(fixture.Store);
    Equal(fixture.Number("SELECT COUNT(*) FROM source_records"), 5L, "Retry after cursor failure succeeds once");

    File.Move(renamed, fixture.Log);
    ClaudeCollector.Capture(fixture.Store);
    var archived = Path.Combine(Path.GetDirectoryName(fixture.Log)!, "z-archive.jsonl");
    File.Move(fixture.Log, archived);
    File.WriteAllText(fixture.Log, Title("E") + "\n");
    ClaudeCollector.Capture(fixture.Store);
    Equal(fixture.Number("SELECT COUNT(*) FROM _source_cursors"), 2L, "Rotation retains old file and creates new file cursor");
    Equal(fixture.Text("SELECT title FROM session_title_observations ORDER BY observation_id DESC LIMIT 1"), "E", "Rotated archive cannot overwrite new facts");

    using var watchStop = new CancellationTokenSource();
    var watching = ClaudeCollector.Watch(fixture.Store, watchStop.Token);
    await Until(() => fixture.Number("SELECT COUNT(*) FROM capture_runs WHERE stopped_at_utc_ms IS NULL") == 0);
    File.AppendAllText(fixture.Log, Title("F") + "\n");
    await Until(() => fixture.Text("SELECT title FROM session_title_observations ORDER BY observation_id DESC LIMIT 1") == "F");
    await Task.Delay(350);
    var attempts = fixture.Number("SELECT COUNT(*) FROM capture_runs");
    await Task.Delay(350);
    Equal(fixture.Number("SELECT COUNT(*) FROM capture_runs"), attempts, "Watch stays idle without source changes");
    watchStop.Cancel();
    await watching;

    var outside = Path.Combine(fixture.Home, "parked.jsonl");
    File.Move(fixture.Log, outside);
    ClaudeCollector.Capture(fixture.Store);
    Equal(fixture.Number("SELECT COUNT(*) FROM source_file_events WHERE kind='missing'"), 1L, "Missing source has explicit evidence");
    ClaudeCollector.Capture(fixture.Store);
    Equal(fixture.Number("SELECT COUNT(*) FROM source_file_events WHERE kind='missing'"), 1L, "Missing state is not repeated");
    File.Move(outside, fixture.Log);
    ClaudeCollector.Capture(fixture.Store);
    Equal(fixture.Number("SELECT COUNT(*) FROM source_file_events WHERE kind='resumed'"), 1L, "Returning file resumes its identity");
    var beforeRewrite = fixture.Number("SELECT COUNT(*) FROM source_records");
    File.WriteAllText(fixture.Log, Title("E") + "\n" + Title("G") + "\n");
    ClaudeCollector.Capture(fixture.Store);
    Equal(fixture.Text("SELECT title FROM session_title_observations ORDER BY observation_id DESC LIMIT 1"), "G", "Same-size rewrite is observed");
    Equal(fixture.Number("SELECT COUNT(*) FROM source_records"), beforeRewrite + 1, "Rewrite replays existing records without duplicates");
    Equal(fixture.Number("SELECT COUNT(*) FROM source_file_events WHERE kind='rewritten'"), 1L, "Rewrite records discontinuity");

    File.Delete(new Store(fixture.Store).CurrentDatabase("claude")!);
    ClaudeCollector.Capture(fixture.Store);
    Equal(fixture.Number("SELECT COUNT(*) FROM _source_cursors"), 2L, "Output database replacement establishes new cursors");
    Equal(fixture.Number("SELECT COUNT(*) FROM capture_runs"), 1L, "Rebuilt output records fresh import");

    using var recoveryStop = new CancellationTokenSource();
    var beforeWatching = fixture.Number("SELECT COUNT(*) FROM capture_runs");
    var recovering = ClaudeCollector.Watch(fixture.Store, recoveryStop.Token);
    await Until(() => fixture.Number("SELECT COUNT(*) FROM capture_runs WHERE stopped_at_utc_ms IS NOT NULL") > beforeWatching);
    Directory.Move(fixture.Home, fixture.Home + "-away");
    await Until(() => fixture.Number("SELECT COUNT(*) FROM capture_runs WHERE error IS NOT NULL") > 0);
    File.AppendAllText(Path.Combine(fixture.Home + "-away", "projects/project/session.jsonl"), Title("H") + "\n");
    Directory.Move(fixture.Home + "-away", fixture.Home);
    await Until(() => fixture.Text("SELECT title FROM session_title_observations ORDER BY observation_id DESC LIMIT 1") == "H");
    File.AppendAllText(fixture.Log, Title("I") + "\n");
    await Until(() => fixture.Text("SELECT title FROM session_title_observations ORDER BY observation_id DESC LIMIT 1") == "I");
    recoveryStop.Cancel();
    await recovering;

    using var large = new Fixture();
    Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", large.Home);
    File.WriteAllText(large.Log, JsonSerializer.Serialize(new { type = "ignored", content = new string('x', 300_000) }) + "\n" + titleA + "\n");
    ClaudeCollector.Capture(large.Store);
    File.AppendAllText(large.Log, Title("B") + "\n");
    ClaudeCollector.Capture(large.Store);
    Equal(large.Number("SELECT records_read FROM capture_runs ORDER BY run_id DESC LIMIT 1"), 1L, "Append parses only the new record");
    if (large.Number("SELECT bytes_read FROM capture_runs ORDER BY run_id DESC LIMIT 1") > 17_000)
        throw new Exception("Append reread more than the new line and bounded checkpoints");

    File.AppendAllLines(large.Log, new[] { (Uuid: (string?)null, Type: "text"), (Uuid: "hidden", Type: "tool_use") }
        .Select(record => JsonSerializer.Serialize(new
        {
            type = "assistant", sessionId = "usage-only", uuid = record.Uuid, timestamp = "2026-01-01T00:00:00Z",
            message = new { id = record.Type, model = "model", content = new[] { new { type = record.Type, text = "ignored" } },
                usage = new { input_tokens = 1, output_tokens = 2 } },
        })));
    ClaudeCollector.Capture(large.Store);
    Equal(large.Number("SELECT COUNT(*) FROM response_token_usage_observations"), 2L, "Usage survives missing UUID or invisible content");
    Equal(large.Number("SELECT COUNT(*) FROM messages"), 0L, "Usage-only records do not create visible messages");
    Console.WriteLine("Claude incremental, replay, rollback and watch checks passed.");
}
finally { Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", originalHome); }

static string Title(string title) => JsonSerializer.Serialize(new { type = "custom-title", sessionId = "session", customTitle = title });
static void Equal<T>(T actual, T expected, string message)
{
    if (!EqualityComparer<T>.Default.Equals(actual, expected)) throw new Exception($"{message}: expected {expected}, got {actual}");
}
static void ExpectFailure(Action operation)
{
    try { operation(); }
    catch (IOException) { return; }
    throw new Exception("Expected capture failure");
}
static async Task Until(Func<bool> condition)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
    while (!condition()) await Task.Delay(30, timeout.Token);
}

sealed class Fixture : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "orisumi-claude-incremental-" + Guid.NewGuid());
    public string Home => Path.Combine(root, "source");
    public string Store => Path.Combine(root, "store");
    public string Log => Path.Combine(Home, "projects", "project", "session.jsonl");
    public Fixture() => Directory.CreateDirectory(Path.GetDirectoryName(Log)!);
    public long Number(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={new Store(Store).CurrentDatabase("claude")};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }
    public string Text(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={new Store(Store).CurrentDatabase("claude")};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar()) ?? "";
    }
    public void Execute(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={new Store(Store).CurrentDatabase("claude")};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }
    public void Dispose() => Directory.Delete(root, true);
}
