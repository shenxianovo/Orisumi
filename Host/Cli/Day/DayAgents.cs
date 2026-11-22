namespace Orisumi.Cli.Day;

internal sealed record DaySession(string Source, string Id, string? Title, string? Cwd, string? Branch,
    long Start, long End, long Prompts, int Subagents);

internal sealed class DayAgents
{
    private sealed class Session
    {
        internal string? Parent, Title, Cwd, Branch;
        internal long Start = long.MaxValue, End = long.MinValue, Prompts;
        internal long CwdAt = long.MinValue, BranchAt = long.MinValue;
    }

    private readonly Dictionary<(string Source, string Id), Session> sessions = [];

    internal void ReadClaude(DayDatabase database)
    {
        ReadMetadata(database, "claude", "sessions", "session_id", "parent_session_id", "current_sessions", ["custom_title", "ai_title"]);

        const string table = "transcript_events";
        if (!database.Has(table, "session_id", "created_at_utc_ms")) return;
        var cwd = database.Column(table, "cwd");
        var branch = database.Column(table, "git_branch");
        var prompt = database.Has(table, "row_type", "prompt_source") ? "row_type='user' AND prompt_source IS NOT NULL" : "0";
        var sequence = database.Has(table, "observation_id") ? ", observation_id" : "";
        ReadEvents(database, "claude", $"""
            SELECT session_id, created_at_utc_ms AS at, ({prompt}), {cwd}, {branch}
            FROM transcript_events WHERE at >= $from AND at < $to ORDER BY at{sequence}
            """);
    }

    internal void ReadCodex(DayDatabase database)
    {
        ReadMetadata(database, "codex", "threads", "thread_id", "parent_thread_id", "current_threads", ["name"]);

        const string table = "rollout_events";
        if (!database.Has(table, "thread_id")) return;
        var completed = database.Column(table, "completed_at_utc_ms");
        var recorded = database.Column(table, "recorded_at_utc_ms");
        var started = database.Column(table, "started_at_utc_ms");
        var item = database.Column(table, "item_type");
        ReadEvents(database, "codex", $"""
            SELECT thread_id, COALESCE({completed},{recorded},{started}) AS at, {item}='UserMessage', NULL, NULL
            FROM rollout_events WHERE at >= $from AND at < $to
            """);
    }

    internal IEnumerable<DaySession> Fold()
    {
        var roots = new Dictionary<(string Source, string Id), (long Start, long End, int Children)>();
        foreach (var (key, session) in sessions)
        {
            if (session.Start == long.MaxValue) continue;
            var root = (key.Source, Id: Root(key));
            var child = root != key ? 1 : 0;
            roots[root] = roots.TryGetValue(root, out var span)
                ? (Math.Min(span.Start, session.Start), Math.Max(span.End, session.End), span.Children + child)
                : (session.Start, session.End, child);
        }

        foreach (var (key, span) in roots)
        {
            var session = sessions.GetValueOrDefault(key);
            yield return new(key.Source, key.Id, session?.Title, session?.Cwd, session?.Branch,
                span.Start, span.End, session?.Prompts ?? 0, span.Children);
        }

        string Root((string Source, string Id) key)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var id = key.Id;
            while (sessions.TryGetValue((key.Source, id), out var session) && session.Parent is { Length: > 0 } parent)
            {
                if (!visited.Add(id)) return key.Id;
                id = parent;
            }
            return id;
        }
    }

    private void ReadMetadata(DayDatabase database, string source, string table, string id, string parent,
        string view, string[] titleColumns)
    {
        if (!database.Has(table, id)) return;
        var parentColumn = database.Column(table, parent, "s.");
        var cwd = source == "codex" ? database.Column(table, "cwd", "s.") : "NULL";
        var branch = source == "codex" ? database.Column(table, "git_branch", "s.") : "NULL";
        var hasView = database.Has(view, id);
        var columns = titleColumns.Select(column => hasView ? database.Column(view, column, "t.") : "NULL").ToArray();
        var title = source == "claude" ? $"COALESCE({columns[0]},{columns[1]})" : $"NULLIF({columns[0]},'')";
        var join = hasView ? $"LEFT JOIN {view} t USING({id})" : "";
        foreach (var row in database.Read($"""
            SELECT s.{id}, {parentColumn}, {title}, {cwd}, {branch} FROM {table} s {join}
            """, row => (Id: row.GetString(0), Parent: DayDatabase.Text(row, 1), Title: DayDatabase.Text(row, 2),
                Cwd: DayDatabase.Text(row, 3), Branch: DayDatabase.Text(row, 4))))
        {
            var session = Get(source, row.Id);
            session.Parent = row.Parent ?? session.Parent;
            session.Title = row.Title ?? session.Title;
            session.Cwd = row.Cwd ?? session.Cwd;
            session.Branch = row.Branch ?? session.Branch;
        }
    }

    private void ReadEvents(DayDatabase database, string source, string sql)
    {
        foreach (var row in database.Read(sql, row => (Id: row.GetString(0), At: row.GetInt64(1),
            Prompt: DayDatabase.Number(row, 2) == 1, Cwd: DayDatabase.Text(row, 3), Branch: DayDatabase.Text(row, 4)), bounded: true))
        {
            var session = Get(source, row.Id);
            session.Start = Math.Min(session.Start, row.At);
            session.End = Math.Max(session.End, row.At);
            if (row.Prompt) session.Prompts++;
            if (row.Cwd is not null && row.At >= session.CwdAt) (session.Cwd, session.CwdAt) = (row.Cwd, row.At);
            if (row.Branch is not null && row.At >= session.BranchAt) (session.Branch, session.BranchAt) = (row.Branch, row.At);
        }
    }

    private Session Get(string source, string id)
    {
        if (!sessions.TryGetValue((source, id), out var session))
            sessions.Add((source, id), session = new());
        return session;
    }
}
