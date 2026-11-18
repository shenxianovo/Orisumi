using Microsoft.Data.Sqlite;
using Orisumi.Collectors.Storage;
using System.Text;
using System.Text.Json;

namespace Orisumi.Collectors.Browser;

internal static class BrowserDatabase
{
    internal const string Source = "browser";

    internal static async Task<Reply> HandleMessage(string store, ReadOnlyMemory<byte> message)
    {
        var batch = JsonSerializer.Deserialize<Batch>(message.Span, JsonSerializerOptions.Web);
        if (batch is null || batch.ProfileId == Guid.Empty)
            throw new InvalidDataException("invalid browser batch profile");

        if (batch is { GetDatabaseId: true, Visits: null, HistoryPages: null, TabEvents: null, BookmarkObservation: null, Downloads: null, Coverage: null })
        {
            // All extension cursors and baselines follow this file's lifetime.
            var databaseId = (string)await Execute(store, """
                INSERT OR IGNORE INTO _collector_state VALUES (1, lower(hex(randomblob(16))));
                SELECT database_id FROM _collector_state WHERE singleton = 1;
                """);
            if (!Guid.TryParseExact(databaseId, "N", out _))
                throw new InvalidDataException("SQLite did not return a database ID");
            return new Reply(true, DatabaseId: databaseId);
        }
        if (batch.GetDatabaseId)
            throw new InvalidDataException("database ID requests cannot contain data");

        if (batch.Coverage is { } coverage)
        {
            if (batch.Visits is not null || batch.HistoryPages is not null || batch.TabEvents is not null ||
                batch.BookmarkObservation is not null || batch.Downloads is not null)
                throw new InvalidDataException("coverage observations cannot contain other data");
            return new Reply(true, (long)await Execute(store, WriteCoverage(batch.ProfileId, coverage) + "SELECT changes();"));
        }

        if (batch.BookmarkObservation is { } observation)
        {
            if (batch.Visits is not null || batch.HistoryPages is not null || batch.TabEvents is not null || batch.Downloads is not null)
                throw new InvalidDataException("bookmark observations cannot contain other data");
            return new Reply(true, BookmarkDatabase.Capture(store, batch.ProfileId, observation));
        }

        // Hex encoding keeps the batch separate from the SQL syntax.
        var payload = $"CAST(X'{Convert.ToHexString(JsonSerializer.SerializeToUtf8Bytes(batch, JsonSerializerOptions.Web))}' AS TEXT)";
        var write = (batch.Visits, batch.HistoryPages, batch.TabEvents, batch.Downloads) switch
        {
            ({ } visits, null, null, null) => WriteVisits(batch.ProfileId, payload, visits),
            (null, { } pages, null, null) => WriteHistoryPages(batch.ProfileId, payload, pages),
            (null, null, { } events, null) => WriteTabEvents(batch.ProfileId, payload, events),
            (null, null, null, { } downloads) => WriteDownloads(batch.ProfileId, downloads),
            _ => throw new InvalidDataException("invalid browser batch"),
        };
        var count = batch.TabEvents is null ? "SELECT changes();" : "SELECT COUNT(*) FROM incoming_tab_events;";
        var inserted = (long)await Execute(store, write + "\n" + count);
        return new Reply(true, Inserted: inserted);
    }

    private static async Task<object> Execute(string store, string sql)
    {
        using var connection = CollectorDatabase.Open(store, Source);
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        try
        {
            var result = await command.ExecuteScalarAsync();
            transaction.Commit();
            return result!;
        }
        catch (SqliteException error)
        {
            throw new IOException($"could not save browser data: {error.Message}", error);
        }
    }

    internal sealed record Reply(bool Ok, long? Inserted = null, string? DatabaseId = null);

    private sealed record Batch(Guid ProfileId, Visit?[]? Visits, HistoryPage?[]? HistoryPages, TabEvent?[]? TabEvents,
        BookmarkDatabase.Observation? BookmarkObservation, Download?[]? Downloads, bool GetDatabaseId, Coverage?[]? Coverage);

    private sealed record Coverage(Guid EventId, string Scope, string Mode, long StartedAtUtcMs,
        long? StoppedAtUtcMs, string Evidence, string? Detail, Guid? RunId);

    private static string WriteCoverage(Guid profile, Coverage?[] entries)
    {
        var sql = new StringBuilder();
        foreach (var value in entries)
        {
            if (value is null || value.EventId == Guid.Empty || string.IsNullOrWhiteSpace(value.Scope) ||
                value.Mode is not ("notification" or "replay" or "snapshot") || value.StartedAtUtcMs < 0 ||
                value.StoppedAtUtcMs < value.StartedAtUtcMs || string.IsNullOrWhiteSpace(value.Evidence))
                throw new InvalidDataException("invalid capture coverage");
            var json = JsonSql(value);
            sql.AppendLine($"""
                INSERT OR IGNORE INTO capture_channel_events
                SELECT '{value.EventId:D}','chrome','{profile:D}',{json}->>'$.scope',{json}->>'$.mode',
                  {value.StartedAtUtcMs},{json}->>'$.stoppedAtUtcMs',{json}->>'$.evidence',{json}->>'$.detail',{json}->>'$.runId';
                """);
        }
        return sql.ToString();
    }

    private static string WriteVisits(Guid profileId, string payload, Visit?[] entries)
    {
        if (!entries.All(visit =>
                visit is { VisitId.Length: > 0, Url.Length: > 0, VisitedAtUtcMs: > 0 }))
            throw new InvalidDataException("invalid visits batch");

        return $"""
            INSERT INTO visits (browser, profile_id, visit_id, visited_at_utc_ms, url, transition, referring_visit_id, is_local)
            SELECT 'chrome', '{profileId:D}', value ->> '$.visitId',
                   value ->> '$.visitedAtUtcMs', value ->> '$.url', value ->> '$.transition',
                   value ->> '$.referringVisitId', value ->> '$.isLocal'
            FROM json_each({payload}, '$.visits')
            WHERE true
            ON CONFLICT (browser, profile_id, visit_id) DO UPDATE SET
                transition = COALESCE(visits.transition, excluded.transition),
                referring_visit_id = COALESCE(visits.referring_visit_id, excluded.referring_visit_id),
                is_local = COALESCE(visits.is_local, excluded.is_local)
            -- A visit does not change; later reads may only fill attributes that were unknown.
            WHERE (visits.transition IS NULL AND excluded.transition IS NOT NULL)
               OR (visits.referring_visit_id IS NULL AND excluded.referring_visit_id IS NOT NULL)
               OR (visits.is_local IS NULL AND excluded.is_local IS NOT NULL);
            """;
    }

    private sealed record Visit(string? VisitId, long VisitedAtUtcMs, string? Url, string? Transition,
        string? ReferringVisitId, bool? IsLocal);

    private static string WriteHistoryPages(Guid profileId, string payload, HistoryPage?[] entries)
    {
        if (!entries.All(page => page is { Url.Length: > 0, ObservedAtUtcMs: > 0 } && page.EventId != Guid.Empty &&
                !string.IsNullOrWhiteSpace(page.Title)))
            throw new InvalidDataException("invalid history pages batch");

        return $"""
            INSERT OR IGNORE INTO history_page_title_observations (browser, profile_id, event_id, url, title, observed_at_utc_ms)
            SELECT 'chrome', '{profileId:D}', value ->> '$.eventId', value ->> '$.url', value ->> '$.title', value ->> '$.observedAtUtcMs'
            FROM json_each({payload}, '$.historyPages')
            WHERE value ->> '$.eventId' IS NOT NULL
            OR value ->> '$.title' IS NOT (SELECT title FROM history_pages p
                WHERE p.browser='chrome' AND p.profile_id='{profileId:D}' AND p.url=value ->> '$.url')
              AND value ->> '$.observedAtUtcMs' >= (SELECT observed_at_utc_ms FROM history_pages p
                WHERE p.browser='chrome' AND p.profile_id='{profileId:D}' AND p.url=value ->> '$.url')
            OR NOT EXISTS (SELECT 1 FROM history_pages p WHERE p.browser='chrome' AND p.profile_id='{profileId:D}' AND p.url=value ->> '$.url');
            """;
    }

    private sealed record HistoryPage(string? Url, string? Title, long ObservedAtUtcMs, Guid? EventId);

    private static string WriteTabEvents(Guid profileId, string payload, TabEvent?[] entries)
    {
        if (!entries.All(entry => entry is { SessionSequence: > 0, RunStartedAtUtcMs: > 0, ObservedAtUtcMs: > 0 } &&
                entry.EventId != Guid.Empty && entry.CaptureSessionId != Guid.Empty &&
                (entry.Tabs?.All(tab => tab is { TabId: >= 0, Fields.ValueKind: JsonValueKind.Object }) ?? true)))
            throw new InvalidDataException("invalid tab observation");

        return $"""
            CREATE TEMP TABLE incoming_tab_events (ordinal INTEGER PRIMARY KEY, event_id TEXT UNIQUE, value TEXT NOT NULL);
            INSERT OR IGNORE INTO incoming_tab_events
            SELECT j.key,j.value->>'$.eventId',j.value FROM json_each({payload}, '$.tabEvents') j
            WHERE NOT EXISTS (SELECT 1 FROM tab_observations o WHERE o.event_id=j.value->>'$.eventId')
            ORDER BY j.key;

            INSERT OR IGNORE INTO capture_runs
            SELECT DISTINCT value->>'$.captureSessionId','chrome','{profileId:D}','tabs',value->>'$.runStartedAtUtcMs',NULL
            FROM incoming_tab_events;
            INSERT INTO tab_observations(event_id,run_id,run_sequence,observed_at_utc_ms,event_kind)
            SELECT event_id,value->>'$.captureSessionId',value->>'$.sessionSequence',value->>'$.observedAtUtcMs',value->>'$.eventKind'
            FROM incoming_tab_events ORDER BY ordinal;

            CREATE TEMP VIEW incoming_tabs AS
            SELECT observation_id,run_id,j.value->>'$.tabId' AS tab_id,j.value->'$.fields' AS fields
            FROM incoming_tab_events JOIN tab_observations USING(event_id),json_each(incoming_tab_events.value,'$.tabs') j
            WHERE json_type(incoming_tab_events.value,'$.tabs')='array';
            CREATE TEMP VIEW incoming_windows AS
            SELECT observation_id,run_id,j.value->>'$.windowId' AS window_id,j.value->>'$.isPresent' AS is_present
            FROM incoming_tab_events JOIN tab_observations USING(event_id),json_each(incoming_tab_events.value,'$.windows') j
            WHERE json_type(incoming_tab_events.value,'$.windows')='array';
            CREATE TEMP VIEW incoming_focus AS
            SELECT observation_id,run_id,value->>'$.focus.tabId' AS tab_id
            FROM incoming_tab_events JOIN tab_observations USING(event_id) WHERE json_type(value,'$.focus')='object';

            INSERT INTO tabs(run_id,tab_id)
            SELECT run_id,tab_id FROM incoming_tabs UNION SELECT run_id,tab_id FROM incoming_focus WHERE tab_id IS NOT NULL
            ON CONFLICT(run_id,tab_id) DO NOTHING;
            INSERT INTO browser_windows(run_id,window_id)
            SELECT run_id,window_id FROM incoming_windows
            UNION SELECT run_id,fields->>'$.windowId' FROM incoming_tabs WHERE fields->>'$.windowId' IS NOT NULL
            ON CONFLICT(run_id,window_id) DO NOTHING;
            INSERT INTO window_presence_events
            SELECT observation_id,window_key,is_present FROM incoming_windows JOIN browser_windows USING(run_id,window_id);
            INSERT INTO tab_presence_events
            SELECT observation_id,tab_key,fields->>'$.isPresent' FROM incoming_tabs JOIN tabs USING(run_id,tab_id)
            WHERE json_type(fields,'$.isPresent') IS NOT NULL;
            INSERT INTO tab_window_observations
            SELECT observation_id,tab_key,window_key IS NOT NULL,window_key
            FROM incoming_tabs JOIN tabs USING(run_id,tab_id)
            LEFT JOIN browser_windows w ON w.run_id=incoming_tabs.run_id AND w.window_id=fields->>'$.windowId'
            WHERE json_type(fields,'$.windowId') IS NOT NULL;
            INSERT INTO tab_url_observations
            SELECT observation_id,tab_key,fields->>'$.url' FROM incoming_tabs JOIN tabs USING(run_id,tab_id)
            WHERE json_type(fields,'$.url') IS NOT NULL;
            INSERT INTO tab_title_observations
            SELECT observation_id,tab_key,fields->>'$.title' FROM incoming_tabs JOIN tabs USING(run_id,tab_id)
            WHERE json_type(fields,'$.title') IS NOT NULL;
            INSERT INTO active_tab_focus_events
            SELECT observation_id,tab_key IS NOT NULL,tab_key FROM incoming_focus LEFT JOIN tabs USING(run_id,tab_id);
            """;
    }

    private static string JsonSql<T>(T value) => $"CAST(X'{Convert.ToHexString(JsonSerializer.SerializeToUtf8Bytes(value, JsonSerializerOptions.Web))}' AS TEXT)";

    private sealed record TabEvent(Guid EventId, Guid CaptureSessionId, long RunStartedAtUtcMs, long SessionSequence,
        long ObservedAtUtcMs, string? EventKind, WindowChange[]? Windows, TabChange[]? Tabs, FocusChange? Focus);
    private sealed record WindowChange(int WindowId, bool IsPresent);
    private sealed record TabChange(int TabId, JsonElement Fields);
    private sealed record FocusChange(int? TabId);

    private static string WriteDownloads(Guid profileId, Download?[] entries)
    {
        var sql = new StringBuilder();
        foreach (var entry in entries)
        {
            if (entry is not { DownloadId: >= 0, RunSequence: > 0, RunStartedAtUtcMs: > 0, ObservedAtUtcMs: > 0 } ||
                entry.EventId == Guid.Empty || entry.RunId == Guid.Empty || entry.Fields.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("invalid download observation");
            var json = JsonSql(entry.Fields);
            sql.AppendLine($"""
                INSERT OR IGNORE INTO capture_runs VALUES ('{entry.RunId:D}','chrome','{profileId:D}','downloads',{entry.RunStartedAtUtcMs},NULL);
                INSERT OR IGNORE INTO downloads(browser,profile_id,download_id) VALUES ('chrome','{profileId:D}',{entry.DownloadId});
                UPDATE downloads SET url=COALESCE(url,{json}->>'$.url'),started_at_utc_ms=COALESCE(started_at_utc_ms,{json}->>'$.startedAtUtcMs')
                WHERE browser='chrome' AND profile_id='{profileId:D}' AND download_id={entry.DownloadId}
                  AND ((url IS NULL AND {json}->>'$.url' IS NOT NULL) OR (started_at_utc_ms IS NULL AND {json}->>'$.startedAtUtcMs' IS NOT NULL));
                INSERT OR IGNORE INTO download_observations(event_id,run_id,run_sequence,observed_at_utc_ms,kind,download_key)
                SELECT '{entry.EventId:D}','{entry.RunId:D}',{entry.RunSequence},{entry.ObservedAtUtcMs},{JsonSql(entry.Kind)}->>'$',download_key
                FROM downloads WHERE browser='chrome' AND profile_id='{profileId:D}' AND download_id={entry.DownloadId};
                """);
            void AppendChanged(string aspect, (string Column, string Field)[] fields, Func<string[], string[]>? adjust = null)
            {
                // Missing callback fields retain the previous value; explicit null clears it.
                var table = $"download_{aspect}_observations";
                var previous = $"FROM {table} f JOIN download_observations p USING(observation_id) " +
                    "WHERE p.download_key=o.download_key AND p.observation_id<o.observation_id";
                var expressions = fields.Select(field => $"""
                    CASE WHEN json_type({json},'$.{field.Field}') IS NOT NULL THEN {json}->>'$.{field.Field}'
                    ELSE (SELECT {field.Column} {previous} ORDER BY p.observation_id DESC LIMIT 1) END
                    """).ToArray();
                var values = string.Join(',', adjust?.Invoke(expressions) ?? expressions);
                var supplied = string.Join(" OR ", fields.Select(field => $"json_type({json},'$.{field.Field}') IS NOT NULL"));
                var columns = string.Join(',', fields.Select(field => field.Column));
                sql.AppendLine($"""
                    INSERT OR IGNORE INTO {table} SELECT observation_id,{values} FROM download_observations o
                    WHERE event_id='{entry.EventId:D}' AND ({supplied})
                      AND (NOT EXISTS (SELECT 1 {previous})
                        OR (o.kind='snapshot' AND NOT EXISTS (SELECT 1 FROM download_observations p WHERE p.download_key=o.download_key AND p.run_id=o.run_id AND p.kind='snapshot' AND p.observation_id<o.observation_id))
                        OR ({values}) IS NOT (SELECT {columns} {previous} ORDER BY p.observation_id DESC LIMIT 1));
                    """);
            }
            AppendChanged("response", [("final_url", "finalUrl"), ("total_bytes", "totalBytes"), ("mime_type", "mimeType")]);
            // An end time does not apply while the download is in progress, even if the callback kept the old one.
            AppendChanged("state", [("state", "state"), ("ended_at_utc_ms", "endedAtUtcMs")],
                values => [values[0], $"CASE WHEN ({values[0]})='in_progress' THEN NULL ELSE ({values[1]}) END"]);
            AppendChanged("path", [("local_path", "filename")]);
            sql.AppendLine($"""
                DELETE FROM download_observations WHERE event_id='{entry.EventId:D}' AND kind='snapshot'
                  AND NOT EXISTS (SELECT 1 FROM download_response_observations f WHERE f.observation_id=download_observations.observation_id)
                  AND NOT EXISTS (SELECT 1 FROM download_state_observations f WHERE f.observation_id=download_observations.observation_id)
                  AND NOT EXISTS (SELECT 1 FROM download_path_observations f WHERE f.observation_id=download_observations.observation_id);
                """);
        }
        return sql.ToString();
    }

    private sealed record Download(Guid EventId, Guid RunId, long RunStartedAtUtcMs, long RunSequence,
        long ObservedAtUtcMs, string Kind, long DownloadId, JsonElement Fields);
}
