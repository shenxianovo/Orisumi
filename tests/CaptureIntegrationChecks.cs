#:project ../src/Orisumi.Cli/Orisumi.Cli.csproj
#:property PublishAot=false
#:property JsonSerializerIsReflectionEnabledByDefault=true

using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Orisumi.Collectors.Claude;
using Orisumi.Collectors.Codex;

var root = Directory.GetParent(Path.GetDirectoryName(SourceFile())!)!.FullName;
var cli = Path.Combine(root, "src/Orisumi.Cli/bin/Debug/net11.0/Orisumi");
await CodexChecks();
await ClaudeChecks();
await TabChecks();
await DownloadChecks();
await BookmarkChecks();
using (var corrupt = new Fixture())
{
    Directory.CreateDirectory(Path.Combine(corrupt.Store, "raw"));
    File.WriteAllText(corrupt.Database("browser"), "invalid database contents");
    await Browser(corrupt, new() { ["getDatabaseId"] = true }, expectedOk: false);
    Check(File.ReadAllText(corrupt.Database("browser")) == "invalid database contents", "Corrupt database was modified");
}
if (OperatingSystem.IsMacOS()) await MacOSChecks();
Console.WriteLine("Codex, Claude, browser and macOS capture integration checks passed.");

static string SourceFile([CallerFilePath] string path = "") => path;
static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
static void Exec(SqliteConnection db, string sql, params object?[] values)
{
    using var command = db.CreateCommand(); command.CommandText = sql;
    for (var i = 0; i < values.Length; i++) command.Parameters.AddWithValue($"$p{i}", values[i] ?? DBNull.Value);
    command.ExecuteNonQuery();
}
static void Rows(SqliteConnection db, string sql, params object?[][] expected)
{
    using var command = db.CreateCommand(); command.CommandText = sql;
    using var reader = command.ExecuteReader();
    var index = 0;
    while (reader.Read())
    {
        Check(index < expected.Length && reader.FieldCount == expected[index].Length, $"Unexpected row shape: {sql}");
        for (var column = 0; column < reader.FieldCount; column++)
        {
            var actual = reader.IsDBNull(column) ? null : Convert.ToString(reader.GetValue(column), CultureInfo.InvariantCulture);
            var value = expected[index][column] is null ? null : Convert.ToString(expected[index][column], CultureInfo.InvariantCulture);
            Check(actual == value, $"Unexpected row {index}, column {column}: {sql}; expected {value ?? "null"}, got {actual ?? "null"}");
        }
        index++;
    }
    Check(index == expected.Length, $"Missing rows: {sql}");
}
static void Count(SqliteConnection db, string table, int expected) => Rows(db, $"SELECT COUNT(*) FROM {table}", [expected]);
static SqliteConnection Open(string path)
{
    var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
    db.Open();
    return db;
}
// One capture round in process; the collectors read their source location from the environment.
static void Capture(Fixture fixture, Func<string, string> capture)
{
    Environment.SetEnvironmentVariable("CODEX_HOME", fixture.Root);
    Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", fixture.Root);
    try { capture(fixture.Store); }
    finally
    {
        Environment.SetEnvironmentVariable("CODEX_HOME", null);
        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", null);
    }
}
async Task Browser(Fixture fixture, JsonObject batch, bool expectedOk = true)
{
    batch["profileId"] = "11111111-1111-4111-8111-111111111111";
    var payload = JsonSerializer.SerializeToUtf8Bytes(batch);
    var frame = new byte[payload.Length + 4];
    BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length);
    payload.CopyTo(frame, 4);
    var reply = await Run(cli, ["--store", fixture.Store, "browser", "native-host"], frame);
    Check(reply.Length >= 4 && BinaryPrimitives.ReadInt32LittleEndian(reply) == reply.Length - 4, "Native Messaging reply frame");
    using var json = JsonDocument.Parse(reply.AsMemory(4));
    Check(json.RootElement.GetProperty("ok").GetBoolean() == expectedOk, $"Unexpected Native Messaging acknowledgement: {json.RootElement}");
}

async Task CodexChecks()
{
    using var fixture = new Fixture();
    using var state = Open(Path.Combine(fixture.Root, "state_5.sqlite"));
    using var history = Open(Path.Combine(fixture.Root, "thread_history_1.sqlite"));
    Exec(state, """
        CREATE TABLE threads(id TEXT,name TEXT,title TEXT,cwd TEXT,created_at_ms INT,created_at INT,updated_at_ms INT,updated_at INT,archived INT,history_mode TEXT,rollout_path TEXT);
        INSERT INTO threads VALUES('t','A','fallback','/work',1000,1,2000,2,0,'paginated','missing');
        """);
    Exec(history, """
        CREATE TABLE thread_turns(thread_id TEXT,turn_id TEXT,started_at INT,completed_at INT,status TEXT);
        INSERT INTO thread_turns VALUES('t','turn',1,NULL,'inProgress');
        CREATE TABLE thread_items(thread_id TEXT,turn_id TEXT,item_id TEXT,item_type TEXT,created_at_ms INT,rollout_ordinal INT,item_json TEXT);
        INSERT INTO thread_items VALUES('t','turn','m','agentMessage',1000,1,'{"text":"first"}');
        """);
    var rollout = Path.Combine(fixture.Root, "rollout.jsonl");
    object Usage(int output) => new
    {
        type = "token_usage_record", timestamp = "2026-01-01T00:00:00Z",
        payload = new { thread_id = "t", turn_id = "turn", response_id = "response", usage = new { input_tokens = 10, output_tokens = output } },
    };
    File.WriteAllText(rollout, JsonSerializer.Serialize(Usage(1)) + "\n");
    Exec(state, "UPDATE threads SET rollout_path=$p0", rollout);
    Capture(fixture, CodexCollector.Capture);
    using var db = Open(fixture.Database("codex"));
    Capture(fixture, CodexCollector.Capture);
    Count(db, "thread_title_observations", 1);
    Count(db, "message_content_observations", 1);
    File.AppendAllText(rollout, JsonSerializer.Serialize(Usage(2)) + "\n");
    Exec(state, "UPDATE threads SET name='B'");
    Exec(history, "UPDATE thread_items SET item_json='final'; UPDATE thread_turns SET completed_at=3,status='completed'");
    Capture(fixture, CodexCollector.Capture);
    Exec(state, "UPDATE threads SET name='A'");
    Capture(fixture, CodexCollector.Capture);
    Rows(db, "SELECT title FROM thread_title_observations ORDER BY observation_id", ["A"], ["B"], ["A"]);
    Count(db, "response_token_usage_observations", 2);
    Rows(db, "SELECT total_output_tokens FROM response_token_usage", [2]);
    Count(db, "thread_cwd_observations", 1);
    Count(db, "thread_archive_observations", 1);
    Count(db, "message_content_observations", 2);
    Rows(db, "SELECT status,completed_at_utc_ms FROM current_turns", ["completed", 3000]);
    Count(db, "turn_status_observations", 2);
    Rows(db, "SELECT started_at_utc_ms FROM turns", [1000]);
    Rows(db, "SELECT trigger,stop_reason,error FROM capture_runs GROUP BY stop_reason", ["startup", "completed", null]);
    Rows(db, "SELECT item_json FROM current_messages", ["final"]);
    Rows(db, "SELECT COUNT(*) FROM capture_runs WHERE stopped_at_utc_ms IS NOT NULL", [4]);
    Console.WriteLine("PASS: Codex independent changes and repeated import");
}

async Task ClaudeChecks()
{
    using var fixture = new Fixture();
    var directory = Directory.CreateDirectory(Path.Combine(fixture.Root, "projects/test"));
    object Message(string text, int output) => new
    {
        type = "assistant", sessionId = "s", uuid = "m", timestamp = "2026-01-01T00:00:00Z", cwd = "/work",
        message = new { id = "r", model = "model", content = new[] { new { type = "text", text } }, usage = new { input_tokens = 10, output_tokens = output } },
    };
    object CustomTitle(string title) => new { type = "custom-title", sessionId = "s", customTitle = title };
    // The first message of a chain has a JSON null parent; this one also lacks a source time.
    object root = new { type = "user", sessionId = "s", uuid = "u", parentUuid = (string?)null, message = new { role = "user", content = "hi" } };
    object[] records = [root, CustomTitle("A"), Message("first", 1), CustomTitle("B"), Message("final", 2), CustomTitle("A")];
    File.WriteAllLines(Path.Combine(directory.FullName, "session.jsonl"), records.Select(record => JsonSerializer.Serialize(record)));
    Capture(fixture, ClaudeCollector.Capture);
    using var db = Open(fixture.Database("claude"));
    Capture(fixture, ClaudeCollector.Capture);
    Rows(db, "SELECT title FROM session_title_observations ORDER BY observation_id", ["A"], ["B"], ["A"]);
    Rows(db, "SELECT message_uuid,parent_uuid,created_at_utc_ms,response_id FROM messages ORDER BY message_uuid",
        ["m", null, 1767225600000L, "r"], ["u", "", null, null]);
    Count(db, "message_content_observations", 3);
    Count(db, "response_token_usage_observations", 2);
    Rows(db, "SELECT total_output_tokens FROM response_token_usage", [2]);
    Count(db, "source_records", 6);
    Rows(db, "SELECT stop_reason,error FROM capture_runs GROUP BY stop_reason", ["completed", null]);
    Rows(db, "SELECT title FROM current_sessions", ["A"]);
    Console.WriteLine("PASS: Claude source order and content revisions");
}

async Task TabChecks()
{
    using var fixture = new Fixture();
    var run = Guid.NewGuid();
    JsonObject Tab(int sequence, string kind, string fields) => new()
    {
        ["eventId"] = Guid.NewGuid().ToString(), ["captureSessionId"] = run.ToString(), ["runStartedAtUtcMs"] = 1000,
        ["sessionSequence"] = sequence, ["observedAtUtcMs"] = 1000 + sequence, ["eventKind"] = kind,
        ["tabs"] = JsonNode.Parse(fields),
    };
    var first = Tab(1, "snapshot", """
        [{"tabId":2,"fields":{"isPresent":true,"windowId":1,"url":"A","title":"Start"}},
         {"tabId":3,"fields":{"isPresent":true,"windowId":1,"url":null,"title":""}}]
        """);
    first["windows"] = JsonNode.Parse("""[{"windowId":1,"isPresent":true},{"windowId":5,"isPresent":true}]""");
    first["focus"] = JsonNode.Parse("""{"tabId":2}""");
    var forward = Tab(3, "tab_updated", """[{"tabId":4,"fields":{"url":"B","title":""}}]""");
    var replace = Tab(7, "tab_replaced", """
        [{"tabId":2,"fields":{"isPresent":false}},
         {"tabId":6,"fields":{"isPresent":true,"windowId":1,"url":"C","title":"Replacement"}}]
        """);
    replace["focus"] = JsonNode.Parse("""{"tabId":6}""");
    var close = Tab(9, "window_removed", """[{"tabId":4,"fields":{"isPresent":false}}]""");
    close["windows"] = JsonNode.Parse("""[{"windowId":5,"isPresent":false}]""");
    await Browser(fixture, new() { ["tabEvents"] = new JsonArray(first,
        Tab(2, "tab_created", """[{"tabId":4,"fields":{"isPresent":true,"windowId":1,"url":"A","title":null}}]"""),
        forward, forward.DeepClone(),
        Tab(4, "tab_updated", """[{"tabId":4,"fields":{"url":"A"}}]"""),
        Tab(5, "tab_detached", """[{"tabId":4,"fields":{"windowId":null}}]"""),
        Tab(6, "tab_attached", """[{"tabId":4,"fields":{"windowId":5}}]"""), replace,
        Tab(8, "tab_removed", """[{"tabId":3,"fields":{"isPresent":false}}]"""), close) });
    var replay = forward.DeepClone();
    replay["focus"] = JsonNode.Parse("""{"tabId":4}""");
    await Browser(fixture, new() { ["tabEvents"] = new JsonArray(replay, close.DeepClone()) });

    using var db = Open(fixture.Database("browser"));
    Count(db, "active_tab_focus_events", 2);
    Count(db, "tab_url_observations", 6);
    Count(db, "tab_title_observations", 5);
    Count(db, "tab_observations", 9);
    Count(db, "tab_presence_events", 7);
    Count(db, "tab_window_observations", 6);
    Count(db, "window_presence_events", 3);

    Rows(db, """
        SELECT run_sequence,window_id,has_window,url,title,is_present FROM tab_history WHERE tab_id=4 ORDER BY run_sequence
        """, [2, 1, 1, "A", null, 1], [3, 1, 1, "B", "", 1], [4, 1, 1, "A", "", 1],
        [5, null, 0, "A", "", 1], [6, 5, 1, "A", "", 1], [9, 5, 1, "A", "", 0]);
    Rows(db, """
        SELECT tab_id,window_id,url,title,is_focused FROM tab_history
        WHERE is_present=1 AND observed_at_utc_ms<=1006 AND (valid_until_utc_ms>1006 OR valid_until_utc_ms IS NULL)
        ORDER BY tab_id
        """, [2, 1, "A", "Start", 1], [3, 1, null, "", 0], [4, 5, "A", "", 0]);
    Rows(db, "SELECT tab_id,window_id,url,title,is_focused FROM current_tabs", [6, 1, "C", "Replacement", 1]);

    var invalid = Tab(11, "tab_updated", """[{"tabId":6,"fields":{"isPresent":4}}]""");
    await Browser(fixture, new() { ["tabEvents"] = new JsonArray(
        Tab(10, "tab_updated", """[{"tabId":6,"fields":{"title":"Uncommitted"}}]"""), invalid) }, expectedOk: false);
    Count(db, "tab_observations", 9);
    JsonObject Coverage(int started, string evidence) => new()
    {
        ["eventId"] = Guid.NewGuid().ToString(), ["scope"] = "tabs", ["mode"] = "notification",
        ["startedAtUtcMs"] = started, ["evidence"] = evidence, ["runId"] = run.ToString(),
    };
    var gap = Coverage(1010, "unavailable");
    gap["detail"] = "baseline_reset";
    await Browser(fixture, new() { ["coverage"] = new JsonArray(Coverage(1001, "available"), gap) });
    run = Guid.NewGuid();
    var restart = Tab(1, "snapshot", """[{"tabId":6,"fields":{"isPresent":true,"windowId":5,"url":"C","title":"Replacement"}}]""");
    restart["runStartedAtUtcMs"] = 1020;
    restart["observedAtUtcMs"] = 1020;
    restart["windows"] = JsonNode.Parse("""[{"windowId":5,"isPresent":true}]""");
    restart["focus"] = JsonNode.Parse("""{"tabId":null}""");
    await Browser(fixture, new() { ["tabEvents"] = new JsonArray(restart) });
    await Browser(fixture, new() { ["coverage"] = new JsonArray(Coverage(1020, "available")) });
    Rows(db, "SELECT tab_id,window_id,is_focused FROM current_tabs", [6, 5, 0]);
    Rows(db, "SELECT started_at_utc_ms,stopped_at_utc_ms,evidence FROM capture_coverage ORDER BY started_at_utc_ms",
        [1001, 1010, "available"], [1010, 1020, "unavailable"], [1020, null, "available"]);
    Rows(db, """
        SELECT tab_id FROM tab_history
        WHERE is_present=1 AND observed_at_utc_ms<=1015 AND (valid_until_utc_ms>1015 OR valid_until_utc_ms IS NULL)
        """);
    Rows(db, "SELECT COUNT(DISTINCT tab_key) FROM tabs WHERE tab_id=6", [2]);
    Console.WriteLine("PASS: browser inventory, as-of history, focus, retry, atomicity and recovery");
}

async Task DownloadChecks()
{
    using var fixture = new Fixture();
    var run = Guid.NewGuid().ToString();
    JsonObject Download(int sequence, JsonObject fields) => new()
    {
        ["eventId"] = Guid.NewGuid().ToString(), ["runId"] = run, ["runStartedAtUtcMs"] = 1000,
        ["runSequence"] = sequence, ["observedAtUtcMs"] = 1000 + sequence, ["kind"] = "changed", ["downloadId"] = 1, ["fields"] = fields,
    };
    var initial = Download(1, new() { ["url"] = "https://example.com/file", ["startedAtUtcMs"] = 1000, ["state"] = "in_progress", ["filename"] = "/tmp/file" });
    var moved = Download(2, new() { ["filename"] = "/tmp/new" });
    var done = Download(3, new() { ["state"] = "complete", ["endedAtUtcMs"] = 1003 });
    await Browser(fixture, new() { ["downloads"] = new JsonArray(initial, moved, done) });
    await Browser(fixture, new() { ["downloads"] = new JsonArray(done.DeepClone()) });
    using var db = Open(fixture.Database("browser"));
    Rows(db, "SELECT url,started_at_utc_ms FROM downloads", ["https://example.com/file", 1000]);
    Count(db, "download_response_observations", 0);
    Count(db, "download_state_observations", 2);
    Count(db, "download_path_observations", 2);
    Rows(db, "SELECT state,local_path,url,ended_at_utc_ms FROM current_downloads", ["complete", "/tmp/new", "https://example.com/file", 1003]);
    Rows(db, "SELECT state,local_path FROM download_history ORDER BY observation_id", ["in_progress", "/tmp/file"], ["in_progress", "/tmp/new"], ["complete", "/tmp/new"]);
    Console.WriteLine("PASS: download state and path change independently");
}

async Task MacOSChecks()
{
    using var fixture = new Fixture();
    var native = Path.Combine(root, "src/Collectors/Orisumi.Collectors.MacOS/Native");
    var main = Path.Combine(fixture.Root, "main.swift");
    File.Copy(Path.Combine(root, "tests/macos_facts.swift"), main);
    var binary = Path.Combine(fixture.Root, "facts");
    await Run("/usr/bin/xcrun", ["swiftc", main, .. Directory.GetFiles(native, "*.swift").Where(path => Path.GetFileName(path) != "main.swift"),
        "-lsqlite3", "-o", binary]);
    // A copy of the schema exactly as the build packages it beside the executable; a check below breaks it.
    var schema = Path.Combine(root, "src/Collectors/Orisumi.Collectors.MacOS/bin/Debug/net11.0/Orisumi.Collectors.MacOS/Schema");
    var packagedSchema = Path.Combine(fixture.Root, "Orisumi.Collectors.MacOS/Schema");
    foreach (var file in Directory.EnumerateFiles(schema, "*", SearchOption.AllDirectories))
    {
        var target = Path.Combine(packagedSchema, Path.GetRelativePath(schema, file));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(file, target);
    }
    var path = Path.Combine(fixture.Root, "macos.sqlite");
    await Run(binary, [path]);
    using var db = Open(path);
    // The fixture PID is absent from the OS: its window observations have an
    // unverified identity, separate from both supplied lifecycle identities.
    Count(db, "process_instances", 4);
    Count(db, "window_focus_instances", 3);
    Count(db, "focused_window_events", 5);
    Count(db, "window_title_observations", 4);
    // No window, an unreadable focus and an untitled window stay distinguishable.
    Rows(db, "SELECT has_window,window_title FROM window_history ORDER BY run_sequence",
        [1, "A"], [1, "B"], [1, "C"], [0, null], [null, null], [1, ""]);
    Rows(db, "SELECT has_app,process_instance_id IS NOT NULL FROM foreground_app_events ORDER BY run_sequence", [1, 1], [0, 0]);
    Rows(db, "SELECT available FROM capture_channel_events ORDER BY run_sequence", [1], [0], [1]);
    Rows(db, "SELECT capture_windows,capture_input,stopped_at_utc_ms IS NOT NULL FROM capture_runs", [1, 1, 1]);
    Rows(db, "PRAGMA foreign_key_check");
    // The native fixture also opens twice with capture features disabled.
    using var disabled = Open(path + ".disabled");
    Rows(disabled, "SELECT COUNT(*) FROM window_history", [0]);
    Rows(disabled, "SELECT COUNT(*) FROM keyboard_presses", [0]);
    Rows(disabled, "SELECT capture_windows,capture_input FROM capture_runs", [0, 0], [0, 0]);
    // A failed initialization leaves the database empty.
    var broken = Path.Combine(packagedSchema, "Indexes/invalid.sql");
    File.WriteAllText(broken, "CREATE INDEX invalid ON nonexistent(column_name);");
    var failedPath = Path.Combine(fixture.Root, "failed.sqlite");
    await Run(binary, [failedPath, "reject"]);
    using var failed = Open(failedPath);
    Rows(failed, "SELECT COUNT(*) FROM sqlite_schema", [0]);
    Console.WriteLine("PASS: macOS focus, titles, PID reuse and channel gaps");
}

async Task BookmarkChecks()
{
    using var fixture = new Fixture();
    var run = Guid.NewGuid().ToString();
    var initial = new JsonObject
    {
        ["observationId"] = Guid.NewGuid().ToString(), ["runId"] = run,
        ["runStartedAtUtcMs"] = 1000, ["runSequence"] = 1, ["observedAtUtcMs"] = 1001,
        ["kind"] = "snapshot",
        ["nodes"] = new JsonArray(new JsonObject { ["bookmarkId"] = "b", ["title"] = "A", ["url"] = "https://example.com" }),
    };
    await Browser(fixture, new() { ["bookmarkObservation"] = initial });
    await Browser(fixture, new() { ["bookmarkObservation"] = initial.DeepClone() });
    await Browser(fixture, new() { ["getDatabaseId"] = true });
    await Browser(fixture, new() { ["bookmarkObservation"] = new JsonObject
    {
        ["observationId"] = Guid.NewGuid().ToString(), ["runId"] = run,
        ["runStartedAtUtcMs"] = 1000, ["runSequence"] = 2, ["observedAtUtcMs"] = 1002,
        ["kind"] = "changed", ["bookmarkId"] = "b", ["title"] = "B",
    } });
    using var db = Open(fixture.Database("browser"));
    Rows(db, "SELECT title FROM current_bookmarks", ["B"]);
    Rows(db, "SELECT title FROM bookmark_history ORDER BY observation_id", ["A"], ["B"]);
    Count(db, "bookmark_observations", 2);
    Count(db, "database_identity", 1);
    Count(db, "host_observations", 1);
    Rows(db, "SELECT COUNT(*) FROM capture_runs WHERE host_observation_id IS NULL", [0L]);
    Count(db, "visits", 0);
    Rows(db, "PRAGMA foreign_key_check");
    Console.WriteLine("PASS: bookmarks and other browser messages share the complete schema");
}

static async Task<byte[]> Run(string executable, string[] arguments, byte[]? input = null, Fixture? fixture = null)
{
    var start = new ProcessStartInfo(executable)
    {
        UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
    };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    if (fixture is not null)
    {
        // Point the source readers at fixtures in the child only; never import personal data.
        start.Environment["CODEX_HOME"] = fixture.Root;
        start.Environment["CLAUDE_CONFIG_DIR"] = fixture.Root;
    }
    using var process = Process.Start(start) ?? throw new Exception($"Cannot start {executable}");
    using var stdout = new MemoryStream();
    var copy = process.StandardOutput.BaseStream.CopyToAsync(stdout);
    var error = process.StandardError.ReadToEndAsync();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    try
    {
        if (input is not null) await process.StandardInput.BaseStream.WriteAsync(input, timeout.Token);
        process.StandardInput.Close();
        await process.WaitForExitAsync(timeout.Token);
        await copy;
        Check(process.ExitCode == 0, await error);
        return stdout.ToArray();
    }
    finally
    {
        if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
    }
}

sealed class Fixture : IDisposable
{
    private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("orisumi-facts-");
    internal string Root => directory.FullName;
    internal string Store => Path.Combine(Root, "store");
    internal string Database(string source) => Path.Combine(Store, "raw", source + ".sqlite");
    public void Dispose() => directory.Delete(recursive: true);
}
