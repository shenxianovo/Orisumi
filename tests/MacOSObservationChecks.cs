#:package Microsoft.Data.Sqlite@10.0.14
#:property PublishAot=false

using System.Diagnostics;
using Microsoft.Data.Sqlite;

if (!OperatingSystem.IsMacOS()) return;
var root = Directory.GetCurrentDirectory();
var temp = Directory.CreateTempSubdirectory("orisumi-macos-observation-");
try
{
    var native = Path.Combine(root, "src/Collectors/Orisumi.Collectors.MacOS/Native");
    var main = Path.Combine(temp.FullName, "main.swift");
    File.Copy(Path.Combine(root, "tests/macos_observation.swift"), main);
    var binary = Path.Combine(temp.FullName, "checks");
    await Run("/usr/bin/xcrun", ["swiftc", main, .. Directory.GetFiles(native, "*.swift").Where(path => Path.GetFileName(path) != "main.swift"), "-lsqlite3", "-o", binary]);
    var schema = Path.Combine(root, "src/Collectors/Orisumi.Collectors.MacOS/Schema");
    foreach (var file in Directory.GetFiles(schema, "*", SearchOption.AllDirectories))
    {
        var target = Path.Combine(temp.FullName, "Orisumi.Collectors.MacOS/Schema", Path.GetRelativePath(schema, file));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(file, target);
    }
    var db = Path.Combine(temp.FullName, "macos.sqlite");
    await Run(binary, [db]);
    using var connection = new SqliteConnection($"Data Source={db};Pooling=False");
    connection.Open();
    Check(Number("SELECT COUNT(*) FROM process_instances WHERE pid=123456789") == 2, "a newly verified birth cannot retroactively certify an unknown process segment");
    Check(Number("SELECT COUNT(*) FROM process_instances WHERE pid=123456788") == 2, "an unverified identity cannot span an observation gap");
    Check(Number("SELECT COUNT(*) FROM capture_method_events") == 2, "unchanged capture method is not rewritten");
    Check(Number("SELECT COUNT(*) FROM capture_coverage WHERE evidence='unavailable' AND started_at_utc_ms=1002000 AND stopped_at_utc_ms=1003000") == 1, "gap has its actual observation interval");
    Check(Number("SELECT COUNT(*) FROM capture_coverage WHERE evidence='failed'") == 1, "failed stop remains visible");
    Console.WriteLine("PASS: macOS identities, sparse methods and coverage evidence");
    // Exercise the real entry point with denied system permissions, without changing TCC.
    var permissions = Path.Combine(temp.FullName, "Permissions.swift");
    File.WriteAllText(permissions, "func AXIsProcessTrusted() -> Bool { false }\nfunc CGPreflightListenEventAccess() -> Bool { false }\n");
    await Run("/usr/bin/xcrun", ["swiftc", permissions, .. Directory.GetFiles(native, "*.swift"), "-lsqlite3", "-o", binary]);
    using (var command = connection.CreateCommand())
    {
        command.CommandText = "CREATE TRIGGER reject_stop BEFORE UPDATE OF stopped_at_utc_ms ON capture_runs BEGIN SELECT RAISE(ABORT,'stop marker rejected'); END";
        command.ExecuteNonQuery();
    }
    Check((await Run(binary, [db, "1"], expectedExit: 1)).Contains("stop marker rejected"), "stop write failure must reach stderr and the exit code");
    using (var command = connection.CreateCommand())
    {
        command.CommandText = "CREATE TRIGGER reject_sample BEFORE INSERT ON afk_events BEGIN SELECT RAISE(ABORT,'sample marker rejected'); END";
        command.ExecuteNonQuery();
    }
    var failed = await Run(binary, [db, "1"], expectedExit: 1);
    Check(failed.Contains("sample marker rejected") && failed.Contains("stop marker rejected"), "both capture and stop failures must be visible");
    var denied = Path.Combine(temp.FullName, "denied.sqlite");
    await Run(binary, [denied, "1", "--windows", "--input"]);
    using (var deniedDb = new SqliteConnection($"Data Source={denied};Pooling=False"))
    {
        deniedDb.Open();
        using var command = deniedDb.CreateCommand();
        command.CommandText = """
            SELECT (SELECT COUNT(*) FROM capture_permission_events WHERE event_kind='snapshot_denied')=2
              AND (SELECT COUNT(*) FROM capture_channel_events WHERE channel IN ('windows','input') AND available=0)=2
              AND EXISTS(SELECT 1 FROM foreground_app_events)
              AND EXISTS(SELECT 1 FROM afk_events)
              AND EXISTS(SELECT 1 FROM capture_runs WHERE stop_reason='stopped');
            """;
        Check((long)command.ExecuteScalar()! == 1, "denied channels must leave permission evidence while other channels capture and stop normally");
    }
    Console.WriteLine("PASS: denied permissions preserve other channels; failed stop is reported");
    long Number(string sql) { using var command = connection.CreateCommand(); command.CommandText = sql; return (long)command.ExecuteScalar()!; }
}
finally { temp.Delete(true); }

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static async Task<string> Run(string executable, string[] arguments, int expectedExit = 0)
{
    var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var process = Process.Start(start)!;
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    Console.Write(await stdout);
    var error = await stderr;
    Check(process.ExitCode == expectedExit, $"Expected exit {expectedExit}, got {process.ExitCode}: {error}");
    return error;
}
