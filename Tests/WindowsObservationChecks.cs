#:project ../Collectors/Windows/Orisumi.Collectors.Windows.csproj
#:property AssemblyName=WindowsObservationChecks
#:property PublishAot=false

using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using Orisumi.Capture.Storage;
using Orisumi.Collectors.Windows;
using Orisumi.Platform;

var temp = Directory.CreateTempSubdirectory("orisumi-windows-check-");
try
{
    using var writer = new CaptureWriter(temp.FullName, WindowsForeground.Collector, (c, r) =>
        c.Execute("INSERT INTO capture_run_configuration VALUES($p0,$p1,$p2,$p3)", r, 300000, true, true));
    var source = new Fixture();
    var observe = new WindowsObservations(source, true, 300000, input: true);
    observe.InputRegistered(true);
    source.Report = observe.ReadStatus;
    var editor = source.Editor;
    using var db = new SqliteConnection(new SqliteConnectionStringBuilder {
        DataSource = new Store(temp.FullName).CurrentDatabase("windows"), Pooling = false, Mode = SqliteOpenMode.ReadOnly
    }.ToString());
    db.Open();
    observe.Start();
    observe.Display(false);
    Save();
    var initial = Number("SELECT COUNT(*) FROM capture_channel_events");
    observe.Reconcile(true);
    observe.Display(false);
    observe.Lock(false);
    observe.TimeZone();
    var unchanged = observe.Flush();
    Check(unchanged.Facts.Count == 0, "stationary snapshots emit no facts");
    unchanged.Commit(writer);

    source.Handle = 11;
    observe.Foreground(11);
    source.Handle = 10;
    observe.Foreground(10);
    source.Text = "B"; observe.Title(10);
    source.Text = "A"; observe.Title(10);
    source.Text = ""; observe.Title(10);
    source.Text = null; observe.Title(10); observe.Title(10);
    source.Text = "A"; observe.Title(10);
    observe.Title(11);
    source.Handle = 0; observe.Foreground(0);
    source.Valid = false;
    source.Handle = 12; observe.Foreground(12);
    source.Valid = true;
    source.Handle = 10; observe.Foreground(10);
    source.Taskbar = false; observe.Applications();
    source.Taskbar = true; observe.Applications();
    source.Taskbar = null; observe.Applications();
    source.Taskbar = true;
    source.Editor = editor with { Elevated = true, Name = "different" }; observe.Applications();

    source.Idle = 300000; observe.Idle();
    source.Idle = 0; observe.Idle();
    source.Idle = null; observe.Idle(); observe.Idle();
    source.Idle = 0; observe.Idle();
    observe.Lock(true); observe.Lock(null); observe.Lock(false);
    observe.Display(true); observe.Display(false);
    source.Zone = new("Asia/Tokyo", "iana"); observe.TimeZone();
    source.Zone = null; observe.TimeZone();
    source.Zone = new("Asia/Shanghai", "iana"); observe.TimeZone();
    observe.Clock();
    Save();

    Check(Text("SELECT group_concat(value,',') FROM (SELECT coalesce(has_app,'?') value FROM foreground_app_events ORDER BY run_sequence)") ==
        "1,1,1,0,?,1", "foreground A B A, empty, failed and recovered");
    Check(Number("SELECT COUNT(*) FROM window_focus_instances WHERE hwnd=10") == 3, "returning to one HWND starts another focus interval");
    Check(Number("SELECT COUNT(*) FROM focused_window_events WHERE has_window IS NULL AND event_kind='read_failed'") == 1, "failed focus is unknown");
    Check(Number("SELECT COUNT(*) FROM window_history WHERE window_title=''") == 1 &&
        Number("SELECT COUNT(*) FROM window_title_observations WHERE title IS NULL") == 1, "empty and failed titles stay distinct");
    Check(Number("SELECT COUNT(*) FROM process_instances WHERE app_name='Editor' AND is_elevated=1") == 1,
        "process identity fills unknown fields only");
    Check(Number("SELECT COUNT(*) FROM afk_events") == 5 && Number("SELECT COUNT(*) FROM afk_events WHERE state IS NULL") == 1, "idle threshold and failed read");
    Check(Number("SELECT COUNT(*) FROM time_zone_events") == 4 && Number("SELECT COUNT(*) FROM clock_events") == 2, "time zone snapshots and clock adjustment");
    Check(Number("SELECT COUNT(*) FROM capture_read_failures WHERE error LIKE '%win32=5%'") == 2, "failed title and idle reads carry the native code");
    Check(Number("SELECT COUNT(*) FROM taskbar_app_intervals WHERE process_instance_id='one'") == 3, "taskbar presence changes independently");

    observe.Key(0x11, 0x1d, false, true);   // Ctrl down
    observe.Key(0x43, 0x2e, false, true);   // C down
    observe.Key(0x43, 0x2e, false, true);   // auto-repeat while held
    observe.Key(0x43, 0x2e, false, false);  // C up
    observe.Key(0x11, 0x1d, false, false);  // Ctrl up
    observe.Key(0x11, 0x1d, true, true);    // right Ctrl (E0) is a different key from left Ctrl
    observe.Key(0x11, 0x1d, true, false);
    observe.Key(0xff, 0x2a, false, true);   // fake key around an E1 sequence
    observe.MouseButton(0, true, (10, 20));
    observe.MouseButton(4, false, null);
    source.Idle = 300000; observe.Idle();
    source.Idle = 5; observe.Activity();    // input while idle reads activity at once
    observe.Key(0x41, 0x1e, false, true);   // A, still held when observation is interrupted below
    Save();
    Check(Number("SELECT COUNT(*) FROM keyboard_presses") == 3 &&
        Number("SELECT modifiers_at_press FROM keyboard_presses WHERE vk_code=0x43") == 2 &&
        Number("SELECT COUNT(*) FROM keyboard_presses WHERE vk_code=0x43 AND released_at_utc_ms >= pressed_at_utc_ms") == 1 &&
        Number("SELECT COUNT(*) FROM keyboard_presses WHERE vk_code=0x11 AND is_extended=1 AND modifiers_at_press=0") == 1,
        "one row per press with modifiers held at press; repeats and fake keys are not presses");
    Check(Number("SELECT COUNT(*) FROM mouse_button_events WHERE event_kind='button_down' AND button=0 AND x_pixels=10 AND y_pixels=20") == 1 &&
        Number("SELECT COUNT(*) FROM mouse_button_events WHERE event_kind='button_up' AND button=4 AND x_pixels IS NULL") == 1,
        "mouse buttons with known and unknown cursor positions");
    Check(Text("SELECT group_concat(state,',') FROM (SELECT state FROM afk_events ORDER BY run_sequence DESC LIMIT 2)") == "active,idle",
        "input while idle returns to active immediately");

    observe.Sleep(true); observe.Sleep(true);
    observe.SessionChanged(false);
    observe.Sleep(false); observe.Sleep(false);
    observe.Reconcile(true); observe.Clock(); observe.TimeZone();
    Save();
    var before = Number("SELECT COUNT(*) FROM foreground_app_events");
    observe.SessionChanged(true);
    Save();
    Check(Number("SELECT COUNT(*) FROM foreground_app_events") == before+1 && Number("SELECT COUNT(*) FROM afk_events") == 8 &&
        Number("SELECT COUNT(*) FROM clock_events") == 3 && Number("SELECT COUNT(*) FROM time_zone_events") == 5,
        "nested interruptions resume only once with fresh initial values");
    Check(Number("SELECT COUNT(*) FROM keyboard_presses WHERE vk_code=0x41 AND released_at_utc_ms IS NULL") == 1 &&
        Number("SELECT COUNT(*) FROM capture_channel_events WHERE channel='input' AND available=0 AND reason='system_sleep'") == 1 &&
        Number("SELECT COUNT(*) FROM capture_channel_events WHERE channel='input' AND available=1") == 2,
        "an interruption ends held keys without a release and pauses the input channel");
    Check(Number("SELECT COUNT(*) FROM capture_channel_events WHERE channel='applications' AND available=0") == 1 &&
        Number("SELECT COUNT(*) FROM capture_channel_events") > initial, "lock and display transitions do not interrupt applications");
    source.WithEditor = false; observe.Applications(); observe.Applications(); Save();
    Check(Number("SELECT COUNT(*) FROM running_app_observations WHERE event_kind='disappeared' AND process_instance_id='one'") == 1, "process exit recorded once");
    source.WithEditor = true;
    source.Editor = editor with { Id = "three", Created = 999 };
    observe.Applications(); Save();
    Check(Number("SELECT COUNT(*) FROM process_instances WHERE pid=10") == 2, "PID reuse starts another process instance");

    var confirmation = Number("SELECT confirmed_until_utc_ms FROM capture_runs");
    var invalid = new Batch(confirmation+1000, [
        (c,r) => c.Execute("INSERT INTO clock_events VALUES($p0,100000,1,'changed',1)",r),
        (c,r) => c.Execute("INSERT INTO clock_events VALUES($p0,100000,2,'changed',2)",r)
    ]);
    try { invalid.Commit(writer); throw new Exception("expected batch failure"); }
    catch (IOException) { }
    Check(Number("SELECT COUNT(*) FROM clock_events WHERE run_sequence=100000") == 0 &&
        Number("SELECT confirmed_until_utc_ms FROM capture_runs") == confirmation &&
        Number("SELECT COUNT(*) FROM capture_read_failures WHERE error LIKE '%UNIQUE%'") == 1, "failed batch rolls back facts and confirmation");

    foreach (var method in typeof(Native).GetMethods(BindingFlags.Static | BindingFlags.NonPublic))
    {
        if (method.GetCustomAttribute<LibraryImportAttribute>() is not { } import) continue;
        var entry = import.EntryPoint ?? method.Name;
        var library = NativeLibrary.Load(import.LibraryName, typeof(Native).Assembly, DllImportSearchPath.System32);
        try { Check(NativeLibrary.TryGetExport(library, entry, out _), $"{import.LibraryName} exports {entry}"); }
        finally { NativeLibrary.Free(library); }
    }
    using var real = new DesktopSource();
    var first = real.MonotonicNanoseconds();
    Check(first > 0 && real.MonotonicNanoseconds() >= first, "QueryInterruptTime is monotonic");
    Check(real.TimeZone() is { Id.Length: > 0 }, "machine time zone readable");
    writer.Stop();
    Console.WriteLine("PASS: Windows observations, focus, taskbar, time, interruptions, process identity and batch rollback");

    void Save() => observe.Flush().Commit(writer);
    object? Scalar(string sql) { using var c = db.CreateCommand(); c.CommandText = sql; return c.ExecuteScalar(); }
    long Number(string sql) => (long)Scalar(sql)!;
    string Text(string sql) => (string)Scalar(sql)!;
}
finally { temp.Delete(true); }

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

sealed class Fixture : IDesktopSource
{
    public ProcessFacts Editor = new("one",10,123,"C:\\Editor.exe","","Editor",null);
    private readonly ProcessFacts browser = new("two",11,456,"C:\\Browser.exe","Pkg!Browser","Browser",false);
    public Action<string,string,int>? Report;
    public nint Handle = 10;
    public bool Valid = true, WithEditor = true;
    public bool? Taskbar = true;
    public long? Idle = 100;
    public Zone? Zone = new("Asia/Shanghai", "iana");
    public string? Text = "A";
    public nint Foreground() => Handle;
    public bool WindowExists(nint hwnd) => Valid;
    public ProcessFacts? Owner(nint hwnd) => hwnd == 10 ? Editor : browser;
    public string? Title(nint hwnd) { Report?.Invoke("window_title", "fixture/title", Text == null ? 5 : 0); return Text; }
    public IReadOnlyList<AppSample> Applications() => WithEditor ? [new(Editor, Taskbar), new(browser, true)] : [new(browser, true)];
    public Session Session() => new(true, false);
    public long? IdleMilliseconds() { Report?.Invoke("afk", "fixture/idle", Idle == null ? 5 : 0); return Idle; }
    public Zone? TimeZone() => Zone;
    public long MonotonicNanoseconds() => 10000;
}
