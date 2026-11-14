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
async Task Capture(Fixture fixture, string source, params string[] arguments) =>
    await Run(cli, ["--store", fixture.Store, source, "capture", .. arguments], fixture: fixture);
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
    Check(json.RootElement.GetProperty("ok").GetBoolean() == expectedOk, "Native Messaging acknowledgement");
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
    await Capture(fixture, "codex", "--all");
    using var db = Open(fixture.Database("codex"));
    await Capture(fixture, "codex", "--all");
    Count(db, "thread_title_observations", 1);
    Count(db, "message_content_observations", 1);
    File.AppendAllText(rollout, JsonSerializer.Serialize(Usage(2)) + "\n");
    Exec(state, "UPDATE threads SET name='B'");
    Exec(history, "UPDATE thread_items SET item_json='final'; UPDATE thread_turns SET completed_at=3,status='completed'");
    await Capture(fixture, "codex", "--all");
    Exec(state, "UPDATE threads SET name='A'");
    await Capture(fixture, "codex", "--all");
    Rows(db, "SELECT title FROM thread_title_observations ORDER BY observation_id", ["A"], ["B"], ["A"]);
    Count(db, "response_token_usage_observations", 2);
    Rows(db, "SELECT total_output_tokens FROM response_token_usage", [2]);
    Count(db, "thread_cwd_observations", 1);
    Count(db, "thread_archive_observations", 1);
    Count(db, "message_content_observations", 2);
    Rows(db, "SELECT status,completed_at_utc_ms FROM current_turns", ["completed", 3000]);
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
    object[] records = [CustomTitle("A"), Message("first", 1), CustomTitle("B"), Message("final", 2), CustomTitle("A")];
    File.WriteAllLines(Path.Combine(directory.FullName, "session.jsonl"), records.Select(record => JsonSerializer.Serialize(record)));
    await Capture(fixture, "claude");
    using var db = Open(fixture.Database("claude"));
    await Capture(fixture, "claude");
    Rows(db, "SELECT title FROM session_title_observations ORDER BY observation_id", ["A"], ["B"], ["A"]);
    Count(db, "messages", 1);
    Count(db, "message_content_observations", 2);
    Count(db, "response_token_usage_observations", 2);
    Rows(db, "SELECT total_output_tokens FROM response_token_usage", [2]);
    Count(db, "source_records", 5);
    Rows(db, "SELECT title FROM current_sessions", ["A"]);
    Console.WriteLine("PASS: Claude source order and content revisions");
}

async Task TabChecks()
{
    using var fixture = new Fixture();
    var run = Guid.NewGuid().ToString();
    JsonObject Tab(int sequence, string? title = "A", string? url = "https://example.com", string kind = "snapshot", bool blur = false) => new()
    {
        ["eventId"] = Guid.NewGuid().ToString(), ["captureSessionId"] = run, ["runStartedAtUtcMs"] = 1000,
        ["sessionSequence"] = sequence, ["observedAtUtcMs"] = 1000 + sequence, ["eventKind"] = kind,
        ["windowId"] = blur ? null : JsonValue.Create(1), ["tabId"] = blur ? null : JsonValue.Create(2),
        ["url"] = url, ["title"] = title,
    };
    var second = Tab(2, title: "B", kind: "title_changed");
    await Browser(fixture, new() { ["activeTabEvents"] = new JsonArray(Tab(1), second,
        Tab(3, "C", "https://example.com/other", "url_changed"), Tab(4, null, null, "window_blurred", true)) });
    await Browser(fixture, new() { ["activeTabEvents"] = new JsonArray(second.DeepClone()) });
    using var db = Open(fixture.Database("browser"));
    Count(db, "active_tab_focus_events", 2);
    Count(db, "tab_url_observations", 2);
    Count(db, "tab_title_observations", 3);
    Count(db, "tab_observations", 4);
    Rows(db, "SELECT title FROM active_tab_state_history ORDER BY run_sequence", ["A"], ["B"], ["C"], [null]);
    Console.WriteLine("PASS: browser title, navigation, focus and retry");
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
    Count(db, "download_resource_observations", 1);
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
    await Run("/usr/bin/xcrun", ["swiftc", main, .. new[] { "DatabaseSchema.swift", "ForegroundDatabase.swift", "InputEventObserver.swift", "KeyboardPressAccumulator.swift" }
        .Select(name => Path.Combine(native, name)), "-lsqlite3", "-o", binary]);
    var schema = Path.Combine(root, "src/Collectors/Orisumi.Collectors.MacOS/Schema");
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
    Count(db, "process_instances", 2);
    Count(db, "window_focus_instances", 2);
    Count(db, "focused_window_events", 3);
    Count(db, "window_title_observations", 3);
    Rows(db, "SELECT window_title FROM window_history ORDER BY run_sequence", ["A"], ["B"], ["C"], [null]);
    Rows(db, "SELECT available FROM capture_channel_events ORDER BY run_sequence", [1], [0], [1]);
    Rows(db, "SELECT capture_windows,capture_input,stopped_at_utc_ms IS NOT NULL FROM capture_runs", [1, 1, 1]);
    Rows(db, "PRAGMA foreign_key_check");
    Rows(db, "PRAGMA user_version", [1]);
    // The native fixture also opens twice with capture features disabled.
    using var disabled = Open(path + ".disabled");
    Rows(disabled, "SELECT COUNT(*) FROM window_history", [0]);
    Rows(disabled, "SELECT COUNT(*) FROM keyboard_presses", [0]);
    Rows(disabled, "SELECT capture_windows,capture_input FROM capture_runs", [0, 0], [0, 0]);
    // A rejected version must preserve both metadata and previously collected facts.
    Exec(db, "PRAGMA user_version=99");
    await Run(binary, [path, "reject"]);
    Rows(db, "PRAGMA user_version", [99]);
    Count(db, "process_instances", 2);
    var broken = Path.Combine(packagedSchema, "Indexes/invalid.sql");
    File.WriteAllText(broken, "CREATE INDEX invalid ON nonexistent(column_name);");
    var failedPath = Path.Combine(fixture.Root, "failed.sqlite");
    await Run(binary, [failedPath, "reject"]);
    using var failed = Open(failedPath);
    Rows(failed, "PRAGMA user_version", [0]);
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
    Count(db, "_collector_state", 1);
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
