namespace Orisumi.Cli.Day;

internal sealed record DayTab(string? Url, string? Title);
internal sealed record DayBrowserFocus(long FocusedAt, DayTab? Tab);
internal sealed record DayVisit(long At, string? Url, string? Title, string? Transition);
internal sealed record DayDownload(long Start, string? Url, string? Path, string? State);

internal sealed class DayBrowser(DayDatabase database, DayTitles titles)
{
    internal void ReadInto(DayDevice device)
    {
        device.Tabs.AddRange(ReadTabs());
        device.Visits.AddRange(ReadVisits());
        device.Downloads.AddRange(ReadDownloads());
    }

    private IEnumerable<DaySpan<DayBrowserFocus>> ReadTabs()
    {
        var runs = database.Runs();
        var hasCoverage = database.Has("capture_channel_events", "run_id", "scope", "started_at_utc_ms", "stopped_at_utc_ms", "evidence");
        var hasObservations = database.Has("tab_observations", "observation_id", "run_id", "run_sequence", "observed_at_utc_ms");
        var hasFocus = database.Has("active_tab_focus", "observation_id", "window_focus_observation_id", "window_active_observation_id",
            "has_tab", "tab_key");
        var hasWindows = database.Has("browser_window_focus_events", "observation_id", "has_window");
        var (url, urlJoin) = TabFact("url");
        var (title, titleJoin) = TabFact("title");
        if (!hasCoverage || !hasObservations || !hasFocus || !hasWindows || runs.Count == 0) yield break;

        // Browser coverage records whole gaps. The next snapshot supplies fresh tab facts.
        var gaps = database.Read("""
            SELECT run_id, started_at_utc_ms, stopped_at_utc_ms
            FROM capture_channel_events
            WHERE scope='tabs' AND evidence IN ('snapshot_timeout','reconciliation_changed')
              AND (stopped_at_utc_ms IS NULL OR stopped_at_utc_ms > started_at_utc_ms)
            """, row => (Run: row.GetInt64(0), Start: row.GetInt64(1), End: DayDatabase.Number(row, 2) ?? long.MaxValue))
            .ToLookup(gap => gap.Run);

        var changes = database.Read($"""
            SELECT o.run_id, o.run_sequence, o.observed_at_utc_ms, w.has_window, f.has_tab, f.tab_key,
                   window.observed_at_utc_ms, active.observed_at_utc_ms, {url}, {title}
            FROM tab_observations o JOIN active_tab_focus f USING(observation_id)
            LEFT JOIN tab_observations window ON window.observation_id=f.window_focus_observation_id AND window.run_id=o.run_id
            LEFT JOIN browser_window_focus_events w ON w.observation_id=window.observation_id
            LEFT JOIN tab_observations active ON active.observation_id=f.window_active_observation_id AND active.run_id=o.run_id
            {urlJoin}
            {titleJoin}
            """, row =>
            {
                var position = DayDatabase.Position(row);
                var freshSince = gaps[position.Run].Where(gap => gap.End <= position.At)
                    .Select(gap => gap.End).DefaultIfEmpty(long.MinValue).Max();
                var windowAt = DayDatabase.Number(row, 6);
                DayBrowserFocus? focus = null;
                if (DayDatabase.Number(row, 3) == 1 && windowAt is { } focusedAt && focusedAt >= freshSince)
                {
                    DayTab? tab = null;
                    if (DayDatabase.Number(row, 4) == 1 && DayDatabase.Number(row, 5) is not null &&
                        DayDatabase.Number(row, 7) >= freshSince)
                        tab = new(FreshText(8), FreshText(10));
                    focus = new(focusedAt, tab);
                }
                return new DayChange<DayBrowserFocus?>(position, focus);

                string? FreshText(int index) => DayDatabase.Number(row, index + 1) >= freshSince
                    ? DayDatabase.Text(row, index) : null;
            });

        foreach (var span in DayTimeline.Values(database.Day, runs, changes, End))
        {
            if (span.Value is not { } focus) continue;
            if (focus.Tab is { } tab)
                focus = focus with { Tab = tab with { Title = titles.Apply("tab_title", tab.Title) } };
            yield return new(span.Start, span.End, focus);
        }

        long? End(DayPosition position)
        {
            var end = long.MaxValue;
            foreach (var gap in gaps[position.Run])
            {
                if (gap.End <= position.At) continue;
                if (gap.Start <= position.At) return null;
                end = Math.Min(end, gap.Start);
            }
            return end;
        }
    }

    private (string Columns, string Join) TabFact(string field)
    {
        var table = $"tab_{field}_observations";
        if (!database.Has(table, "observation_id", "tab_key", field)) return ("NULL, NULL", "");
        return ($"{field}.{field}, {field}_time.observed_at_utc_ms", $"""
            LEFT JOIN {table} {field} ON {field}.tab_key=f.tab_key AND {field}.observation_id=(
                SELECT fact.observation_id FROM {table} fact JOIN tab_observations fact_time USING(observation_id)
                WHERE fact.tab_key=f.tab_key AND fact.observation_id<=o.observation_id AND fact_time.run_id=o.run_id
                ORDER BY fact.observation_id DESC LIMIT 1)
            LEFT JOIN tab_observations {field}_time ON {field}_time.observation_id={field}.observation_id
            """);
    }

    private IEnumerable<DayVisit> ReadVisits()
    {
        if (!database.Has("visits", "visited_at_utc_ms", "is_local")) return [];
        var url = database.Column("visits", "url", "v.");
        var transition = database.Column("visits", "transition", "v.");
        var titles = database.Has("history_pages", "browser", "profile_id", "url", "title")
            && database.Has("visits", "browser", "profile_id", "url");
        var title = titles ? "h.title" : "NULL";
        var join = titles ? "LEFT JOIN history_pages h USING(browser,profile_id,url)" : "";
        return database.Read($"""
            SELECT v.visited_at_utc_ms, {url}, {title}, {transition}
            FROM visits v {join}
            WHERE v.is_local=1 AND v.visited_at_utc_ms >= $from AND v.visited_at_utc_ms < $to
            """, row => new DayVisit(row.GetInt64(0), DayDatabase.Text(row, 1), DayDatabase.Text(row, 2), DayDatabase.Text(row, 3)),
            bounded: true);
    }

    private IEnumerable<DayDownload> ReadDownloads()
    {
        if (!database.Has("downloads", "started_at_utc_ms")) return [];
        var originalUrl = database.Column("downloads", "url", "d.");
        var joined = database.Has("current_downloads", "download_key") && database.Has("downloads", "download_key");
        var finalUrl = joined ? database.Column("current_downloads", "final_url", "c.") : "NULL";
        var path = joined ? database.Column("current_downloads", "local_path", "c.") : "NULL";
        var state = joined ? database.Column("current_downloads", "state", "c.") : "NULL";
        var join = joined ? "LEFT JOIN current_downloads c USING(download_key)" : "";
        return database.Read($"""
            SELECT d.started_at_utc_ms, COALESCE({finalUrl},{originalUrl}), {path}, {state}
            FROM downloads d {join}
            WHERE d.started_at_utc_ms >= $from AND d.started_at_utc_ms < $to
            """, row => new DayDownload(row.GetInt64(0), DayDatabase.Text(row, 1), DayDatabase.Text(row, 2), DayDatabase.Text(row, 3)),
            bounded: true);
    }
}
