using System.Text.Json;
using Orisumi.Capture.Storage;
using Observation = Orisumi.Collectors.Browser.BrowserDatabase.Observation;

namespace Orisumi.Collectors.Browser;

internal static class BookmarkDatabase
{
    private sealed record State(string Id, string? Kind = null, long? Added = null, bool? Present = null,
        string? Title = null, string? Url = null, string? Parent = null, long? LastUsed = null);

    internal static void Capture(Observation observation)
    {
        var command = observation.Command;
        var profile = observation.Profile;
        var data = observation.Data;
        var id = data.Text("id");
        if (!observation.IsSnapshot && string.IsNullOrEmpty(id))
            throw new InvalidDataException("missing bookmark ID");

        command.CommandText = """
            SELECT bookmark_id,node_kind,added_at_utc_ms,present,title,url,parent_id,last_used_at_utc_ms
            FROM bookmark_state WHERE browser='chrome' AND profile_id=$p0
            """;
        if (!observation.IsSnapshot && observation.Kind != "removed") command.CommandText += " AND bookmark_id=$p1";
        command.Parameters.Clear();
        command.Parameters.AddWithValue("$p0", profile);
        if (command.CommandText.EndsWith("$p1", StringComparison.Ordinal)) command.Parameters.AddWithValue("$p1", id!);
        var before = new Dictionary<string, State>(StringComparer.Ordinal);
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                string? Text(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
                long? Number(int i) => reader.IsDBNull(i) ? null : reader.GetInt64(i);
                var state = new State(reader.GetString(0), Text(1), Number(2), Number(3) is long present ? present == 1 : null,
                    Text(4), Text(5), Text(6), Number(7));
                before.Add(state.Id, state);
            }
        }
        var after = new Dictionary<string, State>(StringComparer.Ordinal);
        State Observe(JsonElement node)
        {
            var nodeId = node.Text("id") ?? throw new InvalidDataException("missing bookmark node ID");
            var parent = node.Text("parentId");
            var url = node.Text("url");
            var known = before.GetValueOrDefault(nodeId);
            return new State(nodeId, known?.Kind ?? (parent is null ? "root" : url is null ? "folder" : "bookmark"),
                known?.Added ?? Milliseconds(node, "dateAdded"), true, node.Text("title"), url, parent,
                Milliseconds(node, "dateLastUsed") ?? known?.LastUsed);
        }
        switch (observation.Kind)
        {
            case "snapshot":
                foreach (var node in data.Elements())
                {
                    var state = Observe(node);
                    after.Add(state.Id, state);
                }
                foreach (var state in before.Values)
                    if (state.Present == true) after.TryAdd(state.Id, state with { Present = false });
                break;
            case "created":
                after.Add(id!, Observe(data.Field("node")));
                break;
            case "changed":
                var changed = before.GetValueOrDefault(id!) ?? new State(id!);
                var fields = data.Field("change");
                after.Add(id!, changed with { Present = true, Title = fields.Text("title"),
                    Url = fields.Text("url") ?? changed.Url, Kind = changed.Kind ?? (fields.Has("url") ? "bookmark" : null) });
                break;
            case "moved":
                var moved = before.GetValueOrDefault(id!) ?? new State(id!);
                after.Add(id!, moved with { Present = true, Parent = data.Text("parentId") ??
                    throw new InvalidDataException("missing bookmark parent") });
                break;
            case "removed":
                var children = before.Values.Where(state => state.Present == true && state.Parent is not null).ToLookup(state => state.Parent);
                var pending = new Stack<string>();
                pending.Push(id!);
                while (pending.TryPop(out var removed))
                {
                    var state = before.GetValueOrDefault(removed) ?? new State(removed);
                    if (!after.TryAdd(removed, state with { Present = false })) continue;
                    foreach (var child in children[removed]) pending.Push(child.Id);
                }
                break;
            default: throw new InvalidDataException("invalid bookmark event");
        }
        bool StructureDiffers(State state) => before.TryGetValue(state.Id, out var last)
            ? state.Present != last.Present || state.Title != last.Title || state.Url != last.Url || state.Parent != last.Parent
            : state.Present == true;
        var baseline = observation.IsSnapshot && observation.Snapshot(after.Values.Any(StructureDiffers));
        var changes = after.Values.Where(state => state != before.GetValueOrDefault(state.Id) || baseline && state.Present == true).ToArray();
        if (changes.Length == 0) return;

        var observed = (long)command.Scalar("""
            INSERT INTO bookmark_observations(event_id,run_id,run_sequence,observed_at_utc_ms,kind)
            VALUES($p0,$p1,$p2,$p3,$p4) RETURNING observation_id
            """, observation.EventId, observation.Run, observation.Sequence, observation.Time, observation.Kind)!;
        long Key(string bookmark, string? kind = null, long? added = null) => (long)command.Scalar("""
            INSERT INTO bookmarks(browser,profile_id,bookmark_id,node_kind,added_at_utc_ms) VALUES('chrome',$p0,$p1,$p2,$p3)
            ON CONFLICT(browser,profile_id,bookmark_id) DO UPDATE SET
                node_kind=COALESCE(bookmarks.node_kind,excluded.node_kind),
                added_at_utc_ms=COALESCE(bookmarks.added_at_utc_ms,excluded.added_at_utc_ms)
            RETURNING bookmark_key
            """, profile, bookmark, kind, added)!;
        foreach (var state in changes)
        {
            var old = before.GetValueOrDefault(state.Id);
            var key = Key(state.Id, state.Kind, state.Added);
            var initial = state.Present == true && (baseline || old?.Present != true);
            if (initial || state.Present != old?.Present)
                command.Execute("INSERT INTO bookmark_presence_events VALUES($p0,$p1,$p2)", key, observed, state.Present);
            if (state.Present != true) continue;
            if (state.Title is not null && (initial || state.Title != old?.Title))
                command.Execute("INSERT INTO bookmark_title_events VALUES($p0,$p1,$p2)", key, observed, state.Title);
            if (state.Url is not null && (initial || state.Url != old?.Url))
                command.Execute("INSERT INTO bookmark_url_events VALUES($p0,$p1,$p2)", key, observed, state.Url);
            if (state.Parent is not null && (initial || state.Parent != old?.Parent))
                command.Execute("INSERT INTO bookmark_location_events VALUES($p0,$p1,$p2)", key, observed, Key(state.Parent));
            if (state.LastUsed is not null && (initial || state.LastUsed != old?.LastUsed))
                command.Execute("INSERT INTO bookmark_last_used_observations VALUES($p0,$p1,$p2)", key, observed, state.LastUsed);
        }
    }

    private static long? Milliseconds(JsonElement node, string name) =>
        node.Field(name) is { ValueKind: JsonValueKind.Number } value ? (long)value.GetDouble() : null;
}
