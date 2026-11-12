using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Orisumi.Collectors.MacOS;

public static class ActivityTimeline
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static async Task Write(string store, DateTimeOffset from, DateTimeOffset to, TextWriter output)
    {
        var database = MacOSStore.GetDatabasePath(store);
        if (!File.Exists(database))
            throw new FileNotFoundException($"macOS activity database not found: {database}");

        const string windowTableQuery = """
            SELECT EXISTS (
                SELECT 1 FROM sqlite_schema
                WHERE type = 'table' AND name = 'focused_window_events'
            ) AS present;
            """;
        using var tables = JsonDocument.Parse(await Query(database, windowTableQuery));
        var includeWindows = tables.RootElement[0].GetProperty("present").GetInt32() != 0;

        var windowEvents = includeWindows ? """
            UNION ALL
            SELECT observed_at_utc_ms, run_id, run_sequence, 'focused_window', event_kind, pid, NULL, NULL, window_title, NULL, NULL
            FROM focused_window_events
            """ : "";
        var sql = string.Create(CultureInfo.InvariantCulture, $"""
            WITH events(observed_at_utc_ms, run_id, run_sequence, source, event_kind, pid, bundle_id, app_name, window_title, state, idle_ms) AS (
                SELECT observed_at_utc_ms, run_id, run_sequence, 'foreground_app', event_kind, pid, bundle_id, app_name, NULL, NULL, NULL
                FROM foreground_app_events
                UNION ALL
                SELECT observed_at_utc_ms, run_id, run_sequence, 'app_lifecycle', event_kind, pid, bundle_id, app_name, NULL, NULL, NULL
                FROM app_lifecycle_events WHERE event_kind <> 'snapshot'
                UNION ALL
                SELECT observed_at_utc_ms, run_id, run_sequence, 'power', event_kind, NULL, NULL, NULL, NULL, NULL, NULL
                FROM system_sleep_events
                UNION ALL
                SELECT observed_at_utc_ms, run_id, run_sequence, 'session', event_kind, NULL, NULL, NULL, NULL, NULL, NULL
                FROM user_session_events
                UNION ALL
                SELECT observed_at_utc_ms, run_id, run_sequence, 'afk', event_kind, NULL, NULL, NULL, NULL, state, idle_ms
                FROM afk_events
                {windowEvents}
                UNION ALL
                SELECT started_at_utc_ms, run_id, NULL, 'capture_run', 'started', NULL, NULL, NULL, NULL, NULL, NULL
                FROM capture_runs
                UNION ALL
                SELECT stopped_at_utc_ms, run_id, NULL, 'capture_run', 'stopped', NULL, NULL, NULL, NULL, NULL, NULL
                FROM capture_runs WHERE stopped_at_utc_ms IS NOT NULL
            )
            SELECT events.*, (
                SELECT zone.time_zone_id
                FROM time_zone_events AS zone
                WHERE zone.run_id = events.run_id
                  AND (zone.observed_at_utc_ms, zone.run_sequence) <= (
                      events.observed_at_utc_ms,
                      COALESCE(events.run_sequence, CASE WHEN events.event_kind = 'started' THEN 1 ELSE {long.MaxValue} END)
                  )
                ORDER BY zone.observed_at_utc_ms DESC, zone.run_sequence DESC
                LIMIT 1
            ) AS time_zone_id
            FROM events
            WHERE observed_at_utc_ms >= {MillisecondCeiling(from)} AND observed_at_utc_ms < {MillisecondCeiling(to)}
            ORDER BY observed_at_utc_ms, run_id,
                CASE WHEN run_sequence IS NOT NULL THEN 1 WHEN event_kind = 'started' THEN 0 ELSE 2 END,
                run_sequence;
            """);
        var json = await Query(database, sql);
        if (string.IsNullOrWhiteSpace(json))
        {
            output.WriteLine("No activity recorded in this time range.");
            return;
        }

        var rows = JsonSerializer.Deserialize<TimelineRow[]>(json, JsonOptions)
            ?? throw new InvalidDataException("SQLite did not return timeline rows");
        foreach (var row in rows)
        {
            var observedAt = DateTimeOffset.FromUnixTimeMilliseconds(row.ObservedAtUtcMs);
            if (row.TimeZoneId is { } id && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var timeZone))
                observedAt = TimeZoneInfo.ConvertTime(observedAt, timeZone);
            var reference = row.RunSequence is long sequence ? $"{row.RunId}/{sequence}" : row.RunId;
            var details = new List<string>();
            if (row.AppName is { Length: > 0 } appName)
                details.Add(OneLine(appName));
            if (row.BundleId is { Length: > 0 } bundleId)
                details.Add(OneLine(bundleId));
            if (row.Pid is int pid)
                details.Add($"pid={pid}");
            if (row.WindowTitle is { Length: > 0 } windowTitle)
                details.Add(OneLine(windowTitle));
            if (row.State is { Length: > 0 } state)
                details.Add($"state={OneLine(state)}");
            if (row.IdleMs is long idleMs)
                details.Add($"idle_ms={idleMs.ToString(CultureInfo.InvariantCulture)}");
            var detail = details.Count == 0 ? "" : $"  {string.Join(", ", details)}";
            output.WriteLine($"{observedAt.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture)}  {row.Source}  {row.EventKind}{detail}  [{reference}]");
        }
    }

    private static long MillisecondCeiling(DateTimeOffset timestamp) =>
        timestamp.ToUnixTimeMilliseconds() + (timestamp.UtcTicks % TimeSpan.TicksPerMillisecond == 0 ? 0 : 1);

    private static string OneLine(string value) =>
        string.Concat(value.Select(character => char.IsControl(character) || character is '\u2028' or '\u2029' ? ' ' : character));

    private static async Task<string> Query(string database, string sql)
    {
        var start = new ProcessStartInfo("/usr/bin/sqlite3")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[] { "-batch", "-bail", "-readonly", "-init", "/dev/null", "-json", "-cmd", ".timeout 3000", database, sql })
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start) ?? throw new IOException("could not start SQLite");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new IOException($"could not read activity database: {(await stderr).Trim()}");
        return await stdout;
    }

    private sealed record TimelineRow(
        long ObservedAtUtcMs, string RunId, long? RunSequence, string Source, string EventKind,
        int? Pid, string? BundleId, string? AppName, string? WindowTitle, string? State, long? IdleMs,
        string? TimeZoneId);
}
