#:project ../src/Orisumi.Cli/Orisumi.Cli.csproj
#:property AssemblyName=CaptureStatusChecks
#:property PublishAot=false
#:property JsonSerializerIsReflectionEnabledByDefault=false

using System.CommandLine;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Orisumi.Cli;
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
    CREATE TABLE capture_read_failures (observed_at_utc_ms INTEGER);
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
          gui/"$(/usr/bin/id -u)"/dev.orisumi.test_status.*) ;;
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
    foreach (var label in new[] { instance.AgentLabel("idle"), instance.AgentLabel("queued"), instance.AgentLabel("signaled"), "dev.orisumi.other.ignored" })
        File.WriteAllText(Path.Combine(locations.Agents, label + ".plist"), "");
    foreach (var name in new[] { "collecting", "unloaded" })
    {
        Directory.CreateDirectory(store.Collector(name));
        WriteExecutable(Path.Combine(store.Collector(name), "orisumi-" + name), $$"""
            #!/bin/sh
            [ "$#" = 1 ] && [ "$1" = describe ] || exit 2
            printf '%s\n' '{"name":"{{name}}","version":"0.2.0+fixture","arguments":["capture"]}'
            """);
    }
    Directory.CreateDirectory(store.Observations("source_only"));

    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    var started = now - 90_000;
    var confirmed = now - 12_000;
    using (var older = OpenDatabase("archived", "older"))
        Execute(older, $"""
            {RunTables}
            INSERT INTO capture_runs VALUES (8, {started - 1}, NULL, NULL, NULL, NULL, NULL);
            INSERT INTO capture_read_failures VALUES ({now - 1000}), ({now - 3_660_000});
            """);
    using (var latest = OpenDatabase("archived", "latest"))
        Execute(latest, $"""
            {RunTables}
            INSERT INTO capture_runs VALUES (1, {started}, NULL, NULL, NULL, NULL, NULL),
                (2, {started}, '0.1.0+recorded', {confirmed}, {now - 1000}, 'failed', 'fixture error');
            INSERT INTO capture_read_failures VALUES ({now - 1000});
            """);
    using (var unrelated = OpenDatabase("archived", "unrelated"))
        Execute(unrelated, $"CREATE TABLE capture_read_failures (observed_at_utc_ms INTEGER); INSERT INTO capture_read_failures VALUES ({now});");
    using var live = OpenDatabase("collecting", "live");
    Execute(live, $"""
        PRAGMA journal_mode=WAL;
        PRAGMA wal_autocheckpoint=0;
        {RunTables}
        INSERT INTO capture_runs VALUES (1, {started}, '0.2.0+fixture', {confirmed}, NULL, NULL, NULL);
        """);

    using var output = new StringWriter();
    var command = Commands.Create(locations, output, baseDirectory);
    var before = Entries(store.Root);
    var agentsBefore = Entries(locations.Agents);
    Check(await Invoke("describe") == 0, "describe succeeds");
    using var description = JsonDocument.Parse(output.ToString());
    var hostVersion = description.RootElement.GetProperty("version").GetString();

    Check(await Invoke("capture", "status", "--store", store.Root) == 0, "text status succeeds for mixed collector states");
    var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
    Check(lines.Length == 8 && lines[0] == $"test_status ({store.Root}) host {hostVersion}", "text header and one line per collector");
    Check(lines[1].StartsWith("  archived -: not_loaded, started ") && Regex.IsMatch(lines[1], @"confirmed \d+[smhd] ago") &&
        lines[1].EndsWith("stopped (failed), failures (1h) 2"), "text reports stopped run, freshness and failures");
    Check(lines[2].Contains("collecting 0.2.0+fixture: running (pid 4321), started") &&
        lines[3].Contains("idle -: waiting (last exit 17), no run") &&
        lines[4].Contains("queued -: spawn scheduled, no run") &&
        lines[5].Contains("signaled -: waiting (last exit terminated by signal), no run"), "text reports agent details");
    Check(lines[6].Contains("source_only -: not_loaded, no run") &&
        lines[7].Contains("unloaded 0.2.0+fixture: not_loaded, no run"), "text reports observation-only and installed-only collectors");

    Check(await Invoke("--store", store.Root, "capture", "status", "--json") == 0, "JSON status succeeds with a recursive store option");
    using var report = JsonDocument.Parse(output.ToString());
    var status = report.RootElement;
    Check(status.GetProperty("instance").GetString() == instance.Name && status.GetProperty("store").GetString() == store.Root &&
        status.GetProperty("host_version").GetString() == hostVersion, "JSON instance identity matches describe");
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
    Check(before.SequenceEqual(Entries(store.Root)) && agentsBefore.SequenceEqual(Entries(locations.Agents)),
        "status creates no files or directories in the store or LaunchAgents");
    Console.WriteLine("PASS: text and JSON status merge installed collectors, LaunchAgents and observations, including stopped and live WAL runs");

    File.WriteAllText(Path.Combine(locations.Agents, instance.AgentLabel("broken") + ".plist"), "");
    await Expect<IOException>("print failed", ["capture", "status", "--store", store.Root]);
    var checkout = Path.GetDirectoryName(baseDirectory)!;
    File.WriteAllText(Path.Combine(checkout, "Orisumi.slnx"), "<Solution />");
    await Expect<FileNotFoundException>("instance.json", ["capture", "status"]);
    Check(!Directory.Exists(Path.Combine(checkout, ".local")), "implicit status does not initialize a checkout store");
    Console.WriteLine("PASS: unexpected launchctl errors propagate and status stays read-only");

    JsonElement Agent(int index) => collectors[index].GetProperty("agent");

    SqliteConnection OpenDatabase(string source, string name)
    {
        Directory.CreateDirectory(store.Observations(source));
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(store.Observations(source), name + ".sqlite"), Pooling = false,
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

static string[] Entries(string path) => Directory.GetFileSystemEntries(path, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
