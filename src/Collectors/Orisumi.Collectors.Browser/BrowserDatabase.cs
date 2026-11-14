using Microsoft.Data.Sqlite;
using Orisumi.Collectors.Storage;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Orisumi.Collectors.Browser;

internal static class BrowserDatabase
{
    internal static async Task<Reply> HandleMessage(string store, ReadOnlyMemory<byte> message)
    {
        var batch = JsonSerializer.Deserialize<Batch>(message.Span, JsonSerializerOptions.Web);
        if (batch is null || batch.ProfileId == Guid.Empty)
            throw new InvalidDataException("invalid browser batch profile");

        if (batch is { GetDatabaseId: true, Visits: null, HistoryPages: null, ActiveTabEvents: null, BookmarkObservation: null, Downloads: null })
        {
            // The extension ties its history cursor to this file's lifetime.
            var databaseId = await Execute(store, """
                INSERT OR IGNORE INTO _collector_state VALUES (1, lower(hex(randomblob(16))));
                SELECT database_id FROM _collector_state WHERE singleton = 1;
                """);
            if (!Guid.TryParseExact(databaseId, "N", out _))
                throw new InvalidDataException("SQLite did not return a database ID");
            return new Reply(true, DatabaseId: databaseId);
        }
        if (batch.GetDatabaseId)
            throw new InvalidDataException("database ID requests cannot contain data");

        if (batch.BookmarkObservation is { } observation)
        {
            if (batch.Visits is not null || batch.HistoryPages is not null || batch.ActiveTabEvents is not null || batch.Downloads is not null)
                throw new InvalidDataException("bookmark observations cannot contain other data");
            return new Reply(true, BookmarkDatabase.Capture(store, batch.ProfileId, observation));
        }

        // Hex encoding keeps the batch separate from the SQL syntax.
        var payload = $"CAST(X'{Convert.ToHexString(JsonSerializer.SerializeToUtf8Bytes(batch, JsonSerializerOptions.Web))}' AS TEXT)";
        var write = (batch.Visits, batch.HistoryPages, batch.ActiveTabEvents, batch.Downloads) switch
        {
            ({ } visits, null, null, null) => WriteVisits(batch.ProfileId, payload, visits),
            (null, { } pages, null, null) => WriteHistoryPages(batch.ProfileId, payload, pages),
            (null, null, { } events, null) => WriteActiveTabEvents(batch.ProfileId, events),
            (null, null, null, { } downloads) => WriteDownloads(batch.ProfileId, downloads),
            _ => throw new InvalidDataException("invalid browser batch"),
        };
        var result = await Execute(store, $"""
            {write}
            SELECT changes();
            """);
        if (!long.TryParse(result, NumberStyles.Integer, CultureInfo.InvariantCulture, out var inserted))
            throw new InvalidDataException("SQLite did not return a changed row count");
        return new Reply(true, Inserted: inserted);
    }

    private static async Task<string> Execute(string store, string sql)
    {
        using var connection = OpenDatabase(store);
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        try
        {
            var result = await command.ExecuteScalarAsync();
            transaction.Commit();
            return Convert.ToString(result, CultureInfo.InvariantCulture) ?? "";
        }
        catch (SqliteException error)
        {
            throw new IOException($"could not save browser data: {error.Message}", error);
        }
    }

    internal static SqliteConnection OpenDatabase(string store)
    {
        var database = Path.GetFullPath(Path.Combine(store, "raw", "browser.sqlite"));
        Directory.CreateDirectory(Path.GetDirectoryName(database)!);
        var fileOptions = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.Write, Share = FileShare.ReadWrite };
        if (!OperatingSystem.IsWindows())
            fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (File.Open(database, fileOptions)) { }
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database, ForeignKeys = true, Pooling = false, DefaultTimeout = 3,
        }.ToString());
        try
        {
            connection.Open();
            SqliteSchema.EnsureCreated(connection);
            return connection;
        }
        catch (SqliteException error)
        {
            connection.Dispose();
            throw new IOException($"could not open browser database: {error.Message}", error);
        }
        catch { connection.Dispose(); throw; }
    }

    internal sealed record Reply(bool Ok, long? Inserted = null, string? DatabaseId = null);

    private sealed record Batch(Guid ProfileId, Visit?[]? Visits, HistoryPage?[]? HistoryPages, ActiveTabEvent?[]? ActiveTabEvents,
        BookmarkDatabase.Observation? BookmarkObservation, Download?[]? Downloads, bool GetDatabaseId);

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
                transition = COALESCE(excluded.transition, visits.transition),
                referring_visit_id = COALESCE(excluded.referring_visit_id, visits.referring_visit_id),
                is_local = COALESCE(excluded.is_local, visits.is_local)
            WHERE (excluded.transition IS NOT NULL AND visits.transition IS NOT excluded.transition)
               OR (excluded.referring_visit_id IS NOT NULL AND visits.referring_visit_id IS NOT excluded.referring_visit_id)
               OR (excluded.is_local IS NOT NULL AND visits.is_local IS NOT excluded.is_local);
            """;
    }

    private sealed record Visit(string? VisitId, long VisitedAtUtcMs, string? Url, string? Transition,
        string? ReferringVisitId, bool? IsLocal);

    private static string WriteHistoryPages(Guid profileId, string payload, HistoryPage?[] entries)
    {
        if (!entries.All(page => page is { Url.Length: > 0, ObservedAtUtcMs: > 0 } &&
                !string.IsNullOrWhiteSpace(page.Title)))
            throw new InvalidDataException("invalid history pages batch");

        return $"""
            INSERT INTO history_page_title_observations (browser, profile_id, url, title, observed_at_utc_ms)
            SELECT 'chrome', '{profileId:D}', value ->> '$.url', value ->> '$.title', value ->> '$.observedAtUtcMs'
            FROM json_each({payload}, '$.historyPages')
            WHERE value ->> '$.title' IS NOT (SELECT title FROM history_pages p
                WHERE p.browser='chrome' AND p.profile_id='{profileId:D}' AND p.url=value ->> '$.url')
              AND value ->> '$.observedAtUtcMs' >= (SELECT observed_at_utc_ms FROM history_pages p
                WHERE p.browser='chrome' AND p.profile_id='{profileId:D}' AND p.url=value ->> '$.url')
            OR NOT EXISTS (SELECT 1 FROM history_pages p WHERE p.browser='chrome' AND p.profile_id='{profileId:D}' AND p.url=value ->> '$.url');
            """;
    }

    private sealed record HistoryPage(string? Url, string? Title, long ObservedAtUtcMs);

    private static string WriteActiveTabEvents(Guid profileId, ActiveTabEvent?[] entries)
    {
        var sql = new StringBuilder();
        foreach (var entry in entries)
        {
            if (entry is not { SessionSequence: > 0, RunStartedAtUtcMs: > 0, ObservedAtUtcMs: > 0 } ||
                entry.EventId == Guid.Empty || entry.CaptureSessionId == Guid.Empty ||
                (entry.TabId is null) != (entry.WindowId is null))
                throw new InvalidDataException("invalid active tab observation");
            var json = JsonSql(entry);
            sql.AppendLine($"""
                INSERT OR IGNORE INTO capture_runs VALUES ('{entry.CaptureSessionId:D}','chrome','{profileId:D}','active_tabs',{entry.RunStartedAtUtcMs},NULL);
                INSERT OR IGNORE INTO tab_observations(event_id,run_id,run_sequence,observed_at_utc_ms,event_kind)
                VALUES ('{entry.EventId:D}','{entry.CaptureSessionId:D}',{entry.SessionSequence},{entry.ObservedAtUtcMs},{json}->>'$.eventKind');
                INSERT OR IGNORE INTO active_tab_focus_events
                SELECT observation_id,{json}->>'$.windowId',{json}->>'$.tabId' FROM tab_observations o WHERE event_id='{entry.EventId:D}'
                AND (NOT EXISTS (SELECT 1 FROM active_tab_history h WHERE h.run_id=o.run_id AND h.run_sequence<o.run_sequence)
                    OR ({json}->>'$.windowId',{json}->>'$.tabId') IS NOT
                    (SELECT window_id,tab_id FROM active_tab_history h WHERE h.run_id=o.run_id AND h.run_sequence<o.run_sequence ORDER BY h.run_sequence DESC LIMIT 1));
                """);
            foreach (var field in new[] { "url", "title" })
                sql.AppendLine($"""
                    INSERT OR IGNORE INTO tab_{field}_observations SELECT observation_id,{json}->>'$.tabId',{json}->>'$.{field}'
                    FROM tab_observations o WHERE event_id='{entry.EventId:D}' AND {json}->>'$.tabId' IS NOT NULL AND {json}->>'$.{field}' IS NOT NULL
                    AND {json}->>'$.{field}' IS NOT (SELECT f.{field} FROM tab_{field}_observations f JOIN tab_observations p USING(observation_id)
                        WHERE p.run_id=o.run_id AND f.tab_id={json}->>'$.tabId' AND p.run_sequence<o.run_sequence ORDER BY p.run_sequence DESC LIMIT 1);
                    """);
        }
        return sql.ToString();
    }

    private static string JsonSql<T>(T value) => $"CAST(X'{Convert.ToHexString(JsonSerializer.SerializeToUtf8Bytes(value, JsonSerializerOptions.Web))}' AS TEXT)";

    private sealed record ActiveTabEvent(Guid EventId, Guid CaptureSessionId, long RunStartedAtUtcMs, long SessionSequence,
        long ObservedAtUtcMs, string? EventKind, int? WindowId, int? TabId, string? Url, string? Title);

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
                INSERT OR IGNORE INTO download_observations(event_id,run_id,run_sequence,observed_at_utc_ms,kind,download_key)
                SELECT '{entry.EventId:D}','{entry.RunId:D}',{entry.RunSequence},{entry.ObservedAtUtcMs},{JsonSql(entry.Kind)}->>'$',download_key
                FROM downloads WHERE browser='chrome' AND profile_id='{profileId:D}' AND download_id={entry.DownloadId};
                """);
            void AppendChanged(string aspect, params (string Column, string Field)[] fields)
            {
                // Missing callback fields retain the previous value; explicit null clears it.
                var table = $"download_{aspect}_observations";
                var previous = $"FROM {table} f JOIN download_observations p USING(observation_id) " +
                    "WHERE p.download_key=o.download_key AND p.observation_id<o.observation_id";
                var values = string.Join(',', fields.Select(field => $"""
                    CASE WHEN json_type({json},'$.{field.Field}') IS NOT NULL THEN {json}->>'$.{field.Field}'
                    ELSE (SELECT {field.Column} {previous} ORDER BY p.observation_id DESC LIMIT 1) END
                    """));
                var supplied = string.Join(" OR ", fields.Select(field => $"json_type({json},'$.{field.Field}') IS NOT NULL"));
                var columns = string.Join(',', fields.Select(field => field.Column));
                sql.AppendLine($"""
                    INSERT OR IGNORE INTO {table} SELECT observation_id,{values} FROM download_observations o
                    WHERE event_id='{entry.EventId:D}' AND ({supplied})
                      AND (NOT EXISTS (SELECT 1 {previous})
                        OR ({values}) IS NOT (SELECT {columns} {previous} ORDER BY p.observation_id DESC LIMIT 1));
                    """);
            }
            AppendChanged("resource", ("url", "url"), ("started_at_utc_ms", "startedAtUtcMs"),
                ("final_url", "finalUrl"), ("total_bytes", "totalBytes"), ("mime_type", "mimeType"));
            AppendChanged("state", ("state", "state"), ("ended_at_utc_ms", "endedAtUtcMs"));
            AppendChanged("path", ("local_path", "filename"));
            sql.AppendLine($"""
                DELETE FROM download_observations WHERE event_id='{entry.EventId:D}' AND kind='snapshot'
                  AND NOT EXISTS (SELECT 1 FROM download_resource_observations f WHERE f.observation_id=download_observations.observation_id)
                  AND NOT EXISTS (SELECT 1 FROM download_state_observations f WHERE f.observation_id=download_observations.observation_id)
                  AND NOT EXISTS (SELECT 1 FROM download_path_observations f WHERE f.observation_id=download_observations.observation_id);
                """);
        }
        return sql.ToString();
    }

    private sealed record Download(Guid EventId, Guid RunId, long RunStartedAtUtcMs, long RunSequence,
        long ObservedAtUtcMs, string Kind, long DownloadId, JsonElement Fields);
}
