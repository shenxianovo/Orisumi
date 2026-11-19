#:project ../src/Collectors/Orisumi.Collectors.MacOS/Orisumi.Collectors.MacOS.csproj
#:property PublishAot=false

using System.Diagnostics;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Orisumi.Collectors.MacOS;
using Orisumi.Platform;

if (!OperatingSystem.IsMacOS()) return;
var run = typeof(MacOSForeground).GetMethod("RunObserver", BindingFlags.Static | BindingFlags.NonPublic)!
    .CreateDelegate<Func<string, string, int?, bool, int?, bool, bool, bool, bool, Task<int>>>();
if (args is ["receiver", var helper, var childStore])
{
    Environment.ExitCode = await run(helper, childStore, null, false, null, false, false, false, false);
    return;
}

var root = Directory.GetCurrentDirectory();
var temp = Directory.CreateTempSubdirectory("orisumi-macos-observation-");
var native = Path.Combine(root, "src/Collectors/Orisumi.Collectors.MacOS/Native");
try
{
    var failureSource = Path.Combine(temp.FullName, "CaptureFailure.swift");
    File.WriteAllText(failureSource, """
        import Foundation

        func captureForeground(in events: ObservationEvents, seconds: Int?, captureWindows: Bool,
                               afkThresholdMS: Int64, captureInput: Bool, mediaChannels: Set<MediaChannel>) throws {
            try events.appendReadFailure(String(repeating: "x", count: 512 * 1024), at: Date())
            try events.flush()
            throw ObservationPipe.Failure.backpressure
        }
        """);
    var failingObserver = Path.Combine(temp.FullName, "failing-observer");
    await Command("xcrun", ["swiftc", failureSource, .. Directory.GetFiles(native, "*.swift")
        .Where(path => Path.GetFileName(path) != "ForegroundCapture.swift"), "-o", failingObserver]);
    using (var draining = Start(failingObserver, [], redirect: true))
    {
        var error = await draining.StandardError.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Check(error?.Contains("backlog exceeded its limit") == true, "the real entry point reports the failure");
        var batch = await draining.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await draining.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Check(draining.ExitCode == 1 && batch is { Length: > 512 * 1024 } && batch.EndsWith("]}"),
            "a failed helper flushes the already queued batch when its reader catches up");
    }
    using (var blockedFailure = Start(failingObserver, [], redirect: true))
    {
        await blockedFailure.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Check(blockedFailure.ExitCode == 1, "failure drain times out if stdout goes unread");
    }
    Console.WriteLine("PASS: native failure drains queued output with a bounded wait");

    var fixture = await Compile("macos_observation");
    var applications = Wrapper("applications", $"exec '{fixture}' applications");
    var store = Path.Combine(temp.FullName, "observation-store");
    Check(await run(applications, store, null, true, 10, true, false, false, false) == 1,
        "a truncated batch fails the resident run");
    Check(await run(fixture, store, null, false, 10, false, true, true, true) == 0, "media fixture completes");
    using var connection = Open(store);
    Check(Number("SELECT COUNT(*) FROM process_instances WHERE pid=123456789") == 2, "a newly verified birth cannot retroactively certify an unknown process segment");
    Check(Number("SELECT COUNT(*) FROM process_instances WHERE pid=123456788") == 2, "an unverified identity cannot span an observation gap");
    Check(Number("SELECT COUNT(*) FROM capture_method_events") == 2, "unchanged capture method is not rewritten");
    Check(Number("SELECT COUNT(*) FROM capture_runs") == 2 && Number("SELECT COUNT(*) FROM host_observations") == 1
        && Number("SELECT COUNT(*) FROM capture_runs JOIN host_observations ON observation_id=host_observation_id WHERE platform='macos' AND store_path=$store", store) == 2,
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
        == "0,1,0,1,0", "output only starts after a responsible application is known");
    Check(Values("SELECT event_kind FROM audio_process_presence_events ORDER BY run_sequence")
        == "snapshot,snapshot,disappeared,appeared,disappeared,appeared,disappeared", "HAL lifetimes are distinct and unmatched output-only clients stay absent");
    Check(Number("""
        SELECT COUNT(*) FROM audio_output_observations o JOIN audio_process_presence_events p
        USING (run_id,run_sequence,audio_process_instance_id) WHERE p.event_kind='disappeared' AND o.is_running=0
        """) == 1, "a departed HAL client stops any previously active output");
    Check(Number("SELECT COUNT(*) FROM audio_process_instances WHERE pid=1234 AND bundle_id='com.google.Chrome.helper' AND process_instance_id IS NULL")
        == 2, "unmatched microphone clients are retained");
    Check(Number("""
        SELECT COUNT(*) FROM audio_process_instances a JOIN process_instances p USING(process_instance_id)
        WHERE a.pid=1234 AND p.pid=1235 AND a.run_id=p.run_id AND p.bundle_id='com.google.Chrome'
        """) == 2, "the helper is linked to Chrome when its responsible process qualifies");
    Check(Number("""
        SELECT COUNT(*) FROM audio_output_observations JOIN audio_process_instances USING(audio_process_instance_id)
        WHERE process_instance_id IS NULL
        """) == 0, "unattributed audio output is omitted");
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

    Check(Number("SELECT confirmed_until_utc_ms FROM capture_runs WHERE stop_reason='failed'") == 1009000,
        "a partial batch leaves progress at the last committed Swift observation time");
    Check(Number("SELECT COUNT(*) FROM capture_read_failures WHERE observed_at_utc_ms=1002000 AND error='windows: read_failed'") == 1
        && Number("SELECT COUNT(*) FROM capture_read_failures WHERE error LIKE '%incomplete JSONL batch'") == 1,
        "source reads and an incomplete transport round leave failure evidence");

    var facts = await Compile("macos_facts");
    var factsStore = Path.Combine(temp.FullName, "facts-store");
    Check(await run(facts, factsStore, null, true, null, true, false, false, false) == 0, "facts fixture completes");
    using (var db = Open(factsStore))
    {
        Check(NumberOn(db, "SELECT COUNT(*) FROM process_instances") == 2, "foreground and windows share the application instance until PID reuse");
        Check(ValuesOn(db, "SELECT COALESCE(has_window,-1) || ':' || COALESCE(window_title,'<NULL>') FROM window_history ORDER BY run_sequence")
            == "1:A,1:B,1:C,0:<NULL>,-1:<NULL>,1:", "focus and title preserve empty and unknown states");
        Check(ValuesOn(db, "SELECT available FROM capture_channel_events ORDER BY run_sequence") == "1,0,1", "only channel transitions are recorded");
        Check(NumberOn(db, """
            SELECT COUNT(*) FROM capture_run_configuration JOIN capture_runs USING(run_id)
            WHERE capture_windows=1 AND capture_input=1 AND afk_threshold_ms=300000 AND stop_reason='completed'
            """) == 1, "config and completion are written in C#");
        Check(NumberOn(db, "SELECT COUNT(*) FROM pragma_foreign_key_check") == 0, "window facts have valid references");
        Check(NumberOn(db, """
            SELECT COUNT(*) FROM keyboard_presses k JOIN mouse_button_events m USING(run_id)
            WHERE k.run_sequence < m.run_sequence AND k.pressed_at_utc_ms < m.observed_at_utc_ms
              AND k.key_code=42 AND k.modifiers_at_press=131072 AND k.released_at_utc_ms IS NULL
              AND m.x_points=-42.25 AND m.y_points=314.5
            """) == 1, "input values and observation order survive a delayed key completion");
        Execute(db, """
            CREATE TRIGGER reject_title BEFORE INSERT ON window_title_observations
            BEGIN SELECT RAISE(ABORT,'title marker rejected'); END;
            """);
    }
    Check(await run(facts, factsStore, null, true, null, true, false, false, false) == 1, "failed storage ends the run");
    using (var db = Open(factsStore))
        Check(NumberOn(db, """
            SELECT COUNT(*) FROM capture_runs r JOIN capture_read_failures f USING(run_id)
            WHERE r.stop_reason='failed' AND r.confirmed_until_utc_ms IS NULL AND f.error LIKE '%title marker rejected%'
              AND NOT EXISTS (SELECT 1 FROM process_instances p WHERE p.run_id=r.run_id)
            """) == 1, "a failed batch rolls back its facts and leaves error evidence");

    using var timeline = new StringWriter();
    await ActivityTimeline.Write(factsStore, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1), timeline);
    Check(timeline.ToString().Contains("focused_window") && timeline.ToString().Contains("capture_run"), "timeline reads resident run IDs");

    var channels = await Compile("macos_channels");
    var channelsStore = Path.Combine(temp.FullName, "channels-store");
    Check(await run(channels, channelsStore, null, true, null, true, false, false, true) == 0, "channel contract");
    using (var db = Open(channelsStore))
    {
        Check(NumberOn(db, "SELECT COUNT(*) FROM keyboard_presses WHERE key_code=12 AND released_at_utc_ms IS NOT NULL") == 1,
            "an input completion refers to its original sequence");
        Check(ValuesOn(db, "SELECT bundle_id FROM process_instances ORDER BY pid")
            == "fixture.editor,fixture.browser,fixture.promoted,fixture.foreground-accessory,fixture.sampled-policy",
            "only processes observed as regular or foreground reach storage");
        Check(ValuesOn(db, """
            SELECT activation_policy FROM application_activation_policy_observations JOIN process_instances USING(process_instance_id)
            WHERE bundle_id='fixture.promoted' ORDER BY run_sequence
            """) == "regular,accessory,accessory", "policy history continues after promotion and gap");
        Check(NumberOn(db, "SELECT COUNT(*) FROM window_title_observations WHERE title='Loading…'") == 1,
            "loading window titles are retained");
        Check(ValuesOn(db, "SELECT event_kind || ':' || COALESCE(is_locked,'?') FROM screen_lock_events ORDER BY run_sequence")
            == "snapshot:?,transition:1,transition:0,snapshot:1", "screen lock uses current session values at start and resume");
        Check(ValuesOn(db, "SELECT event_kind || ':' || COALESCE(is_asleep,'?') FROM display_sleep_events ORDER BY run_sequence")
            == "snapshot:0,transition:1,transition:0,snapshot:?", "display sleep and wake with independently read initial state");
    }

    // Compile the real entry point with denied permissions without changing TCC.
    var permissions = Path.Combine(temp.FullName, "Permissions.swift");
    File.WriteAllText(permissions, "func AXIsProcessTrusted() -> Bool { false }\nfunc CGPreflightListenEventAccess() -> Bool { false }\n");
    var observer = Path.Combine(temp.FullName, "observer");
    await Command("xcrun", ["swiftc", permissions, .. Directory.GetFiles(native, "*.swift"), "-o", observer]);
    var denied = Path.Combine(temp.FullName, "denied-store");
    Check(await run(observer, denied, 1, true, null, true, false, false, false) == 0, "observer finishes a timed run");
    using (var db = Open(denied))
        Check(NumberOn(db, """
            SELECT (SELECT COUNT(*) FROM capture_permission_events WHERE event_kind='snapshot_denied')=2
              AND (SELECT COUNT(*) FROM capture_channel_events WHERE channel IN ('windows','input') AND available=0)=2
              AND EXISTS(SELECT 1 FROM foreground_app_events)
              AND EXISTS(SELECT 1 FROM afk_events)
              AND EXISTS(SELECT 1 FROM screen_lock_events WHERE event_kind='snapshot')
              AND EXISTS(SELECT 1 FROM display_sleep_events WHERE event_kind='snapshot')
              AND EXISTS(SELECT 1 FROM capture_runs WHERE stop_reason='completed' AND confirmed_until_utc_ms IS NOT NULL)
              AND (SELECT COUNT(*) FROM clock_events WHERE event_kind='snapshot' AND monotonic_ns > 0)=1;
            """) == 1, "denied channels leave evidence while other channels capture and stop normally");

    using (var blocked = Start(facts, ["backpressure"], redirect: true))
    {
        await blocked.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Check(blocked.ExitCode == 1 && (await blocked.StandardError.ReadToEndAsync()).Contains("backlog exceeded its limit"),
            "an unread pipe trips bounded backpressure without hanging");
    }

    foreach (var label in new[] { "signal", "receiver-crash", "observer-crash" })
    {
        var isolatedStore = Path.Combine(temp.FullName, label + "-store");
        var pidFile = Path.Combine(temp.FullName, label + ".pid");
        var tracked = Wrapper(label, $"echo $$ > '{pidFile}'\nexec '{observer}'");
        using var receiver = Start("dotnet", [Assembly.GetExecutingAssembly().Location, "receiver", tracked, isolatedStore]);
        Process? child = null;
        try
        {
            var ready = false;
            for (var attempt = 0; attempt < 200 && !ready; attempt++)
            {
                await Task.Delay(50);
                if (!File.Exists(pidFile) || !int.TryParse(File.ReadAllText(pidFile), out var pid)) continue;
                child ??= Process.GetProcessById(pid);
                if (new Store(isolatedStore).CurrentDatabase("macos") is null) continue;
                using var db = Open(isolatedStore);
                ready = NumberOn(db, "SELECT COUNT(*) FROM capture_runs WHERE confirmed_until_utc_ms IS NOT NULL") == 1;
            }
            Check(ready && child != null, "receiver and observer recorded the initial round");
            if (label == "receiver-crash") receiver.Kill();
            else if (label == "observer-crash") child!.Kill();
            else await Command("/bin/kill", ["-TERM", receiver.Id.ToString()]);
            await receiver.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await child!.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            using var result = Open(isolatedStore);
            var correct = label switch
            {
                "receiver-crash" => NumberOn(result, "SELECT COUNT(*) FROM capture_runs WHERE stopped_at_utc_ms IS NULL") == 1,
                "observer-crash" => receiver.ExitCode == 1 && NumberOn(result, """
                    SELECT COUNT(*) FROM capture_runs WHERE stop_reason='failed' AND error LIKE '%observer exited with code%'
                    """) == 1,
                _ => receiver.ExitCode == 0 && NumberOn(result, "SELECT COUNT(*) FROM capture_runs WHERE stop_reason='completed'") == 1,
            };
            Check(correct, $"{label} leaves the correct run state and Swift exits");
        }
        finally
        {
            if (!receiver.HasExited) receiver.Kill();
            if (child is not null)
            {
                if (!child.HasExited) child.Kill();
                child.Dispose();
            }
        }
    }
    Console.WriteLine("PASS: macOS JSONL, writer transactions, denied permissions, backpressure, and process shutdown");

    async Task<string> Compile(string fixtureName)
    {
        var directory = Directory.CreateDirectory(Path.Combine(temp.FullName, fixtureName));
        var main = Path.Combine(directory.FullName, "main.swift");
        File.Copy(Path.Combine(root, "tests", fixtureName + ".swift"), main);
        var binary = Path.Combine(directory.FullName, fixtureName);
        await Command("xcrun", ["swiftc", main, .. Directory.GetFiles(native, "*.swift").Where(path => Path.GetFileName(path) != "main.swift"), "-o", binary]);
        return binary;
    }

    string Wrapper(string name, string body)
    {
        var path = Path.Combine(temp.FullName, name + ".sh");
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    long Number(string sql, string? storePath = null) => NumberOn(connection, sql, storePath);
    string Values(string sql) => ValuesOn(connection, sql);
}
finally { temp.Delete(true); }

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

static SqliteConnection Open(string store)
{
    var path = new Store(store).CurrentDatabase("macos") ?? throw new Exception("missing fixture database");
    var db = new SqliteConnection($"Data Source={path};Pooling=False");
    db.Open();
    return db;
}

static long NumberOn(SqliteConnection db, string sql, string? storePath = null)
{
    using var command = db.CreateCommand();
    command.CommandText = sql;
    if (storePath is not null) command.Parameters.AddWithValue("$store", storePath);
    return (long)command.ExecuteScalar()!;
}

static string ValuesOn(SqliteConnection db, string sql)
{
    using var command = db.CreateCommand();
    command.CommandText = sql;
    using var reader = command.ExecuteReader();
    var values = new List<string>();
    while (reader.Read()) values.Add(reader.GetString(0));
    return string.Join(",", values);
}

static void Execute(SqliteConnection db, string sql)
{
    using var command = db.CreateCommand();
    command.CommandText = sql;
    command.ExecuteNonQuery();
}

static Process Start(string executable, string[] arguments, bool redirect = false)
{
    var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = redirect, RedirectStandardError = redirect };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    return Process.Start(start)!;
}

static async Task Command(string executable, string[] arguments)
{
    using var process = Start(executable, arguments);
    await process.WaitForExitAsync();
    Check(process.ExitCode == 0, $"{executable} exited with code {process.ExitCode}");
}
