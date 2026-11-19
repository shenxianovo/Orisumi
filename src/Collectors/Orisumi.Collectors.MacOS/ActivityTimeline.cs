using System.Globalization;
using Microsoft.Data.Sqlite;
using Orisumi.Platform;

namespace Orisumi.Collectors.MacOS;

public static class ActivityTimeline
{
    public static async Task Write(string store, DateTimeOffset from, DateTimeOffset to, TextWriter output)
    {
        // The timeline of this host: the database macOS capture writes now.
        var database = new Store(store).CurrentDatabase("macos")
            ?? throw new FileNotFoundException($"no macOS activity database in {new Store(store).Root}");

        var sql = string.Create(CultureInfo.InvariantCulture, $"""
            WITH events(observed_at_utc_ms, run_id, run_sequence, source, event_kind, pid, bundle_id, app_name, window_title, state, idle_ms, permission) AS (
                SELECT observed_at_utc_ms, run_id, run_sequence, 'foreground_app', event_kind, pid, bundle_id, app_name, NULL, NULL, NULL, NULL
                FROM foreground_app_history
                UNION ALL
                SELECT observed_at_utc_ms, run_id, run_sequence, 'app_lifecycle', event_kind, pid, bundle_id, app_name, NULL, NULL, NULL, NULL
                FROM running_app_history WHERE event_kind <> 'snapshot'
                UNION ALL
                SELECT observed_at_utc_ms, run_id, run_sequence, 'power', event_kind, NULL, NULL, NULL, NULL, NULL, NULL, NULL
                FROM system_sleep_events
                UNION ALL
                SELECT observed_at_utc_ms, run_id, run_sequence, 'session', event_kind, NULL, NULL, NULL, NULL, NULL, NULL, NULL
                FROM user_session_events
                UNION ALL
                SELECT observed_at_utc_ms, run_id, run_sequence, 'screen_lock', event_kind, NULL, NULL, NULL, NULL,
                    CASE is_locked WHEN 1 THEN 'locked' WHEN 0 THEN 'unlocked' END, NULL, NULL
                FROM screen_lock_events
                UNION ALL
                SELECT observed_at_utc_ms, run_id, run_sequence, 'display_sleep', event_kind, NULL, NULL, NULL, NULL,
                    CASE is_asleep WHEN 1 THEN 'asleep' WHEN 0 THEN 'awake' END, NULL, NULL
                FROM display_sleep_events
                UNION ALL
                SELECT observed_at_utc_ms, run_id, run_sequence, 'afk', event_kind, NULL, NULL, NULL, NULL, state, idle_ms, NULL
                FROM afk_events
                UNION ALL
                SELECT observed_at_utc_ms, run_id, run_sequence, 'capture_permission', event_kind,
                    NULL, NULL, NULL, NULL, NULL, NULL, permission
                FROM capture_permission_events
                UNION ALL
                SELECT observed_at_utc_ms, run_id, run_sequence, 'capture_channel:' || channel, reason,
                    NULL, NULL, NULL, NULL, CASE available WHEN 1 THEN 'available' ELSE 'unavailable' END, NULL, NULL
                FROM capture_channel_events
                UNION ALL
                SELECT observed_at_utc_ms, run_id, run_sequence, 'focused_window', event_kind, pid, NULL, NULL, window_title, NULL, NULL, NULL
                FROM window_history
                UNION ALL
                SELECT started_at_utc_ms, run_id, NULL, 'capture_run', 'started', NULL, NULL, NULL, NULL, NULL, NULL, NULL
                FROM capture_runs
                UNION ALL
                SELECT stopped_at_utc_ms, run_id, NULL, 'capture_run', 'stopped', NULL, NULL, NULL, NULL, NULL, NULL, NULL
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
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 3,
        }.ToString());
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = await command.ExecuteReaderAsync();
        if (!reader.HasRows)
        {
            output.WriteLine("No activity recorded in this time range.");
            return;
        }

        T? OptionalValue<T>(int column) where T : struct => reader.IsDBNull(column) ? null : reader.GetFieldValue<T>(column);
        string? OptionalText(int column) => reader.IsDBNull(column) ? null : reader.GetString(column);
        while (await reader.ReadAsync())
        {
            var row = new TimelineRow(reader.GetInt64(0), reader.GetInt64(1), OptionalValue<long>(2),
                reader.GetString(3), reader.GetString(4), OptionalValue<int>(5), OptionalText(6), OptionalText(7),
                OptionalText(8), OptionalText(9), OptionalValue<long>(10), OptionalText(12), OptionalText(11));
            var observedAt = DateTimeOffset.FromUnixTimeMilliseconds(row.ObservedAtUtcMs);
            if (row.TimeZoneId is { } id && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var timeZone))
                observedAt = TimeZoneInfo.ConvertTime(observedAt, timeZone);
            var reference = row.RunSequence is long sequence ? $"{row.RunId}/{sequence}" : row.RunId.ToString(CultureInfo.InvariantCulture);
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
            if (row.Permission is { Length: > 0 } permission)
                details.Add($"permission={OneLine(permission)}");
            var detail = details.Count == 0 ? "" : $"  {string.Join(", ", details)}";
            output.WriteLine($"{observedAt.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture)}  {row.Source}  {row.EventKind}{detail}  [{reference}]");
        }
    }

    private static long MillisecondCeiling(DateTimeOffset timestamp) =>
        timestamp.ToUnixTimeMilliseconds() + (timestamp.UtcTicks % TimeSpan.TicksPerMillisecond == 0 ? 0 : 1);

    private static string OneLine(string value) =>
        string.Concat(value.Select(character => char.IsControl(character) || character is '\u2028' or '\u2029' ? ' ' : character));

    private sealed record TimelineRow(
        long ObservedAtUtcMs, long RunId, long? RunSequence, string Source, string EventKind,
        int? Pid, string? BundleId, string? AppName, string? WindowTitle, string? State, long? IdleMs,
        string? TimeZoneId, string? Permission);
}
