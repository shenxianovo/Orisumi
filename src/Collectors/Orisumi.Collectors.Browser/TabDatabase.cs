using System.Text.Json;
using Microsoft.Data.Sqlite;
using Orisumi.Collectors.Storage;
using Observation = Orisumi.Collectors.Browser.BrowserDatabase.Observation;

namespace Orisumi.Collectors.Browser;

internal static class TabDatabase
{
    private sealed record Tab(long Key = 0, bool? Present = null, bool? HasWindow = null,
        int? Window = null, string? Url = null, string? Title = null);
    private readonly record struct Focus(bool? HasTarget, int? Id = null);
    private sealed record Window(long Key = 0, bool? Present = null, Focus? Active = null);

    internal static void Capture(Observation observation)
    {
        var command = observation.Command;
        var profile = observation.Profile;
        var session = observation.Session;
        var data = observation.Data;
        var snapshot = observation.IsSnapshot;
        var tabs = new Dictionary<int, (Tab Before, Tab After)>();
        var windows = new Dictionary<int, (Window Before, Window After)>();
        const string tabQuery = """
            SELECT tab_id,tab_key,is_present,has_window,w.window_id,url,title FROM tab_state t
            LEFT JOIN browser_windows w USING(window_key) WHERE t.profile_id=$p0 AND t.browser_session=$p1
            """;
        const string windowQuery = """
            SELECT window_id,w.window_key,
              (SELECT is_present FROM window_presence_events f WHERE f.window_key=w.window_key ORDER BY observation_id DESC LIMIT 1),
              a.observation_id,a.has_tab,t.tab_id
            FROM browser_windows w
            LEFT JOIN window_active_tab_events a ON a.window_key=w.window_key AND a.observation_id=(
                SELECT MAX(observation_id) FROM window_active_tab_events WHERE window_key=w.window_key)
            LEFT JOIN tabs t USING(tab_key)
            WHERE w.profile_id=$p0 AND w.browser_session=$p1
            """;
        void ReadTabs(string condition, params object?[] values)
        {
            Bind(command, tabQuery + " " + condition, profile, session, values);
            using var row = command.ExecuteReader();
            while (row.Read())
            {
                var tab = new Tab(row.GetInt64(1), Flag(row, 2), Flag(row, 3), Number(row, 4), Text(row, 5), Text(row, 6));
                tabs.TryAdd(row.GetInt32(0), (tab, tab));
            }
        }
        void ReadWindows(string condition, params object?[] values)
        {
            Bind(command, windowQuery + " " + condition, profile, session, values);
            using var row = command.ExecuteReader();
            while (row.Read())
            {
                Focus? active = row.IsDBNull(3) ? null : new Focus(Flag(row, 4), Number(row, 5));
                var window = new Window(row.GetInt64(1), Flag(row, 2), active);
                windows.TryAdd(row.GetInt32(0), (window, window));
            }
        }
        Tab GetTab(int id)
        {
            if (!tabs.ContainsKey(id))
            {
                ReadTabs("AND t.tab_id=$p2", id);
                tabs.TryAdd(id, (new(), new()));
            }
            return tabs[id].After;
        }
        Window GetWindow(int id)
        {
            if (!windows.ContainsKey(id))
            {
                ReadWindows("AND window_id=$p2", id);
                windows.TryAdd(id, (new(), new()));
            }
            return windows[id].After;
        }
        void TabChanged(int id, Tab state) => tabs[id] = (tabs[id].Before, state);
        void WindowChanged(int id, Window state) => windows[id] = (windows[id].Before, state);
        Focus? LastWindowFocus()
        {
            Bind(command, """
                SELECT f.has_window,w.window_id FROM browser_window_focus_events f LEFT JOIN browser_windows w USING(window_key)
                JOIN tab_observations o USING(observation_id) JOIN browser_runs r USING(run_id)
                WHERE r.profile_id=$p0 AND o.browser_session=$p1 ORDER BY observation_id DESC LIMIT 1
                """, profile, session, []);
            using var reader = command.ExecuteReader();
            return reader.Read() ? new(Flag(reader, 0), Number(reader, 1)) : null;
        }
        var beforeWindowFocus = LastWindowFocus();
        var windowFocus = beforeWindowFocus;

        void ObserveTab(JsonElement value)
        {
            var id = Id(value, "id");
            var windowId = Id(value, "windowId");
            var tab = GetTab(id);
            TabChanged(id, tab with { Present = true, HasWindow = true, Window = windowId,
                Url = value.Text("url"), Title = value.Text("title") });
            var window = GetWindow(windowId);
            var active = value.Flag("active") switch
            {
                true => new Focus(true, id),
                false when window.Active?.Id == id => new Focus(null),
                null when snapshot && window.Active?.Id is null => new Focus(null),
                _ => window.Active,
            };
            WindowChanged(windowId, window with { Present = true, Active = active });
        }
        void Depart(int id, bool removed)
        {
            var tab = GetTab(id);
            if (tab.Window is { } windowId)
            {
                var window = GetWindow(windowId);
                if (window.Active?.Id == id) WindowChanged(windowId, window with { Active = new Focus(null) });
            }
            TabChanged(id, removed ? tab with { Present = false } : tab with { Present = true, HasWindow = false, Window = null });
        }

        switch (observation.Kind)
        {
            case "snapshot":
                ReadTabs("AND t.is_present=1");
                ReadWindows("AND (SELECT is_present FROM window_presence_events f WHERE f.window_key=w.window_key ORDER BY observation_id DESC LIMIT 1)=1");
                foreach (var (id, pair) in tabs.ToArray()) TabChanged(id, pair.After with { Present = false });
                foreach (var (id, pair) in windows.ToArray()) WindowChanged(id, pair.After with { Present = false, Active = new Focus(false) });
                foreach (var window in data.Field("windows").Elements())
                {
                    var id = Id(window, "id");
                    WindowChanged(id, GetWindow(id) with { Present = true, Active = new Focus(false) });
                }
                foreach (var tab in data.Field("tabs").Elements()) ObserveTab(tab);
                windowFocus = ReadFocus(data, "focusedWindowId");
                break;
            case "window_created":
                var created = Id(data, "id");
                WindowChanged(created, GetWindow(created) with { Present = true });
                if (data.Flag("focused") == true) windowFocus = new(true, created);
                break;
            case "window_removed":
                var removed = Id(data, "windowId");
                WindowChanged(removed, GetWindow(removed) with { Present = false, Active = new Focus(false) });
                ReadTabs("AND t.is_present=1 AND w.window_id=$p2", removed);
                foreach (var id in tabs.Keys.ToArray()) Depart(id, removed: true);
                if (windowFocus?.Id == removed) windowFocus = new Focus(null);
                break;
            case "window_focus_changed":
                windowFocus = ReadFocus(data, "windowId");
                break;
            case "tab_created":
                ObserveTab(data);
                break;
            case "tab_removed":
                Depart(Id(data, "tabId"), removed: true);
                break;
            case "tab_detached":
                Depart(Id(data, "tabId"), removed: false);
                break;
            case "tab_attached":
                var attached = Id(data, "tabId");
                var target = Id(data, "newWindowId");
                WindowChanged(target, GetWindow(target) with { Present = true });
                TabChanged(attached, GetTab(attached) with { Present = true, HasWindow = true, Window = target });
                break;
            case "tab_updated":
                var updated = Id(data, "tabId");
                var previous = GetTab(updated);
                var fields = data.Field("change");
                TabChanged(updated, previous with
                {
                    Present = true,
                    Url = fields.Has("url") ? fields.Text("url") : previous.Url,
                    Title = fields.Has("title") ? fields.Text("title") : previous.Title,
                });
                break;
            case "tab_activated":
                var active = Id(data, "tabId");
                var owner = Id(data, "windowId");
                TabChanged(active, GetTab(active) with { Present = true, HasWindow = true, Window = owner });
                WindowChanged(owner, GetWindow(owner) with { Present = true, Active = new Focus(true, active) });
                break;
            case "tab_replaced":
                var oldId = Id(data, "removedTabId");
                var replacement = Id(data, "addedTabId");
                var old = GetTab(oldId);
                var wasActive = old.Window is { } oldWindow && GetWindow(oldWindow).Active?.Id == oldId;
                Depart(oldId, removed: true);
                if (data.Field("tab").ValueKind == JsonValueKind.Object) ObserveTab(data.Field("tab"));
                else if (old.Present == true)
                {
                    TabChanged(replacement, GetTab(replacement) with { Present = true, Window = old.Window, HasWindow = old.HasWindow });
                    if (wasActive && old.Window is { } windowId)
                        WindowChanged(windowId, GetWindow(windowId) with { Active = new Focus(true, replacement) });
                }
                break;
            default: throw new InvalidDataException("invalid tab event");
        }

        var differs = tabs.Values.Any(pair => pair.Before != pair.After) ||
            windows.Values.Any(pair => pair.Before != pair.After) || beforeWindowFocus != windowFocus;
        var baseline = snapshot && observation.Snapshot(differs);
        if (!differs && !baseline) return;

        var observed = (long)command.Scalar("""
            INSERT INTO tab_observations(event_id,browser_session,run_id,run_sequence,observed_at_utc_ms,event_kind)
            VALUES($p0,$p1,$p2,$p3,$p4,$p5) RETURNING observation_id
            """, observation.EventId, session, observation.Run, observation.Sequence, observation.Time, observation.Kind)!;
        long? WindowKey(int? id) => id is null ? null : (long)command.Scalar("""
            INSERT INTO browser_windows(profile_id,browser_session,window_id) VALUES($p0,$p1,$p2)
            ON CONFLICT(profile_id,browser_session,window_id) DO UPDATE SET window_id=excluded.window_id RETURNING window_key
            """, profile, session, id)!;
        long? TabKey(int? id) => id is null ? null : (long)command.Scalar("""
            INSERT INTO tabs(profile_id,browser_session,tab_id) VALUES($p0,$p1,$p2)
            ON CONFLICT(profile_id,browser_session,tab_id) DO UPDATE SET tab_id=excluded.tab_id RETURNING tab_key
            """, profile, session, id)!;
        foreach (var (id, (before, after)) in windows)
        {
            var initial = baseline && after.Present == true;
            if (!initial && before == after) continue;
            var key = after.Key == 0 ? WindowKey(id) : after.Key;
            if (initial || before.Present != after.Present)
                command.Execute("INSERT INTO window_presence_events VALUES($p0,$p1,$p2)", observed, key, after.Present);
            if (after.Active is { } activeTab && (initial || before.Active != after.Active))
                command.Execute("INSERT INTO window_active_tab_events(observation_id,window_key,has_tab,tab_key) VALUES($p0,$p1,$p2,$p3)",
                    observed, key, activeTab.HasTarget, TabKey(activeTab.Id));
        }
        foreach (var (id, (before, after)) in tabs)
        {
            var initial = after.Present == true && (baseline || before.Present != true);
            if (!initial && before == after) continue;
            var key = after.Key == 0 ? TabKey(id) : after.Key;
            if (initial || before.Present != after.Present)
                command.Execute("INSERT INTO tab_presence_events VALUES($p0,$p1,$p2)", observed, key, after.Present);
            if (after.Present != true) continue;
            if (after.HasWindow is not null && (initial || before.HasWindow != after.HasWindow || before.Window != after.Window))
                command.Execute("INSERT INTO tab_window_observations VALUES($p0,$p1,$p2,$p3)", observed, key, after.HasWindow, WindowKey(after.Window));
            if (initial || before.Url != after.Url)
                command.Execute("INSERT INTO tab_url_observations VALUES($p0,$p1,$p2)", observed, key, after.Url);
            if (initial || before.Title != after.Title)
                command.Execute("INSERT INTO tab_title_observations VALUES($p0,$p1,$p2)", observed, key, after.Title);
        }
        if (windowFocus is { } focus && (baseline || beforeWindowFocus != windowFocus))
            command.Execute("INSERT INTO browser_window_focus_events(observation_id,has_window,window_key) VALUES($p0,$p1,$p2)",
                observed, focus.HasTarget, WindowKey(focus.Id));
    }

    private static Focus ReadFocus(JsonElement value, string name) => value.Field(name).ValueKind switch
    {
        JsonValueKind.Null => new(false),
        JsonValueKind.Undefined => new(null),
        _ => new(true, Id(value, name)),
    };
    private static int Id(JsonElement value, string name) => value.Integer(name) is >= 0 and var id ? id :
        throw new InvalidDataException($"missing Chrome {name}");
    private static int? Number(SqliteDataReader row, int i) => row.IsDBNull(i) ? null : row.GetInt32(i);
    private static bool? Flag(SqliteDataReader row, int i) => row.IsDBNull(i) ? null : row.GetBoolean(i);
    private static string? Text(SqliteDataReader row, int i) => row.IsDBNull(i) ? null : row.GetString(i);
    private static void Bind(SqliteCommand command, string sql, string profile, string session, object?[] values)
    {
        command.CommandText = sql;
        command.Parameters.Clear();
        command.Parameters.AddWithValue("$p0", profile);
        command.Parameters.AddWithValue("$p1", session);
        for (var i = 0; i < values.Length; i++) command.Parameters.AddWithValue($"$p{i + 2}", values[i] ?? DBNull.Value);
    }
}
