#:project ../Host/Cli/Orisumi.Cli.csproj
#:property AssemblyName=CaptureStatusChecks
#:property PublishAot=false
#:property JsonSerializerIsReflectionEnabledByDefault=false

using System.CommandLine;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Orisumi.Cli;
using Orisumi.Desktop;
using Orisumi.Platform;

[assembly: SupportedOSPlatform("macos")]

if (!OperatingSystem.IsMacOS())
{
    Console.WriteLine("Skipped: capture status requires macOS.");
    return;
}

const string RunTables = """
    CREATE TABLE capture_runs (run_id INTEGER PRIMARY KEY, started_at_utc_ms INTEGER, collector_version TEXT,
        confirmed_until_utc_ms INTEGER, stopped_at_utc_ms INTEGER, stop_reason TEXT, error TEXT);
    CREATE TABLE capture_read_failures (event_id INTEGER PRIMARY KEY, run_id INTEGER NOT NULL REFERENCES capture_runs(run_id),
        observed_at_utc_ms INTEGER NOT NULL, error TEXT NOT NULL, scope TEXT);
    """;
var temp = Directory.CreateTempSubdirectory("orisumi-capture-status-");
try
{
    var instance = Instance.Create(Path.Combine(temp.FullName, "store '中文"), "test_status");
    var store = instance.Store;
    var baseDirectory = Directory.CreateDirectory(Path.Combine(temp.FullName, "checkout", "bin")).FullName;
    var locations = new SystemLocations(Path.Combine(temp.FullName, "agents"), Path.Combine(temp.FullName, "commands"),
        Path.Combine(temp.FullName, "launchctl"));
    WriteExecutable(locations.Launchctl, """
        #!/bin/sh
        set -eu
        [ "$1" = print ] && [ "$#" = 2 ] || exit 2
        case "$2" in
          gui/"$(/usr/bin/id -u)"/dev.orisumi.test_status.capture.*|gui/"$(/usr/bin/id -u)"/dev.orisumi.test_status_overall.capture.*) ;;
          *) exit 2 ;;
        esac
        case "$2" in
          *.collecting) printf '\tstate = running\n\tpid = 4321\n\tlast exit code = 4\n' ;;
          *.idle) printf '\tstate = waiting\n\tlast exit code = 17\n' ;;
          *.queued) printf '\tstate = spawn scheduled\n\tlast exit code = (never exited)\n' ;;
          *.signaled) printf '\tstate = waiting\n\tlast exit code = terminated by signal\n' ;;
          *.broken) printf 'print failed\n' >&2; exit 5 ;;
          *) exit 113 ;;
        esac
        """);
    Directory.CreateDirectory(locations.Agents);
    var other = Instance.Create(Path.Combine(temp.FullName, "other"), "other");
    foreach (var label in new[] { instance.CaptureAgentLabel("idle"), instance.CaptureAgentLabel("queued"),
        instance.CaptureAgentLabel("signaled"), other.CaptureAgentLabel("ignored"), instance.AppAgentLabel })
        File.WriteAllText(Path.Combine(locations.Agents, label + ".plist"), "");
    foreach (var name in new[] { "collecting", "unloaded" })
        Install(store, name);
    Directory.CreateDirectory(store.Observations("source_only"));

    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    var started = now - 600_000;
    var confirmed = now - 12_000;
    using (var older = OpenDatabase("archived", "older"))
        Execute(older, $"""
            PRAGMA journal_mode=WAL;
            {RunTables}
            INSERT INTO capture_runs VALUES (8, {started}, NULL, NULL, NULL, NULL, NULL),
                (7, {started - 3}, NULL, NULL, NULL, NULL, NULL), (6, {started - 5}, NULL, NULL, NULL, NULL, NULL),
                (5, {started - 7}, NULL, NULL, NULL, NULL, NULL), (4, {started - 9}, NULL, NULL, NULL, NULL, NULL),
                (3, {started - 11}, NULL, NULL, NULL, NULL, NULL);
            INSERT INTO capture_read_failures VALUES
                (6, 8, {now - 1000}, 'older recent', 'fixture scope'), (5, 8, {now - 3_660_001}, 'older 1', NULL),
                (4, 8, {now - 3_660_003}, 'older 2', NULL), (3, 8, {now - 3_660_005}, 'older 3', NULL),
                (2, 8, {now - 3_660_007}, 'older 4', NULL), (1, 8, {now - 3_660_009}, 'older oldest', NULL);
            """);
    using (var latest = OpenDatabase("archived", "latest"))
        Execute(latest, $"""
            {RunTables}
            INSERT INTO capture_runs VALUES (1, {started}, NULL, NULL, NULL, NULL, NULL),
                (2, {started}, '0.1.0+recorded', {confirmed}, {now - 1000}, 'failed', 'fixture error'),
                (3, {started - 2}, NULL, NULL, NULL, NULL, NULL), (4, {started - 4}, NULL, NULL, NULL, NULL, NULL),
                (5, {started - 6}, NULL, NULL, NULL, NULL, NULL), (6, {started - 8}, NULL, NULL, NULL, NULL, NULL);
            INSERT INTO capture_read_failures VALUES
                (6, 2, {now - 1000}, 'latest recent', NULL), (5, 2, {now - 3_660_000}, 'latest 1', NULL),
                (4, 2, {now - 3_660_000}, 'latest 2', NULL), (3, 2, {now - 3_660_004}, 'latest 3', NULL),
                (2, 2, {now - 3_660_006}, 'latest 4', NULL), (1, 2, {now - 3_660_008}, 'latest 5', NULL);
            """);
    using (var unrelated = OpenDatabase("archived", "unrelated"))
        Execute(unrelated, $"CREATE TABLE capture_read_failures (observed_at_utc_ms INTEGER); INSERT INTO capture_read_failures VALUES ({now});");
    using (var stale = OpenDatabase("signaled", "stale"))
        Execute(stale, $"""
            {RunTables}
            INSERT INTO capture_runs VALUES (1, {started}, NULL, NULL, NULL, NULL, NULL);
            INSERT INTO capture_read_failures VALUES (1, 1, {now - 1000}, 'stale read', NULL);
            """);
    using (var failed = OpenDatabase("unloaded", "failed"))
        Execute(failed, $"""
            {RunTables}
            INSERT INTO capture_runs VALUES (1, {started}, NULL, NULL, {now - 1000}, 'failed', 'failed before unloading');
            """);
    // A copy from another Mac with a newer failed run must not stand in for the collector here.
    using (var copied = OpenDatabase("collecting", "copied"))
        Execute(copied, $"""
            {RunTables}
            {Identity("other-hardware")}
            INSERT INTO capture_runs VALUES (1, {now - 2000}, NULL, NULL, {now - 1000}, 'failed', 'copied failure');
            INSERT INTO capture_read_failures VALUES (1, 1, {now - 1000}, 'copied read', NULL);
            """);
    using var live = OpenDatabase("collecting", "live");
    Execute(live, $"""
        PRAGMA journal_mode=WAL;
        PRAGMA wal_autocheckpoint=0;
        {RunTables}
        {Identity(HostIdentity.Read().HardwareUuid)}
        INSERT INTO capture_runs VALUES (1, {started}, '0.2.0+fixture', {confirmed}, NULL, NULL, NULL);
        """);

    using var output = new StringWriter();
    var command = Commands.Create(new DesktopServices(
        current => current.Name == "test_status_overall" ? new StatusJobs() : new MacCollectorJobs(current, locations),
        current => new MacInstallation(current, locations, HostBundle.Tools.Current)), output, baseDirectory);
    var queries = Commands.Create(null, output, baseDirectory);
    Check(queries.Subcommands.Select(c => c.Name).Order().SequenceEqual(["capture", "day", "describe", "export"]) &&
        queries.Subcommands.Single(c => c.Name == "capture").Subcommands.Select(c => c.Name).SequenceEqual(["coverage"]),
        "a desktop without supervision exposes the available query commands");
    var before = Entries(store.Root);
    var agentsBefore = Entries(locations.Agents);
    Check(await Invoke("describe") == 0, "describe succeeds");
    using var description = JsonDocument.Parse(output.ToString());
    var hostVersion = description.RootElement.GetProperty("version").GetString();

    Check(await Invoke("capture", "status", "--store", store.Root) == 0, "text status succeeds for mixed collector states");
    var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
    Check(lines.Length == 8 && lines[0] == $"test_status ({store.Root}) host {hostVersion}, health attention", "text header and one line per collector");
    Check(lines[1].StartsWith("  archived -: not_loaded, started ") && Regex.IsMatch(lines[1], @"confirmed \d+[smhd] ago") &&
        lines[1].EndsWith("stopped (failed), failures (1h) 2 [not_installed, run_failed]"),
        "text reports stopped run, freshness, failures and problem codes");
    Check(lines[2].Contains("collecting 0.2.0+fixture: running (pid 4321), started") &&
        lines[3].Contains("idle -: waiting (last exit 17), no run") &&
        lines[4].Contains("queued -: spawn scheduled, no run") &&
        lines[5].Contains("signaled -: waiting (last exit terminated by signal), started") &&
        lines[2].EndsWith("failures (1h) 0"), "text reports agent details and omits empty problems");
    Check(lines[6].Contains("source_only -: not_loaded, no run") &&
        lines[7].Contains("unloaded 0.2.0+fixture: not_loaded, started"), "text reports observation-only and unloaded collectors");

    var beforeJson = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    Check(await Invoke("--store", store.Root, "capture", "status", "--json") == 0, "JSON status succeeds with a recursive store option");
    using var report = JsonDocument.Parse(output.ToString());
    var status = report.RootElement;
    Check(status.GetProperty("instance").GetString() == instance.Name && status.GetProperty("store").GetString() == store.Root &&
        status.GetProperty("host_version").GetString() == hostVersion, "JSON instance identity matches describe");
    var generated = status.GetProperty("generated_at_utc_ms").GetInt64();
    Check(generated >= beforeJson && generated <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "JSON reports its generation time in UTC milliseconds");
    Check(status.GetProperty("health").GetString() == "attention", "mixed collector health is attention");
    CheckProperties(status, "instance", "store", "host_version", "collectors", "generated_at_utc_ms", "health", "version", "source");
    Check(status.GetProperty("version").ValueKind == JsonValueKind.Null && status.GetProperty("source").ValueKind == JsonValueKind.Null,
        "status without an installed manifest has no release version or deployment source");
    var collectors = status.GetProperty("collectors").EnumerateArray().ToArray();
    Check(collectors.Select(collector => collector.GetProperty("name").GetString())
        .SequenceEqual(["archived", "collecting", "idle", "queued", "signaled", "source_only", "unloaded"]), "collector union is ordered and scoped to the instance");
    var run = collectors[0].GetProperty("run");
    Check(run.GetProperty("run_id").GetInt64() == 2 && run.GetProperty("started_at_utc_ms").GetInt64() == started &&
        run.GetProperty("collector_version").GetString() == "0.1.0+recorded" &&
        run.GetProperty("confirmed_until_utc_ms").GetInt64() == confirmed &&
        run.GetProperty("stopped_at_utc_ms").GetInt64() == now - 1000 && run.GetProperty("stop_reason").GetString() == "failed" &&
        run.GetProperty("error").GetString() == "fixture error" &&
        collectors[0].GetProperty("read_failures_last_hour").GetInt64() == 2, "JSON reports newest run and aggregates recent failures");
    Check(Agent(1).GetProperty("state").GetString() == "running" && Agent(1).GetProperty("pid").GetInt32() == 4321 &&
        Agent(1).GetProperty("last_exit_code").ValueKind == JsonValueKind.Null &&
        collectors[1].GetProperty("run").GetProperty("confirmed_until_utc_ms").GetInt64() == confirmed, "running agent and committed WAL data are visible");
    Check(Agent(2).GetProperty("state").GetString() == "waiting" && Agent(2).GetProperty("last_exit_code").GetInt32() == 17 &&
        Agent(3).GetProperty("state").GetString() == "spawn scheduled" && Agent(3).GetProperty("last_exit_code").ValueKind == JsonValueKind.Null &&
        Agent(4).GetProperty("last_exit_code").GetString() == "terminated by signal", "raw states and numeric, null and text exit codes");
    Check(Agent(5).GetProperty("state").GetString() == "not_loaded" && collectors[5].GetProperty("version").ValueKind == JsonValueKind.Null &&
        collectors[5].GetProperty("run").ValueKind == JsonValueKind.Null && Agent(6).GetProperty("state").GetString() == "not_loaded" &&
        collectors[6].GetProperty("version").GetString() == "0.2.0+fixture", "unloaded agents and absent versions and runs");

    CheckProperties(collectors[0], "name", "version", "agent", "run", "read_failures_last_hour", "health", "problems",
        "recent_runs", "recent_read_failures");
    CheckProperties(Agent(0), "state", "pid", "last_exit_code");
    string[] runProperties = ["run_id", "started_at_utc_ms", "collector_version", "confirmed_until_utc_ms",
        "stopped_at_utc_ms", "stop_reason", "error"];
    CheckProperties(run, runProperties);
    var recentRuns = collectors[0].GetProperty("recent_runs").EnumerateArray().ToArray();
    Check(recentRuns.Select(item => (item.GetProperty("database").GetString(), item.GetProperty("run_id").GetInt64()))
        .SequenceEqual([("latest", 2L), ("latest", 1L), ("older", 8L), ("latest", 3L), ("older", 7L)]),
        "five newest runs merge both databases with database order then descending local IDs on ties");
    CheckProperties(recentRuns[0], [.. runProperties, "database"]);
    Check(runProperties.All(field => JsonElement.DeepEquals(run.GetProperty(field), recentRuns[0].GetProperty(field))),
        "latest run fields match the first history entry");

    var recentFailures = collectors[0].GetProperty("recent_read_failures").EnumerateArray().ToArray();
    Check(recentFailures.Select(item => (item.GetProperty("database").GetString(), item.GetProperty("error").GetString()))
        .SequenceEqual([("latest", "latest recent"), ("older", "older recent"), ("latest", "latest 1"), ("latest", "latest 2"),
            ("older", "older 1"), ("older", "older 2"), ("latest", "latest 3"), ("older", "older 3"), ("latest", "latest 4"), ("older", "older 4")]),
        "ten newest read failures merge both databases and include earlier than one hour");
    CheckProperties(recentFailures[0], "observed_at_utc_ms", "run_id", "scope", "error", "database");
    Check(recentFailures[0].GetProperty("observed_at_utc_ms").GetInt64() == now - 1000 &&
        recentFailures[0].GetProperty("run_id").GetInt64() == 2 && recentFailures[0].GetProperty("scope").ValueKind == JsonValueKind.Null &&
        recentFailures[1].GetProperty("run_id").GetInt64() == 8 && recentFailures[1].GetProperty("scope").GetString() == "fixture scope",
        "read failures include their timestamps, run IDs and nullable scopes");
    Check(collectors[5].GetProperty("recent_runs").GetArrayLength() == 0 &&
        collectors[5].GetProperty("recent_read_failures").GetArrayLength() == 0, "empty histories serialize as arrays");
    Check(collectors[1].GetProperty("recent_runs").EnumerateArray().All(item => item.GetProperty("database").GetString() == "live") &&
        collectors[1].GetProperty("recent_read_failures").GetArrayLength() == 0, "copies from other hardware are not this host's history");
    CheckHealth(collectors[0], "attention", "not_installed", "run_failed");
    CheckHealth(collectors[1], "ok");
    CheckHealth(collectors[2], "attention", "not_installed", "not_running");
    CheckHealth(collectors[3], "attention", "not_installed", "not_running");
    CheckHealth(collectors[4], "attention", "not_installed", "not_running", "stale");
    CheckHealth(collectors[5], "attention", "not_installed");
    CheckHealth(collectors[6], "stopped", "not_loaded", "run_failed");
    Console.WriteLine("PASS: JSON health, ordered problem codes, recent runs and recent read failures across databases");

    Check(before.SequenceEqual(Entries(store.Root)) && agentsBefore.SequenceEqual(Entries(locations.Agents)),
        "status creates no files or directories in the store or LaunchAgents");
    Console.WriteLine("PASS: text and JSON status merge installed collectors, LaunchAgents and observations, including stopped and live WAL runs");

    var overallStore = Instance.Create(Path.Combine(temp.FullName, "overall"), "test_status_overall").Store;
    await CheckOverall("ok", 0);
    Install(overallStore, "unloaded");
    Install(overallStore, "another_unloaded");
    await CheckOverall("stopped", 2);
    Install(overallStore, "collecting");
    using var healthyRun = OpenDatabase("collecting", "healthy", overallStore);
    Execute(healthyRun, $"""
        {RunTables}
        INSERT INTO capture_runs VALUES (1, {started}, NULL, NULL, {started + 10_000}, 'completed', NULL);
        """);
    await CheckOverall("attention", 3);
    Directory.Delete(overallStore.Collector("unloaded"), recursive: true);
    Directory.Delete(overallStore.Collector("another_unloaded"), recursive: true);
    await CheckOverall("ok", 1);
    Execute(healthyRun, $"INSERT INTO capture_runs VALUES (2, {now - 60_000}, NULL, {started}, NULL, NULL, NULL)");
    await CheckOverall("ok", 1);
    Console.WriteLine("PASS: empty, stopped, mixed and healthy stores, including completed runs and fresh starts");

    File.WriteAllText(Path.Combine(locations.Agents, instance.CaptureAgentLabel("broken") + ".plist"), "");
    await Expect<IOException>("print failed", ["capture", "status", "--store", store.Root]);
    var checkout = Path.GetDirectoryName(baseDirectory)!;
    File.WriteAllText(Path.Combine(checkout, "Orisumi.slnx"), "<Solution />");
    await Expect<DirectoryNotFoundException>("instance.json", ["capture", "status"]);
    Check(!Directory.Exists(Path.Combine(checkout, "app")), "implicit status does not initialize a checkout store");
    Console.WriteLine("PASS: unexpected launchctl errors propagate and status stays read-only");

    JsonElement Agent(int index) => collectors[index].GetProperty("agent");

    async Task CheckOverall(string health, int count)
    {
        Check(await Invoke("capture", "status", "--json", "--store", overallStore.Root) == 0, "overall status succeeds");
        using var result = JsonDocument.Parse(output.ToString());
        Check(result.RootElement.GetProperty("health").GetString() == health &&
            result.RootElement.GetProperty("collectors").GetArrayLength() == count, $"overall health is {health} with {count} collectors");
    }

    SqliteConnection OpenDatabase(string source, string name, Store? destination = null)
    {
        var directory = (destination ?? store).Observations(source);
        Directory.CreateDirectory(directory);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory, name + ".sqlite"), Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    Task<int> Invoke(params string[] arguments)
    {
        output.GetStringBuilder().Clear();
        return command.Parse(arguments).InvokeAsync(new InvocationConfiguration { EnableDefaultExceptionHandler = false });
    }

    async Task Expect<T>(string message, string[] arguments) where T : Exception
    {
        try { await Invoke(arguments); }
        catch (T error) when (error.Message.Contains(message)) { return; }
        throw new Exception($"Expected {typeof(T).Name} containing {message}");
    }
}
finally { temp.Delete(recursive: true); }

static void CheckProperties(JsonElement value, params string[] names) => Check(
    value.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).SequenceEqual(names.Order(StringComparer.Ordinal)),
    "JSON has expected properties " + string.Join(", ", names));

static void CheckHealth(JsonElement collector, string health, params string[] problems)
{
    var name = collector.GetProperty("name").GetString();
    Check(collector.GetProperty("health").GetString() == health, $"{name} health is {health}");
    Check(collector.GetProperty("problems").EnumerateArray().Select(item => item.GetString()).SequenceEqual(problems),
        $"{name} has expected problem codes in order");
}

static void Install(Store store, string name)
{
    Directory.CreateDirectory(store.Collector(name));
    WriteExecutable(Path.Combine(store.Collector(name), "orisumi-" + name), $$"""
        #!/bin/sh
        [ "$#" = 1 ] && [ "$1" = describe ] || exit 2
        printf '%s\n' '{"name":"{{name}}","version":"0.2.0+fixture","arguments":["capture"]}'
        """);
}

static string Identity(string? hardwareUuid) => $"""
    CREATE TABLE database_identity (singleton INTEGER PRIMARY KEY, database_id TEXT, created_at_utc_ms INTEGER,
        platform TEXT, hardware_uuid TEXT);
    INSERT INTO database_identity VALUES (1, '{new string('0', 32)}', 0, 'macos', {(hardwareUuid is null ? "NULL" : $"'{hardwareUuid}'")});
    """;

static void Execute(SqliteConnection db, string sql)
{
    using var command = db.CreateCommand();
    command.CommandText = sql;
    command.ExecuteNonQuery();
}

static void WriteExecutable(string path, string text)
{
    File.WriteAllText(path, text);
    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
}

// SQLite may add -wal/-shm beside a closed WAL database even on a read-only open; that is accepted.
static string[] Entries(string path) => Directory.GetFileSystemEntries(path, "*", SearchOption.AllDirectories)
    .Where(entry => !entry.EndsWith("-wal", StringComparison.Ordinal) && !entry.EndsWith("-shm", StringComparison.Ordinal))
    .Order(StringComparer.Ordinal).ToArray();

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}


sealed class StatusJobs : ICollectorJobs
{
    public Task<IReadOnlyDictionary<string, SavedJob>> Inspect(IEnumerable<string> sources, CancellationToken token) =>
        Task.FromResult<IReadOnlyDictionary<string, SavedJob>>(sources.Distinct().ToDictionary(name => name,
            name => name == "collecting" ? new SavedJob(name, true, true, "running", 1234, null)
                : new SavedJob(name, false, false, "not_loaded", null, null)));

    public Task<string> Set(string source, JobSpec? desired, CancellationToken token) => throw new Exception("Status modified a job");
    public Task Restore(SavedJob previous, CancellationToken token) => throw new Exception("Status restored a job");
}
