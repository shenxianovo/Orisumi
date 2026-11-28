using Microsoft.Data.Sqlite;
using Orisumi.Capture.Storage;

namespace Orisumi.Collectors.Windows;

internal sealed record ProcessFacts(string Id, uint Pid, long? Created, string? Path, string? Aumid, string? Name, bool? Elevated);
internal sealed record AppSample(ProcessFacts Process, bool? Taskbar);
internal sealed record Zone(string Id, string Kind);
internal readonly record struct Session(bool? Active, bool? Locked);
internal interface IDesktopSource
{
    nint Foreground();
    bool WindowExists(nint hwnd);
    ProcessFacts? Owner(nint hwnd);
    string? Title(nint hwnd);
    IReadOnlyList<AppSample> Applications();
    Session Session();
    long? IdleMilliseconds();
    Zone? TimeZone();
    long MonotonicNanoseconds();
}

internal delegate void WriteFact(SqliteCommand command, long run);
internal sealed record Batch(long Through, List<WriteFact> Facts)
{
    internal void Commit(CaptureWriter writer) => writer.Run((command, run) =>
    {
        foreach (var write in Facts) write(command, run);
        return 0;
    }, observedThrough: Through);
}

// All comparisons and sequence allocation belong to the window thread. Batches own immutable observations.
internal sealed class WindowsObservations(IDesktopSource source, bool windows, long threshold, bool input = false)
{
    private List<WriteFact> pending = [];
    private long sequence;
    private readonly Dictionary<string, object?> last = [];
    private readonly Dictionary<string, ProcessFacts> identities = [];
    private readonly Dictionary<string, bool?> running = [];
    private readonly Dictionary<string, bool> availability = [];
    private readonly Dictionary<string, string> failures = [];
    private nint focused;
    private string? focusId;
    private bool sleeping;
    private bool? sessionActive;
    private bool inputRegistered;
    private readonly record struct Press(long Sequence, long At, int Modifiers);
    private readonly Dictionary<(ushort Vk, ushort Scan, bool Extended), Press> keysDown = [];
    internal bool Observing { get; private set; }
    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    internal Batch Flush()
    {
        var batch = new Batch(Now, pending);
        pending = [];
        return batch;
    }

    internal void Start()
    {
        var session = source.Session();
        Lock(session.Locked);
        SessionChanged(session.Active);
        // A running thread is evidence that the system is awake.
        Sleep(false);
    }

    internal void SessionChanged(bool? active)
    {
        sessionActive = active;
        if (!Changed("session", active)) return;
        Channel("user_session", active.HasValue);
        Event("user_session_events", active switch { true => "session_active", false => "session_inactive", null => null });
        UpdateObservation();
    }

    internal void Sleep(bool asleep)
    {
        sleeping = asleep;
        if (!Changed("sleep", asleep)) return;
        Channel("system_sleep", true);
        Event("system_sleep_events", asleep ? "sleep" : "wake");
        UpdateObservation();
    }

    internal void Destroyed(nint hwnd)
    {
        if (hwnd == 0 || hwnd != focused) return;
        last.Remove("focus");
        Foreground(source.Foreground());
    }

    private void UpdateObservation()
    {
        var active = sessionActive == true && !sleeping;
        if (Observing == active && last.ContainsKey("initialized")) return;
        last["initialized"] = true;
        Observing = active;
        if (!active)
        {
            ReleaseKeys();
            foreach (var channel in (windows
                ? new[] { "applications", "windows", "window_title", "afk", "time_zone", "clock" }
                : ["applications", "afk", "time_zone", "clock"]).Concat(input ? ["input"] : []))
                Channel(channel, false, sleeping ? "system_sleep" : "session_inactive");
            foreach (var key in new[] { "foreground", "focus", "title", "afk", "zone" }) last.Remove(key);
            running.Clear();
            focusId = null;
            focused = 0;
            return;
        }
        Channel("applications", true);
        Method("applications", "notification");
        Method("taskbar_windows", "snapshot");
        if (windows)
        {
            Method("windows", "notification");
            Method("window_title", "snapshot");
        }
        Method("afk", "snapshot");
        if (input)
        {
            Channel("input", inputRegistered, inputRegistered ? null : "register_failed");
            if (inputRegistered) Method("input", "notification");
        }
        Applications(initial: true);
        Foreground(source.Foreground());
        Idle();
        TimeZone();
        Clock(initial: true);
    }

    internal void Lock(bool? locked) => State("screen_lock", "screen_lock_events", locked);
    internal void Display(bool? asleep) => State("display_sleep", "display_sleep_events", asleep);

    private void State(string channel, string table, bool? state)
    {
        var initial = !last.ContainsKey(channel);
        Channel(channel, state.HasValue);
        if (Changed(channel, state)) Event(table, initial ? "snapshot" : "transition", state);
    }

    internal void Applications(bool initial = false)
    {
        if (!Observing) return;
        var current = source.Applications();
        var present = new HashSet<string>();
        foreach (var app in current)
        {
            present.Add(app.Process.Id);
            var exists = running.TryGetValue(app.Process.Id, out var taskbar);
            var seq = ++sequence;
            var at = Now;
            Identity(app.Process);
            if (!exists) Row("running_app_observations", seq, at, initial ? "snapshot" : "appeared", app.Process.Id);
            if (!exists || app.Taskbar != taskbar) Row("taskbar_window_observations", seq, at, app.Process.Id, app.Taskbar);
            running[app.Process.Id] = app.Taskbar;
        }
        foreach (var id in running.Keys.Where(id => !present.Contains(id)).ToArray())
        {
            Event("running_app_observations", "disappeared", id);
            running.Remove(id);
            identities.Remove(id);
        }
    }

    internal void Foreground(nint hwnd)
    {
        if (!Observing) return;
        bool? exists = hwnd == 0 ? false : source.WindowExists(hwnd) ? true : null;
        var process = exists is true ? source.Owner(hwnd) : null;
        if (process != null)
        {
            Identity(process);
            if (!running.ContainsKey(process.Id)) Applications();
        }
        var initial = !last.ContainsKey("foreground");
        var seq = ++sequence;
        var at = Now;
        if (Changed("foreground", (exists, process?.Id)))
            Row("foreground_app_events", seq, at, initial ? "snapshot" : "activated", exists, process?.Id);
        if (!windows) return;
        var initialFocus = !last.ContainsKey("focus");
        if (!Changed("focus", (hwnd, exists, process?.Id))) return;
        focused = hwnd;
        focusId = exists is true ? Guid.NewGuid().ToString("N") : null;
        if (focusId is { } id)
        {
            var owner = process?.Id;
            pending.Add((command, run) => command.Execute("INSERT INTO window_focus_instances VALUES($p0,$p1,$p2,$p3)",
                id, run, owner, (long)hwnd));
        }
        Channel("windows", exists.HasValue);
        var title = focusId != null ? source.Title(focused) : null;
        Channel("window_title", title != null, exists == false ? "no_focused_window" : null);
        // Availability must precede the first value at the same observation time in run sequence.
        seq = ++sequence;
        Row("focused_window_events", seq, at, exists is null ? "read_failed" : initialFocus ? "snapshot" : "focus_changed", exists, focusId);
        last["title"] = title;
        if (focusId != null) Row("window_title_observations", seq, at, focusId, title);
    }

    internal void Title(nint hwnd)
    {
        if (Observing && focusId != null && hwnd == focused) ReadTitle(++sequence, Now);
    }

    private void ReadTitle(long seq, long at)
    {
        var title = source.Title(focused);
        if (Channel("window_title", title != null)) seq = ++sequence;
        if (Changed("title", title)) Row("window_title_observations", seq, at, focusId, title);
    }

    internal void Reconcile(bool checkTitle)
    {
        if (!Observing) return;
        Applications();
        Foreground(source.Foreground());
        if (checkTitle) Title(focused);
        Idle();
    }

    internal void Idle()
    {
        if (!Observing) return;
        var idle = source.IdleMilliseconds();
        string? state = idle is null ? null : idle >= threshold ? "idle" : "active";
        var initial = !last.ContainsKey("afk");
        Channel("afk", state != null);
        if (Changed("afk", state)) Event("afk_events", initial ? "snapshot" : "transition", state, idle);
    }

    internal void InputRegistered(bool registered) => inputRegistered = registered;

    // Raw Input reports each key's make and break; makes repeated while the key is held are not separate presses.
    internal void Key(ushort vk, ushort scan, bool extended, bool pressed)
    {
        // 255 marks the fake keys Raw Input adds around E1 sequences such as Pause.
        if (!Observing || !input || vk is 0 or 255) return;
        var key = (vk, scan, extended);
        if (pressed)
        {
            if (!keysDown.ContainsKey(key))
                keysDown[key] = new(++sequence, Now, keysDown.Keys.Aggregate(0, (bits, held) => bits | Modifier(held.Vk)));
        }
        else if (keysDown.Remove(key, out var press)) WritePress(key, press, Now);
        Activity();
    }

    internal void MouseButton(int button, bool pressed, (int X, int Y)? cursor)
    {
        if (!Observing || !input) return;
        Event("mouse_button_events", pressed ? "button_down" : "button_up", cursor?.X, cursor?.Y, button);
        Activity();
    }

    // Moves and wheel only refresh activity; they are not stored.
    internal void Activity()
    {
        if (Observing && input && Equals(last.GetValueOrDefault("afk"), "idle")) Idle();
    }

    // Keys still held when observation ends have no seen release.
    internal void ReleaseKeys()
    {
        foreach (var (key, press) in keysDown.OrderBy(pair => pair.Value.Sequence)) WritePress(key, press, null);
        keysDown.Clear();
    }

    private void WritePress((ushort Vk, ushort Scan, bool Extended) key, Press press, long? released) =>
        pending.Add((command, run) => command.Execute("""
            INSERT INTO keyboard_presses(run_id,run_sequence,vk_code,scan_code,is_extended,pressed_at_utc_ms,released_at_utc_ms,modifiers_at_press)
            VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7)
            """, run, press.Sequence, (long)key.Vk, (long)key.Scan, key.Extended, press.At, released, (long)press.Modifiers));

    private static int Modifier(ushort vk) => vk switch
    {
        0x10 or 0xA0 or 0xA1 => 1, // Shift
        0x11 or 0xA2 or 0xA3 => 2, // Ctrl
        0x12 or 0xA4 or 0xA5 => 4, // Alt
        0x5B or 0x5C => 8,         // Windows key
        _ => 0,
    };

    internal void TimeZone()
    {
        if (!Observing) return;
        var zone = source.TimeZone();
        Channel("time_zone", zone != null);
        if (Changed("zone", zone)) Event("time_zone_events", zone?.Id, zone?.Kind);
    }

    internal void Clock(bool initial = false)
    {
        if (!Observing) return;
        Channel("clock", true);
        Event("clock_events", initial ? "snapshot" : "changed", source.MonotonicNanoseconds());
    }

    private void Identity(ProcessFacts p)
    {
        if (identities.TryGetValue(p.Id, out var previous))
        {
            p = previous with { Created = previous.Created ?? p.Created, Path = previous.Path ?? p.Path,
                Aumid = previous.Aumid ?? p.Aumid, Name = previous.Name ?? p.Name, Elevated = previous.Elevated ?? p.Elevated };
            if (p == previous) return;
        }
        identities[p.Id] = p;
        pending.Add((command, run) => command.Execute("""
            INSERT INTO process_instances VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7)
            ON CONFLICT(process_instance_id) DO UPDATE SET created_at_filetime=excluded.created_at_filetime,
                executable_path=excluded.executable_path, aumid=excluded.aumid, app_name=excluded.app_name, is_elevated=excluded.is_elevated
            """, p.Id, run, (long)p.Pid, p.Created, p.Path, p.Aumid, p.Name, p.Elevated));
    }

    internal void ReadStatus(string scope, string operation, int error)
    {
        if (error == 0) { failures.Remove(operation); return; }
        var detail = $"{operation}: win32={error}";
        if (failures.TryGetValue(operation, out var previous) && previous == detail) return;
        failures[operation] = detail;
        var at = Now;
        pending.Add((command, run) => command.Execute("""
            INSERT INTO capture_read_failures(run_id,observed_at_utc_ms,error,scope) VALUES($p0,$p1,$p2,$p3)
            """, run, at, detail, scope));
    }

    private bool Channel(string channel, bool available, string? reason = null)
    {
        if (availability.TryGetValue(channel, out var old) && available == old) return false;
        availability[channel] = available;
        Event("capture_channel_events", channel, available, reason ?? (available ? "sample_received" : "read_failed"));
        return true;
    }

    private void Method(string channel, string mode) => Event("capture_method_events", channel, mode);

    private bool Changed(string key, object? value)
    {
        if (last.TryGetValue(key, out var previous) && Equals(previous, value)) return false;
        last[key] = value;
        return true;
    }

    private void Event(string table, params object?[] values) => Row(table, ++sequence, Now, values);

    private void Row(string table, long seq, long at, params object?[] fields)
    {
        pending.Add((command, run) =>
        {
            object?[] values = [run, seq, at, .. fields];
            command.Execute($"INSERT INTO {table} VALUES({string.Join(',', Enumerable.Range(0, values.Length).Select(i => "$p" + i))})", values);
        });
    }
}
