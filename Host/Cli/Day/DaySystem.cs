namespace Orisumi.Cli.Day;

internal sealed record DayApp(string? Name, string? Id);
internal sealed record DayWindow(DayApp App, string? Title);

internal sealed class DaySystem(DayDatabase database, DayTitles titles, string source)
{
    private sealed record Foreground(string? Process, DayApp App);
    private sealed record Focus(long? HasWindow, string? Id, string? Process);
    private sealed record WindowTitle(Focus Focus, string? Text);
    private static readonly DayApp NoApp = new(null, null);
    private static readonly Comparer<DayChange<bool>> ChannelOrder = Comparer<DayChange<bool>>.Create(
        (left, right) => left.Position.Sequence.CompareTo(right.Position.Sequence));

    internal void ReadInto(DayDevice device)
    {
        var day = database.Day;
        var runs = database.Runs();
        var channels = database.Changes("capture_channel_events", "channel, available",
            row => (Channel: row.GetString(3), Available: row.GetBoolean(4)), "channel", "available")
            .ToLookup(change => (change.Position.Run, change.Value.Channel),
                change => new DayChange<bool>(change.Position, change.Value.Available));
        var availability = channels.ToDictionary(group => group.Key, group => group.OrderBy(row => row.Position.Sequence).ToArray());
        var hasChannels = database.Has("capture_channel_events", "run_id", "run_sequence", "observed_at_utc_ms", "channel", "available");

        Func<DayPosition, long?> Channel(string name) => position =>
        {
            if (!hasChannels) return null;
            if (!availability.TryGetValue((position.Run, name), out var events)) return long.MaxValue;
            var index = Array.BinarySearch(events, new DayChange<bool>(position, false), ChannelOrder);
            var next = index >= 0 ? index + 1 : ~index;
            if (next > 0 && !events[next - 1].Value) return null;
            for (var i = next; i < events.Length; i++)
                if (!events[i].Value) return events[i].Position.At;
            return long.MaxValue;
        };

        foreach (var run in runs.Values)
            device.Presence.Add(new(Math.Max(day.From, run.Start), Math.Min(day.To, run.End), "unknown"));
        AddPresence(database.Changes("system_sleep_events", "event_kind", row => row.GetString(3), "event_kind"), "sleep", "asleep");
        AddPresence(database.Changes("user_session_events", "event_kind", row => DayDatabase.Text(row, 3), "event_kind"), "session_inactive", "session_inactive");
        AddPresence(database.Changes("screen_lock_events", "is_locked", row => DayDatabase.Number(row, 3), "is_locked"), 1L, "locked");
        device.Presence.AddRange(DayTimeline.Values(day, runs,
            database.Changes("afk_events", "state", row => DayDatabase.Text(row, 3) ?? "unknown", "state"), Channel("afk")));

        foreach (var zone in DayTimeline.Values(day, runs,
            database.Changes("time_zone_events", "time_zone_id", row => DayDatabase.Text(row, 3), "time_zone_id"), Channel("time_zone")))
            if (zone.Value is { } id) device.TimeZones.Add(id);

        var foreground = ReadForeground().ToLookup(change => change.Position.Run);
        var focus = ReadFocus().ToLookup(change => change.Position.Run);
        var titleChanges = database.Changes("window_title_observations", "focus_id, title",
                row => (Id: row.GetString(3), Text: DayDatabase.Text(row, 4)), "focus_id", "title")
            .ToLookup(change => (change.Position.Run, change.Value.Id));
        var applications = Channel("applications");
        var windows = Channel("windows");
        var windowTitle = Channel("window_title");
        foreach (var run in runs.Keys)
        {
            var apps = DayTimeline.Overlay(DayTimeline.Values(day, runs, foreground[run], applications));
            var focused = DayTimeline.Overlay(DayTimeline.Values(day, runs, focus[run], windows));
            var windowTitles = new List<DaySpan<WindowTitle>>();
            foreach (var group in focused.GroupBy(span => span.Value))
            {
                if (group.Key.Id is not { } id)
                {
                    windowTitles.AddRange(group.Select(span => new DaySpan<WindowTitle>(span.Start, span.End,
                        new(span.Value, span.Value.HasWindow == 0 ? "" : null))));
                    continue;
                }
                var changes = DayTimeline.Overlay(DayTimeline.Values(day, runs, titleChanges[(run, id)], windowTitle));
                foreach (var (start, end, window, title) in DayTimeline.Align(group.ToArray(), changes))
                    if (window is not null) windowTitles.Add(new(start, end, new(window.Value, title?.Value.Text)));
            }
            foreach (var (start, end, app, window) in DayTimeline.Align(apps, windowTitles.OrderBy(span => span.Start).ToArray()))
            {
                if (app is null) continue;
                var value = app.Value.App;
                string? title = null;
                if (app.Value.Process is { } process && window is not null)
                {
                    if (window.Value.Focus.HasWindow == 0) title = "";
                    else if (process == window.Value.Focus.Process)
                        title = titles.Apply("window_title", window.Value.Text, value.Id);
                }
                DayTimeline.Append(device.Windows, new(start, end, new(value, title)));
            }
        }

        void AddPresence<T>(IEnumerable<DayChange<T>> changes, T match, string state)
        {
            foreach (var span in DayTimeline.Values(day, runs, changes))
                if (EqualityComparer<T>.Default.Equals(span.Value, match)) device.Presence.Add(new(span.Start, span.End, state));
        }
    }

    private IEnumerable<DayChange<Foreground>> ReadForeground()
    {
        const string table = "foreground_app_events";
        if (!database.Has(table, "run_id", "run_sequence", "observed_at_utc_ms", "has_app")) return [];
        var hasProcess = database.Has(table, "process_instance_id") && database.Has("process_instances", "process_instance_id");
        var app = database.Column("process_instances", "app_name", "p.");
        var windows = source == "windows";
        var identifier = database.Column("process_instances", windows ? "aumid" : "bundle_id", "p.");
        var path = windows ? database.Column("process_instances", "executable_path", "p.") : "NULL";
        var columns = hasProcess ? $"e.process_instance_id, {app}, {identifier}, {path}" : "NULL, NULL, NULL, NULL";
        var join = hasProcess ? "LEFT JOIN process_instances p USING(process_instance_id)" : "";
        return database.Read($"""
            SELECT e.run_id, e.run_sequence, e.observed_at_utc_ms, e.has_app, {columns}
            FROM foreground_app_events e {join}
            """, row =>
            {
                var foreground = new Foreground(null, NoApp);
                if (DayDatabase.Number(row, 3) == 1)
                {
                    var id = DayDatabase.Text(row, 6);
                    // Windows databases are also read on macOS, where backslash is not a path separator.
                    if (windows && string.IsNullOrEmpty(id))
                        id = DayDatabase.Text(row, 7) is { Length: > 0 } exe
                            ? exe[(exe.LastIndexOfAny(['/', '\\']) + 1)..].ToLowerInvariant() : null;
                    foreground = new(DayDatabase.Text(row, 4), new(DayDatabase.Text(row, 5), id));
                }
                return new DayChange<Foreground>(DayDatabase.Position(row), foreground);
            });
    }

    private IEnumerable<DayChange<Focus>> ReadFocus()
    {
        const string table = "focused_window_events";
        if (!database.Has(table, "run_id", "run_sequence", "observed_at_utc_ms", "has_window")) return [];
        var id = database.Column(table, "focus_id", "e.");
        var hasProcess = id != "NULL" && database.Has("window_focus_instances", "focus_id", "process_instance_id");
        var process = hasProcess ? "f.process_instance_id" : "NULL";
        var join = hasProcess ? "LEFT JOIN window_focus_instances f USING(focus_id)" : "";
        return database.Read($"""
            SELECT e.run_id, e.run_sequence, e.observed_at_utc_ms, e.has_window, {id}, {process}
            FROM focused_window_events e {join}
            """, row => new DayChange<Focus>(DayDatabase.Position(row),
                new(DayDatabase.Number(row, 3), DayDatabase.Text(row, 4), DayDatabase.Text(row, 5))));
    }
}
