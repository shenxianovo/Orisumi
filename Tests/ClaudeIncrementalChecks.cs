#:project ../Collectors/Claude/Orisumi.Collectors.Claude.csproj
#:property PublishAot=false
#:property JsonSerializerIsReflectionEnabledByDefault=true

using System.Text.Json;
using Microsoft.Data.Sqlite;
using Orisumi.Collectors.Claude;
using Orisumi.Core;

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
    Equal(ClaudeCollector.Capture(fixture.Store).Contains("0 records parsed, 0 transcript bytes read"), true, "Unchanged file is not read");

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
    var runsBeforeWatch = fixture.Number("SELECT COUNT(*) FROM capture_runs");
    var watching = ClaudeCollector.Watch(fixture.Store, watchStop.Token);
    await Until(() => fixture.Number("SELECT COUNT(*) FROM capture_runs WHERE stopped_at_utc_ms IS NULL AND confirmed_until_utc_ms IS NOT NULL") == 1);
    Equal(fixture.Number("SELECT COUNT(*) FROM capture_runs WHERE stopped_at_utc_ms IS NULL"), 1L, "Resident run remains open after startup");
    File.AppendAllText(fixture.Log, Title("F") + "\n");
    await Until(() => fixture.Text("SELECT title FROM session_title_observations ORDER BY observation_id DESC LIMIT 1") == "F");
    Equal(fixture.Number("SELECT COUNT(*) FROM capture_runs"), runsBeforeWatch + 1, "Startup and append share one run");
    Equal(fixture.Number("SELECT COUNT(DISTINCT run_id) FROM source_records WHERE observation_id=(SELECT MAX(observation_id) FROM source_records) AND run_id=(SELECT MAX(run_id) FROM capture_runs)"), 1L, "Appended fact belongs to the resident run");
    watchStop.Cancel();
    await watching;
    Equal(fixture.Text("SELECT stop_reason FROM capture_runs ORDER BY run_id DESC LIMIT 1"), "completed", "Cancellation closes the resident run");

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
    await Until(() => fixture.Number("SELECT COUNT(*) FROM capture_runs WHERE stopped_at_utc_ms IS NULL AND confirmed_until_utc_ms IS NOT NULL") == 1);
    var confirmed = fixture.Number("SELECT confirmed_until_utc_ms FROM capture_runs WHERE stopped_at_utc_ms IS NULL");
    Directory.Move(fixture.Home, fixture.Home + "-away");
    await Until(() => fixture.Number("SELECT COUNT(*) FROM capture_read_failures") > 0);
    Equal(fixture.Number("SELECT confirmed_until_utc_ms FROM capture_runs WHERE stopped_at_utc_ms IS NULL"), confirmed,
        "Failed round leaves the resident run open at the previous confirmation");
    Equal(fixture.Number("SELECT COUNT(*) FROM capture_coverage WHERE evidence='read_failed' AND run_id=(SELECT MAX(run_id) FROM capture_runs)"), 1L,
        "Failed round has separate coverage linked to the active run");
    File.AppendAllText(Path.Combine(fixture.Home + "-away", "projects/project/session.jsonl"), Title("H") + "\n");
    Directory.Move(fixture.Home + "-away", fixture.Home);
    await Until(() => fixture.Text("SELECT title FROM session_title_observations ORDER BY observation_id DESC LIMIT 1") == "H");
    File.AppendAllText(fixture.Log, Title("I") + "\n");
    await Until(() => fixture.Text("SELECT title FROM session_title_observations ORDER BY observation_id DESC LIMIT 1") == "I");
    Equal(fixture.Number("SELECT COUNT(*) FROM capture_runs"), beforeWatching + 1, "Failure and recovery share one run");
    Equal(fixture.Number("SELECT confirmed_until_utc_ms FROM capture_runs WHERE stopped_at_utc_ms IS NULL") > confirmed, true,
        "Recovery advances the same confirmation");
    recoveryStop.Cancel();
    await recovering;

    using var large = new Fixture();
    Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", large.Home);
    File.WriteAllText(large.Log, JsonSerializer.Serialize(new { type = "ignored", content = new string('x', 300_000) }) + "\n" + titleA + "\n");
    ClaudeCollector.Capture(large.Store);
    File.AppendAllText(large.Log, Title("B") + "\n");
    var appended = ClaudeCollector.Capture(large.Store);
    Equal(appended.Contains($"1 records parsed, {System.Text.Encoding.UTF8.GetByteCount(Title("B") + "\n")} transcript bytes read"),
        true, "Append parses only the new record");

    File.AppendAllText(large.Log, """
        {"type":"assistant","sessionId":"usage-only","message":{"id":"r1","role":"assistant","model":"model","content":[{"type":"text","text":"No UUID."}],"usage":{"input_tokens":1,"output_tokens":2}}}
        {"type":"assistant","sessionId":"usage-only","uuid":"hidden","parentUuid":null,"message":{"id":"r2","role":"assistant","model":"model","content":[{"type":"tool_use","name":"Read","id":"tool-1","input":{"file_path":"example.txt"}}],"usage":{"input_tokens":1,"output_tokens":2}}}
        """ + "\n");
    ClaudeCollector.Capture(large.Store);
    Equal(large.Number("SELECT COUNT(*) FROM response_token_usage_observations"), 2L, "Usage survives missing UUID or invisible content");
    Equal(large.Number("SELECT COUNT(*) FROM transcript_events"), 1L, "The tool call retains its event without visible text");
    Equal(large.Number("SELECT COUNT(*) FROM message_timeline"), 0L, "Usage-only records do not create visible messages");

    var agentDirectory = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(large.Log)!, "usage-only", "subagents"));
    var agent = Path.Combine(agentDirectory.FullName, "agent-example.jsonl");
    File.WriteAllText(agent, """
        {"type":"user","sessionId":"usage-only","uuid":"agent-root","parentUuid":null,"isSidechain":true,"message":{"role":"user","content":"Read a file."}}
        """ + "\n");
    using var agentStop = new CancellationTokenSource();
    var agentWatch = ClaudeCollector.Watch(large.Store, agentStop.Token);
    try
    {
        await Until(() => large.Number("SELECT COUNT(*) FROM message_timeline WHERE uuid='agent-root' AND agent_id='example'") == 1);
        File.AppendAllText(agent, """
            {"type":"assistant","sessionId":"usage-only","uuid":"agent-call","parentUuid":"agent-root","isSidechain":true,"message":{"id":"r3","role":"assistant","content":[{"type":"tool_use","name":"Read","id":"tool-2","input":{"file_path":"example.txt"}}]}}
            {"type":"user","sessionId":"usage-only","uuid":"agent-result","parentUuid":"agent-call","isSidechain":true,"sourceToolAssistantUUID":"agent-call","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"tool-2","is_error":false,"content":[{"type":"text","text":"sample"}]}]}}
            {"type":"assistant","sessionId":"usage-only","uuid":"agent-reply","parentUuid":"agent-result","isSidechain":true,"message":{"id":"r4","role":"assistant","content":[{"type":"text","text":"Read."}]}}
            """ + "\n");
        await Until(() => large.Text("SELECT visible_parent_uuid FROM message_timeline WHERE uuid='agent-reply'") == "agent-root");
        Equal(large.Number("""
            SELECT COUNT(*) FROM transcript_events JOIN event_contents USING(observation_id)
            WHERE uuid='agent-result' AND json_type(payload_json,'$.is_error')='false'
                AND json_extract(payload_json,'$.content_length')=33 AND json_type(payload_json,'$.content') IS NULL
                AND json_type(payload_json,'$.error_text') IS NULL
            """), 1L, "Appended structured success retains only tool result metadata");
    }
    finally { agentStop.Cancel(); await agentWatch; }
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
