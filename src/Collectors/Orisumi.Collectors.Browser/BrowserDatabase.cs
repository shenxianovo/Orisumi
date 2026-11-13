using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Orisumi.Collectors.Browser;

internal static class BrowserDatabase
{
    private const string Schema = VisitsSchema + "\n" + HistoryPagesSchema + "\n" + ActiveTabEventsSchema + "\n" +
        DownloadsSchema + "\n" + CaptureRunsSchema;

    internal static async Task<Reply> HandleMessage(string store, ReadOnlyMemory<byte> message)
    {
        var batch = JsonSerializer.Deserialize<Batch>(message.Span, JsonSerializerOptions.Web);
        if (batch is null || batch.ProfileId == Guid.Empty)
            throw new InvalidDataException("invalid browser batch profile");

        if (batch is { GetDatabaseId: true, Visits: null, HistoryPages: null, ActiveTabEvents: null, BookmarkObservation: null, Downloads: null })
        {
            // The extension ties its history cursor to this file's lifetime.
            var databaseId = await Execute(store, $"""
                BEGIN IMMEDIATE;
                {Schema}
                CREATE TABLE IF NOT EXISTS _collector_state (
                    singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
                    database_id TEXT NOT NULL
                );
                INSERT OR IGNORE INTO _collector_state VALUES (1, lower(hex(randomblob(16))));
                COMMIT;
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
            PRAGMA foreign_keys=ON;
            BEGIN IMMEDIATE;
            {Schema}
            {write}
            COMMIT;
            SELECT changes();
            """);
        if (!long.TryParse(result, NumberStyles.Integer, CultureInfo.InvariantCulture, out var inserted))
            throw new InvalidDataException("SQLite did not return a changed row count");
        return new Reply(true, Inserted: inserted);
    }

    private static async Task<string> Execute(string store, string sql)
    {
        var database = Path.GetFullPath(Path.Combine(store, "raw", "browser.sqlite"));
        Directory.CreateDirectory(Path.GetDirectoryName(database)!);
        var fileOptions = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.Write, Share = FileShare.ReadWrite };
        if (!OperatingSystem.IsWindows())
            fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (File.Open(database, fileOptions)) { }

        var start = new ProcessStartInfo(OperatingSystem.IsMacOS() ? "/usr/bin/sqlite3" : "sqlite3")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
        };
        var nullDevice = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        foreach (var arg in new[] { "-batch", "-bail", "-init", nullDevice, "-cmd", ".timeout 3000", database })
            start.ArgumentList.Add(arg);

        using var process = Process.Start(start) ?? throw new IOException("could not start SQLite");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        IOException? inputError = null;
        try
        {
            await process.StandardInput.WriteLineAsync(sql);
            await process.StandardInput.DisposeAsync();
        }
        catch (IOException error)
        {
            inputError = error;
            process.StandardInput.BaseStream.Close();
        }

        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new IOException($"could not save browser data: {(await stderr).Trim()}");
        if (inputError is not null)
            throw inputError;
        return (await stdout).Trim();
    }

    internal sealed record Reply(bool Ok, long? Inserted = null, string? DatabaseId = null);

    private sealed record Batch(Guid ProfileId, Visit?[]? Visits, HistoryPage?[]? HistoryPages, ActiveTabEvent?[]? ActiveTabEvents,
        BookmarkDatabase.Observation? BookmarkObservation, Download?[]? Downloads, bool GetDatabaseId);

    private const string VisitsSchema = """
        CREATE TABLE IF NOT EXISTS visits (
            browser TEXT NOT NULL,
            profile_id TEXT NOT NULL,
            visit_id TEXT NOT NULL,
            visited_at_utc_ms INTEGER NOT NULL,
            url TEXT NOT NULL,
            transition TEXT,
            referring_visit_id TEXT,
            is_local INTEGER CHECK (is_local IN (0, 1)),
            PRIMARY KEY (browser, profile_id, visit_id)
        );
        CREATE INDEX IF NOT EXISTS visits_by_time ON visits (visited_at_utc_ms);
        """;

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

    private const string HistoryPagesSchema = """
        CREATE TABLE IF NOT EXISTS history_page_title_observations (
            observation_id INTEGER PRIMARY KEY,
            browser TEXT NOT NULL,
            profile_id TEXT NOT NULL,
            url TEXT NOT NULL,
            title TEXT NOT NULL,
            observed_at_utc_ms INTEGER NOT NULL,
            UNIQUE (browser, profile_id, url, observed_at_utc_ms, title)
        );
        CREATE INDEX IF NOT EXISTS history_titles_by_url ON history_page_title_observations(browser,profile_id,url,observation_id);
        CREATE VIEW IF NOT EXISTS history_pages AS SELECT o.* FROM history_page_title_observations o WHERE observation_id=(
            SELECT MAX(n.observation_id) FROM history_page_title_observations n WHERE (n.browser,n.profile_id,n.url)=(o.browser,o.profile_id,o.url));
        """;

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

    private const string CaptureRunsSchema = """
        CREATE TABLE IF NOT EXISTS capture_runs (
         run_id TEXT PRIMARY KEY NOT NULL,browser TEXT NOT NULL,profile_id TEXT NOT NULL,
         scope TEXT NOT NULL CHECK(scope IN ('bookmarks','active_tabs','downloads')),
         started_at_utc_ms INTEGER NOT NULL,stopped_at_utc_ms INTEGER);
        """;

    private const string ActiveTabEventsSchema = """
        CREATE TABLE IF NOT EXISTS tab_observations (
         observation_id INTEGER PRIMARY KEY,event_id TEXT NOT NULL UNIQUE,run_id TEXT NOT NULL REFERENCES capture_runs(run_id),
         run_sequence INTEGER NOT NULL,observed_at_utc_ms INTEGER NOT NULL,event_kind TEXT NOT NULL,
         UNIQUE(run_id,run_sequence));
        CREATE TABLE IF NOT EXISTS active_tab_focus_events (
         observation_id INTEGER PRIMARY KEY REFERENCES tab_observations(observation_id),window_id INTEGER,tab_id INTEGER,
         CHECK((window_id IS NULL)=(tab_id IS NULL)));
        CREATE TABLE IF NOT EXISTS tab_url_observations (
         observation_id INTEGER PRIMARY KEY REFERENCES tab_observations(observation_id),tab_id INTEGER NOT NULL,url TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS tab_title_observations (
         observation_id INTEGER PRIMARY KEY REFERENCES tab_observations(observation_id),tab_id INTEGER NOT NULL,title TEXT NOT NULL);
        CREATE VIEW IF NOT EXISTS active_tab_history AS SELECT o.*,r.browser,r.profile_id,
         (SELECT window_id FROM active_tab_focus_events f JOIN tab_observations p USING(observation_id) WHERE p.run_id=o.run_id AND p.run_sequence<=o.run_sequence ORDER BY p.run_sequence DESC LIMIT 1) AS window_id,
         (SELECT tab_id FROM active_tab_focus_events f JOIN tab_observations p USING(observation_id) WHERE p.run_id=o.run_id AND p.run_sequence<=o.run_sequence ORDER BY p.run_sequence DESC LIMIT 1) AS tab_id
         FROM tab_observations o JOIN capture_runs r USING(run_id);
        CREATE VIEW IF NOT EXISTS active_tab_state_history AS SELECT h.*,
         (SELECT url FROM tab_url_observations f JOIN tab_observations p USING(observation_id) WHERE p.run_id=h.run_id AND f.tab_id=h.tab_id AND p.run_sequence<=h.run_sequence ORDER BY p.run_sequence DESC LIMIT 1) AS url,
         (SELECT title FROM tab_title_observations f JOIN tab_observations p USING(observation_id) WHERE p.run_id=h.run_id AND f.tab_id=h.tab_id AND p.run_sequence<=h.run_sequence ORDER BY p.run_sequence DESC LIMIT 1) AS title
         FROM active_tab_history h;
        CREATE VIEW IF NOT EXISTS current_active_tabs AS SELECT h.* FROM active_tab_state_history h WHERE observation_id=(SELECT MAX(p.observation_id) FROM tab_observations p WHERE p.run_id=h.run_id);
        """;

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

    private const string DownloadsSchema = """
        CREATE TABLE IF NOT EXISTS downloads (
         download_key INTEGER PRIMARY KEY,browser TEXT NOT NULL,profile_id TEXT NOT NULL,download_id INTEGER NOT NULL,
         UNIQUE(browser,profile_id,download_id));
        CREATE TABLE IF NOT EXISTS download_observations (
         observation_id INTEGER PRIMARY KEY,event_id TEXT NOT NULL UNIQUE,run_id TEXT NOT NULL REFERENCES capture_runs(run_id),
         run_sequence INTEGER NOT NULL,observed_at_utc_ms INTEGER NOT NULL,kind TEXT NOT NULL,
         download_key INTEGER NOT NULL REFERENCES downloads(download_key),UNIQUE(run_id,run_sequence));
        CREATE TABLE IF NOT EXISTS download_resource_observations (observation_id INTEGER PRIMARY KEY REFERENCES download_observations(observation_id),url TEXT,started_at_utc_ms INTEGER,final_url TEXT,total_bytes INTEGER,mime_type TEXT);
        CREATE TABLE IF NOT EXISTS download_state_observations (observation_id INTEGER PRIMARY KEY REFERENCES download_observations(observation_id),state TEXT,ended_at_utc_ms INTEGER);
        CREATE TABLE IF NOT EXISTS download_path_observations (observation_id INTEGER PRIMARY KEY REFERENCES download_observations(observation_id),local_path TEXT);
        CREATE INDEX IF NOT EXISTS download_observations_by_download ON download_observations(download_key,observation_id);
        CREATE VIEW IF NOT EXISTS download_history AS SELECT d.*,o.observation_id,o.run_id,o.observed_at_utc_ms,o.kind,
            (SELECT url FROM download_resource_observations f JOIN download_observations p USING(observation_id) WHERE p.download_key=d.download_key AND p.observation_id<=o.observation_id ORDER BY p.observation_id DESC LIMIT 1) AS url,
            (SELECT started_at_utc_ms FROM download_resource_observations f JOIN download_observations p USING(observation_id) WHERE p.download_key=d.download_key AND p.observation_id<=o.observation_id ORDER BY p.observation_id DESC LIMIT 1) AS started_at_utc_ms,
            (SELECT final_url FROM download_resource_observations f JOIN download_observations p USING(observation_id) WHERE p.download_key=d.download_key AND p.observation_id<=o.observation_id ORDER BY p.observation_id DESC LIMIT 1) AS final_url,
            (SELECT total_bytes FROM download_resource_observations f JOIN download_observations p USING(observation_id) WHERE p.download_key=d.download_key AND p.observation_id<=o.observation_id ORDER BY p.observation_id DESC LIMIT 1) AS total_bytes,
            (SELECT mime_type FROM download_resource_observations f JOIN download_observations p USING(observation_id) WHERE p.download_key=d.download_key AND p.observation_id<=o.observation_id ORDER BY p.observation_id DESC LIMIT 1) AS mime_type,
            (SELECT state FROM download_state_observations f JOIN download_observations p USING(observation_id) WHERE p.download_key=d.download_key AND p.observation_id<=o.observation_id ORDER BY p.observation_id DESC LIMIT 1) AS state,
            (SELECT ended_at_utc_ms FROM download_state_observations f JOIN download_observations p USING(observation_id) WHERE p.download_key=d.download_key AND p.observation_id<=o.observation_id ORDER BY p.observation_id DESC LIMIT 1) AS ended_at_utc_ms,
            (SELECT local_path FROM download_path_observations f JOIN download_observations p USING(observation_id) WHERE p.download_key=d.download_key AND p.observation_id<=o.observation_id ORDER BY p.observation_id DESC LIMIT 1) AS local_path
        FROM downloads d JOIN download_observations o USING(download_key);
        CREATE VIEW IF NOT EXISTS current_downloads AS SELECT h.* FROM download_history h WHERE observation_id=(SELECT MAX(p.observation_id) FROM download_observations p WHERE p.download_key=h.download_key);
        """;

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
