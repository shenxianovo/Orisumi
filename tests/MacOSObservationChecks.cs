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
    // The build copies the collector's and the shared Storage schema into one directory.
    foreach (var schema in new[] { "src/Collectors/Orisumi.Collectors.MacOS/Schema", "src/Collectors/Storage/Schema" }.Select(path => Path.Combine(root, path)))
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
    Check(Number("SELECT COUNT(*) FROM capture_runs") == 2 && Number("SELECT COUNT(*) FROM host_observations") == 1
        && Number($"SELECT COUNT(*) FROM capture_runs JOIN host_observations ON observation_id=host_observation_id WHERE hardware_uuid GLOB '????????-????-????-????-????????????' AND boot_id IS NOT NULL AND store_path='{Path.GetDirectoryName(temp.FullName.TrimEnd('/'))}'") == 2,
        "runs on an unchanged host share one host observation");
    Check(Number("SELECT COUNT(*) FROM database_identity") == 1, "a new database records its identity");
    Check(Number("SELECT COUNT(*) FROM capture_coverage WHERE evidence='unavailable' AND started_at_utc_ms=1002000 AND stopped_at_utc_ms=1003000") == 1, "gap has its actual observation interval");
    Check(Number("SELECT COUNT(*) FROM capture_coverage WHERE evidence='failed'") == 1, "failed stop remains visible");
    Check(Values("""
        SELECT COALESCE(activation_policy,'unknown') FROM application_activation_policy_observations
        JOIN process_instances USING(process_instance_id) WHERE pid=123456787 ORDER BY run_sequence
        """) == "regular,accessory,regular,regular,unknown,regular,prohibited",
        "policy changes, including unknown, are sparse and restart with a baseline");
    Check(Number("""
        SELECT COUNT(*) FROM application_activation_policy_observations p
        JOIN running_app_observations r USING (run_id, run_sequence, process_instance_id)
        WHERE r.event_kind IN ('snapshot','appeared')
        """) == Number("SELECT COUNT(*) FROM running_app_observations WHERE event_kind IN ('snapshot','appeared')"),
        "presence and policy share the initial observation");
    Check(Values("""
        SELECT started_at_utc_ms || '-' || stopped_at_utc_ms FROM regular_app_intervals
        WHERE pid=123456787 ORDER BY run_sequence
        """) == "1000000-1001000,1002000-1003000,1004000-1005000,1006000-1007000",
        "regular intervals end on change, gap, unknown, or disappearance");
    Check(Values("SELECT COALESCE(is_running,'unknown') FROM audio_input_observations ORDER BY run_sequence")
        == "0,1,unknown,0,0,0,0", "input has its own sparse observations and a new baseline after listener recovery");
    Check(Values("SELECT is_running FROM audio_output_observations ORDER BY run_sequence")
        == "0,1,0,0,0,1,0", "output has its own sparse observations and restarts after a session gap");
    Check(Values("SELECT event_kind FROM audio_process_presence_events ORDER BY run_sequence")
        == "snapshot,snapshot,disappeared,appeared,disappeared", "disappearance and reused HAL object IDs start distinct lifetimes");
    Check(Number("""
        SELECT COUNT(*) FROM audio_output_observations o JOIN audio_process_presence_events p
        USING (run_id,run_sequence,audio_process_instance_id) WHERE p.event_kind='disappeared' AND o.is_running=0
        """) == 1, "a departed HAL client stops any previously active output");
    Check(Number("SELECT COUNT(*) FROM audio_process_instances WHERE pid=1234 AND bundle_id='' AND process_instance_id IS NULL")
        == 2, "unmatched audio processes are retained");
    Check(Number("""
        SELECT COUNT(*) FROM audio_process_instances a JOIN process_instances p USING(process_instance_id)
        WHERE a.pid=p.pid AND a.run_id=p.run_id AND p.bundle_id='fixture'
        """) == 1, "the current audio object can be linked after its application becomes known");
    Check(Values("SELECT event_kind || ':' || cmio_object_id FROM camera_device_presence_events ORDER BY run_sequence")
        == "appeared:17,disappeared:17,appeared:18", "camera attachment and removal are captured");
    Check(Values("SELECT is_running FROM camera_running_observations ORDER BY run_sequence") == "0,1,0,0",
        "camera state has its own baseline and changes");
    Check(Number("SELECT COUNT(*) FROM camera_devices WHERE device_uid='fixture-camera' AND device_name='Camera'") == 1,
        "a returning camera with the same UID keeps its identity");
    Check(Values("""
        SELECT evidence || ':' || detail FROM capture_coverage WHERE scope='camera' AND mode='snapshot'
        ORDER BY started_at_utc_ms, evidence DESC
        """) == "unavailable:read_failed,available:snapshot_received", "unreadable and empty device lists differ");
    Check(Number("""
        SELECT COUNT(*) FROM capture_coverage WHERE scope='microphone' AND mode='snapshot' AND evidence='unavailable'
        """) == 1 && Number("""
        SELECT COUNT(*) FROM capture_coverage WHERE scope='audio_output' AND mode='snapshot' AND evidence='unavailable'
        """) == 1, "audio channel coverage shows session gaps");
    Check(Values("""
        SELECT channel FROM capture_channel_events
        WHERE available=1 AND channel IN ('microphone','audio_output') ORDER BY run_sequence
        """) == "microphone,audio_output,audio_output,microphone", "input listener failure does not block output");
    Check(Number("SELECT COUNT(*) FROM pragma_foreign_key_check") == 0, "macOS facts reference valid identities");
    Console.WriteLine("PASS: macOS identities, activation policies, audio and camera changes, and coverage gaps");
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
              AND EXISTS(SELECT 1 FROM capture_runs WHERE stop_reason='stopped')
              AND (SELECT COUNT(*) FROM clock_events WHERE event_kind='snapshot' AND monotonic_ns > 0)=1;
            """;
        Check((long)command.ExecuteScalar()! == 1, "denied channels must leave permission evidence while other channels capture and stop normally");
    }
    Console.WriteLine("PASS: denied permissions preserve other channels; failed stop is reported");
    long Number(string sql) { using var command = connection.CreateCommand(); command.CommandText = sql; return (long)command.ExecuteScalar()!; }
    string Values(string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read()) values.Add(reader.GetString(0));
        return string.Join(",", values);
    }
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
