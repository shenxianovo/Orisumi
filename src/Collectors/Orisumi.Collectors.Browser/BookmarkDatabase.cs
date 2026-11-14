using Microsoft.Data.Sqlite;

namespace Orisumi.Collectors.Browser;

internal static class BookmarkDatabase
{
    internal sealed record Node(string BookmarkId, string? ParentId, int? Position, string Title,
        string? Url, long? AddedAtUtcMs, long? LastUsedAtUtcMs);
    internal sealed record Usage(string BookmarkId, long LastUsedAtUtcMs);

    internal sealed record Observation(Guid ObservationId, Guid RunId, long RunStartedAtUtcMs,
        long RunSequence, long ObservedAtUtcMs, string Kind, Node[]? Nodes = null, Node? Node = null,
        string? BookmarkId = null, string? Title = null, string? Url = null, string? ParentId = null,
        int? Position = null, string[]? ChildIds = null, Usage[]? Usages = null);

    private sealed record State(string Id, string? Kind = null, long? Added = null, bool Present = true,
        string? Title = null, string? Url = null, string? Parent = null, int? Position = null, long? LastUsed = null)
    {
        internal static State From(Node node) => new(node.BookmarkId, node.Url is null ? "folder" : "bookmark",
            node.AddedAtUtcMs, true, node.Title, node.Url, node.ParentId, node.Position, node.LastUsedAtUtcMs);
    }

    internal static long Capture(string store, Guid profileId, Observation observation)
    {
        Validate(observation);
        try
        {
            using var connection = BrowserDatabase.OpenDatabase(store);
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            long Execute(string sql, params object?[] values)
            {
                command.CommandText = sql;
                command.Parameters.Clear();
                for (var i = 0; i < values.Length; i++) command.Parameters.AddWithValue($"$p{i}", values[i] ?? DBNull.Value);
                return command.ExecuteNonQuery();
            }
            object? Scalar(string sql, params object?[] values)
            {
                command.CommandText = sql;
                command.Parameters.Clear();
                for (var i = 0; i < values.Length; i++) command.Parameters.AddWithValue($"$p{i}", values[i] ?? DBNull.Value);
                return command.ExecuteScalar();
            }
            if (Scalar("SELECT 1 FROM bookmark_observations WHERE event_id=$p0", observation.ObservationId.ToString()) is not null)
                return 0;
            var profile = profileId.ToString();
            var run = observation.RunId.ToString();
            Execute("INSERT OR IGNORE INTO capture_runs VALUES ($p0,'chrome',$p1,'bookmarks',$p2,NULL)", run, profile, observation.RunStartedAtUtcMs);
            if (Scalar("SELECT 1 FROM capture_runs WHERE run_id=$p0 AND profile_id=$p1 AND started_at_utc_ms=$p2", run, profile, observation.RunStartedAtUtcMs) is null)
                throw new InvalidDataException("bookmark capture run does not match its origin");
            var lastSequence = Scalar("SELECT MAX(run_sequence) FROM bookmark_observations WHERE run_id=$p0", run);
            if (lastSequence is long previousSequence && previousSequence >= observation.RunSequence)
                throw new InvalidDataException("bookmark observations arrived out of order");
            var baseline = observation.Kind == "snapshot" && Scalar("SELECT 1 FROM bookmark_observations WHERE run_id=$p0 AND kind='snapshot' LIMIT 1", run) is null;
            command.CommandText = "SELECT bookmark_id,node_kind,added_at_utc_ms,present,title,url,parent_id,position,last_used_at_utc_ms FROM bookmark_state WHERE browser='chrome' AND profile_id=$p0";
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$p0", profile);
            var before = new Dictionary<string, State>(StringComparer.Ordinal);
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    string? Text(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
                    long? Number(int i) => reader.IsDBNull(i) ? null : reader.GetInt64(i);
                    var state = new State(reader.GetString(0), Text(1), Number(2), Number(3) == 1,
                        Text(4), Text(5), Text(6), (int?)Number(7), Number(8));
                    before.Add(state.Id, state);
                }
            }
            var after = new Dictionary<string, State>(before, StringComparer.Ordinal);
            Apply(after, observation);
            var changes = after.Values.Where(state => !before.TryGetValue(state.Id, out var old) || state != old || baseline && state.Present).ToArray();
            if (observation.Kind == "snapshot" && changes.Length == 0 && !baseline)
            {
                transaction.Commit();
                return 0;
            }
            Execute("INSERT INTO bookmark_observations(event_id,run_id,run_sequence,observed_at_utc_ms,kind) VALUES($p0,$p1,$p2,$p3,$p4)",
                observation.ObservationId.ToString(), run, observation.RunSequence, observation.ObservedAtUtcMs, observation.Kind);
            var observationId = (long)Scalar("SELECT last_insert_rowid()")!;
            long written = 0;
            foreach (var state in changes)
            {
                before.TryGetValue(state.Id, out var old);
                Execute("""
                    INSERT INTO bookmarks(browser,profile_id,bookmark_id,node_kind,added_at_utc_ms) VALUES('chrome',$p0,$p1,$p2,$p3)
                    ON CONFLICT(browser,profile_id,bookmark_id) DO UPDATE SET
                        node_kind=COALESCE(bookmarks.node_kind,excluded.node_kind),
                        added_at_utc_ms=COALESCE(bookmarks.added_at_utc_ms,excluded.added_at_utc_ms)
                    """, profile, state.Id, state.Kind, state.Added);
                var key = (long)Scalar("SELECT bookmark_key FROM bookmarks WHERE browser='chrome' AND profile_id=$p0 AND bookmark_id=$p1", profile, state.Id)!;
                void Append(string table, params object?[] values)
                {
                    var all = new object?[] { key, observationId }.Concat(values).ToArray();
                    written += Execute($"INSERT INTO {table} VALUES ({string.Join(',', Enumerable.Range(0, all.Length).Select(i => $"$p{i}"))})", all);
                }
                var initial = old is null || baseline && state.Present || old.Present == false && state.Present;
                if (initial || state.Present != old!.Present) Append("bookmark_presence_events", state.Present ? 1 : 0);
                if (!state.Present) continue;
                if (state.Title is not null && (initial || state.Title != old!.Title)) Append("bookmark_title_events", state.Title);
                if (state.Url is not null && (initial || state.Url != old!.Url)) Append("bookmark_url_events", state.Url);
                if ((initial && (state.Kind is not null || state.Parent is not null || state.Position is not null)) || (!initial && (state.Parent != old!.Parent || state.Position != old.Position))) Append("bookmark_location_events", state.Parent, state.Position);
                if (state.LastUsed is not null && (initial || state.LastUsed != old!.LastUsed)) Append("bookmark_last_used_observations", state.LastUsed);
            }
            transaction.Commit();
            return written;
        }
        catch (SqliteException error)
        {
            throw new IOException($"could not save bookmark facts: {error.Message}", error);
        }
    }

    private static void Apply(Dictionary<string, State> states, Observation observation)
    {
        State Observe(Node node)
        {
            var state = State.From(node);
            var known = states.GetValueOrDefault(node.BookmarkId);
            return state with { Added = known?.Added ?? state.Added, LastUsed = state.LastUsed ?? known?.LastUsed };
        }
        void Shift(string? parent, int? start, int delta, string? except = null)
        {
            if (start is null) return;
            foreach (var state in states.Values.ToArray())
                if (state.Present && state.Id != except && state.Parent == parent && state.Position >= start)
                    states[state.Id] = state with { Position = state.Position + delta };
        }
        var id = observation.BookmarkId ?? observation.Node?.BookmarkId;
        var previous = id is not null ? states.GetValueOrDefault(id) : null;
        switch (observation.Kind)
        {
            case "last_used":
                foreach (var usage in observation.Usages!)
                    if (states.TryGetValue(usage.BookmarkId, out var used) && used.Present)
                        states[usage.BookmarkId] = used with { LastUsed = usage.LastUsedAtUtcMs };
                break;
            case "snapshot":
                var incoming = observation.Nodes!.ToDictionary(node => node.BookmarkId, Observe, StringComparer.Ordinal);
                foreach (var state in states.Values.ToArray())
                    if (!incoming.ContainsKey(state.Id)) states[state.Id] = state with { Present = false };
                foreach (var state in incoming.Values) states[state.Id] = state;
                break;
            case "created":
                var created = Observe(observation.Node!);
                if (previous?.Present != true) Shift(created.Parent, created.Position, 1, created.Id);
                states[created.Id] = created;
                break;
            case "changed":
                var changed = previous ?? new State(id!);
                states[id!] = changed with { Present = true, Title = observation.Title,
                    Url = observation.Url ?? changed.Url, Kind = observation.Url is null ? changed.Kind : "bookmark" };
                break;
            case "moved":
                var moved = previous ?? new State(id!);
                if (moved.Present) Shift(moved.Parent, moved.Position + 1, -1, id);
                Shift(observation.ParentId, observation.Position, 1, id);
                states[id!] = moved with { Present = true, Parent = observation.ParentId, Position = observation.Position };
                break;
            case "removed":
                var removed = new HashSet<string>(StringComparer.Ordinal) { id! };
                bool more;
                do
                {
                    more = false;
                    foreach (var state in states.Values)
                        if (state.Present && state.Parent is not null && removed.Contains(state.Parent)) more |= removed.Add(state.Id);
                } while (more);
                if (previous?.Present == true) Shift(previous.Parent, previous.Position + 1, -1, id);
                foreach (var removedId in removed)
                    states[removedId] = (states.GetValueOrDefault(removedId) ?? new State(removedId)) with { Present = false };
                break;
            case "reordered":
                for (var index = 0; index < observation.ChildIds!.Length; index++)
                {
                    var child = observation.ChildIds[index];
                    states[child] = (states.GetValueOrDefault(child) ?? new State(child)) with { Present = true, Parent = id, Position = index };
                }
                break;
        }
    }

    private static void Validate(Observation value)
    {
        static bool ValidNode(Node? node) => node is { BookmarkId.Length: > 0, Title: not null } && node.Position is not < 0;
        if (value.ObservationId == Guid.Empty || value.RunId == Guid.Empty || value.RunSequence <= 0 ||
            value.RunStartedAtUtcMs <= 0 || value.ObservedAtUtcMs < value.RunStartedAtUtcMs)
            throw new InvalidDataException("invalid bookmark observation identity or time");
        var valid = value.Kind switch
        {
            "snapshot" => value.Nodes is { } nodes && nodes.All(ValidNode) && nodes.Select(node => node.BookmarkId).Distinct().Count() == nodes.Length,
            "last_used" => value.Usages is { } usages && usages.All(usage => !string.IsNullOrEmpty(usage.BookmarkId) && usage.LastUsedAtUtcMs > 0)
                && usages.Select(usage => usage.BookmarkId).Distinct().Count() == usages.Length,
            "created" => ValidNode(value.Node),
            "changed" => !string.IsNullOrEmpty(value.BookmarkId) && value.Title is not null,
            "moved" => !string.IsNullOrEmpty(value.BookmarkId) && !string.IsNullOrEmpty(value.ParentId) && value.Position is >= 0,
            "removed" => !string.IsNullOrEmpty(value.BookmarkId),
            "reordered" => !string.IsNullOrEmpty(value.BookmarkId) && value.ChildIds is { } ids && ids.All(id => !string.IsNullOrEmpty(id)) && ids.Distinct().Count() == ids.Length,
            _ => false,
        };
        if (!valid) throw new InvalidDataException("invalid bookmark observation");
    }
}
