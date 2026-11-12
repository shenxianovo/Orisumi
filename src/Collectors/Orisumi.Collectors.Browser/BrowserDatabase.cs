using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Orisumi.Collectors.Browser;

internal static class BrowserDatabase
{
    private const string Schema = VisitsSchema + "\n" + ActiveTabEventsSchema + "\n" +
        BookmarksSchema + "\n" + DownloadsSchema;

    internal static async Task<Reply> HandleMessage(string store, ReadOnlyMemory<byte> message)
    {
        var batch = JsonSerializer.Deserialize<Batch>(message.Span, JsonSerializerOptions.Web);
        if (batch is null || batch.ProfileId == Guid.Empty)
            throw new InvalidDataException("invalid browser batch profile");

        if (batch is { GetDatabaseId: true, Visits: null, ActiveTabEvents: null, Bookmarks: null, Downloads: null })
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

        // Hex encoding keeps the batch separate from the SQL syntax.
        var payload = $"CAST(X'{Convert.ToHexString(JsonSerializer.SerializeToUtf8Bytes(batch, JsonSerializerOptions.Web))}' AS TEXT)";
        var write = (batch.Visits, batch.ActiveTabEvents, batch.Bookmarks, batch.Downloads) switch
        {
            ({ } visits, null, null, null) => WriteVisits(batch.ProfileId, payload, visits),
            (null, { } events, null, null) => WriteActiveTabEvents(batch.ProfileId, payload, events),
            (null, null, { } bookmarks, null) => WriteBookmarks(batch.ProfileId, payload, bookmarks),
            (null, null, null, { } downloads) => WriteDownloads(batch.ProfileId, payload, downloads),
            _ => throw new InvalidDataException("invalid browser batch"),
        };
        var result = await Execute(store, $"""
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

    private sealed record Batch(Guid ProfileId, Visit?[]? Visits, ActiveTabEvent?[]? ActiveTabEvents,
        Bookmark?[]? Bookmarks, Download?[]? Downloads, bool GetDatabaseId);

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

    private const string ActiveTabEventsSchema = """
        CREATE TABLE IF NOT EXISTS active_tab_events (
            browser TEXT NOT NULL,
            profile_id TEXT NOT NULL,
            event_id TEXT NOT NULL,
            capture_session_id TEXT NOT NULL,
            session_sequence INTEGER NOT NULL CHECK (session_sequence > 0),
            observed_at_utc_ms INTEGER NOT NULL,
            event_kind TEXT NOT NULL CHECK (event_kind IN ('snapshot', 'tab_activated', 'url_changed', 'window_focused', 'window_blurred')),
            window_id INTEGER,
            tab_id INTEGER,
            url TEXT,
            title TEXT,
            PRIMARY KEY (browser, profile_id, event_id),
            UNIQUE (browser, profile_id, capture_session_id, session_sequence),
            CHECK ((window_id IS NULL) = (tab_id IS NULL))
        );
        CREATE INDEX IF NOT EXISTS active_tab_events_by_time ON active_tab_events (observed_at_utc_ms);
        """;

    private static string WriteActiveTabEvents(Guid profileId, string payload, ActiveTabEvent?[] entries)
    {
        if (!entries.All(IsValid))
            throw new InvalidDataException("invalid activetabevents batch");

        return $"""
            INSERT INTO active_tab_events (browser, profile_id, event_id, capture_session_id, session_sequence,
                                           observed_at_utc_ms, event_kind, window_id, tab_id, url, title)
            SELECT 'chrome', '{profileId:D}', value ->> '$.eventId', value ->> '$.captureSessionId',
                   value ->> '$.sessionSequence', value ->> '$.observedAtUtcMs', value ->> '$.eventKind',
                   value ->> '$.windowId', value ->> '$.tabId', value ->> '$.url', value ->> '$.title'
            FROM json_each({payload}, '$.activeTabEvents')
            WHERE true
            ON CONFLICT (browser, profile_id, event_id) DO NOTHING;
            """;
    }

    private static bool IsValid(ActiveTabEvent? entry)
    {
        if (entry is not { SessionSequence: > 0, ObservedAtUtcMs: > 0 } ||
            entry.EventId == Guid.Empty || entry.CaptureSessionId == Guid.Empty)
            return false;

        var hasTab = entry is { WindowId: >= 0, TabId: >= 0 };
        var hasNoTab = entry is { WindowId: null, TabId: null, Url: null, Title: null };
        return entry.EventKind switch
        {
            "snapshot" => hasTab || hasNoTab,
            "tab_activated" or "url_changed" or "window_focused" => hasTab,
            "window_blurred" => hasNoTab,
            _ => false,
        };
    }

    private sealed record ActiveTabEvent(Guid EventId, Guid CaptureSessionId, long SessionSequence,
        long ObservedAtUtcMs, string? EventKind, int? WindowId, int? TabId, string? Url, string? Title);

    private const string BookmarksSchema = """
        CREATE TABLE IF NOT EXISTS bookmarks (
            browser TEXT NOT NULL,
            profile_id TEXT NOT NULL,
            bookmark_id TEXT NOT NULL,
            parent_id TEXT,
            position INTEGER,
            title TEXT NOT NULL,
            url TEXT,
            added_at_utc_ms INTEGER,
            last_used_at_utc_ms INTEGER,
            PRIMARY KEY (browser, profile_id, bookmark_id)
        );
        """;

    private static string WriteBookmarks(Guid profileId, string payload, Bookmark?[] entries)
    {
        if (!entries.All(bookmark =>
                bookmark is { BookmarkId.Length: > 0, Title: not null }))
            throw new InvalidDataException("invalid bookmarks batch");

        return $"""
            CREATE TEMP TABLE incoming_bookmarks AS
            SELECT value ->> '$.bookmarkId' AS bookmark_id, value ->> '$.parentId' AS parent_id,
                   value ->> '$.position' AS position, value ->> '$.title' AS title,
                   value ->> '$.url' AS url, value ->> '$.addedAtUtcMs' AS added_at_utc_ms,
                   value ->> '$.lastUsedAtUtcMs' AS last_used_at_utc_ms
            FROM json_each({payload}, '$.bookmarks');
            CREATE UNIQUE INDEX incoming_bookmarks_by_id ON incoming_bookmarks (bookmark_id);
            DELETE FROM bookmarks
            WHERE browser = 'chrome' AND profile_id = '{profileId:D}'
              AND NOT EXISTS (SELECT 1 FROM incoming_bookmarks WHERE incoming_bookmarks.bookmark_id = bookmarks.bookmark_id);
            INSERT INTO bookmarks (browser, profile_id, bookmark_id, parent_id, position, title, url,
                                   added_at_utc_ms, last_used_at_utc_ms)
            SELECT 'chrome', '{profileId:D}', bookmark_id, parent_id, position, title, url,
                   added_at_utc_ms, last_used_at_utc_ms
            FROM incoming_bookmarks WHERE true
            ON CONFLICT (browser, profile_id, bookmark_id) DO UPDATE SET
                parent_id = excluded.parent_id, position = excluded.position, title = excluded.title,
                url = excluded.url, added_at_utc_ms = excluded.added_at_utc_ms, last_used_at_utc_ms = excluded.last_used_at_utc_ms
            WHERE (bookmarks.parent_id, bookmarks.position, bookmarks.title, bookmarks.url,
                   bookmarks.added_at_utc_ms, bookmarks.last_used_at_utc_ms) IS NOT
                  (excluded.parent_id, excluded.position, excluded.title, excluded.url,
                   excluded.added_at_utc_ms, excluded.last_used_at_utc_ms);
            """;
    }

    private sealed record Bookmark(string? BookmarkId, string? ParentId, int? Position, string? Title,
        string? Url, long? AddedAtUtcMs, long? LastUsedAtUtcMs);

    private const string DownloadsSchema = """
        CREATE TABLE IF NOT EXISTS downloads (
            browser TEXT NOT NULL,
            profile_id TEXT NOT NULL,
            download_id INTEGER NOT NULL,
            url TEXT NOT NULL,
            final_url TEXT,
            filename TEXT,
            started_at_utc_ms INTEGER NOT NULL,
            ended_at_utc_ms INTEGER,
            state TEXT NOT NULL CHECK (state IN ('in_progress', 'interrupted', 'complete')),
            total_bytes INTEGER,
            mime_type TEXT,
            PRIMARY KEY (browser, profile_id, download_id)
        );
        """;

    private static string WriteDownloads(Guid profileId, string payload, Download?[] entries)
    {
        if (!entries.All(download =>
                download is { DownloadId: >= 0, Url.Length: > 0, StartedAtUtcMs: > 0,
                State: "in_progress" or "interrupted" or "complete" }))
            throw new InvalidDataException("invalid downloads batch");

        return $"""
            INSERT INTO downloads (browser, profile_id, download_id, url, final_url, filename,
                                   started_at_utc_ms, ended_at_utc_ms, state, total_bytes, mime_type)
            SELECT 'chrome', '{profileId:D}', value ->> '$.downloadId', value ->> '$.url',
                   value ->> '$.finalUrl', value ->> '$.filename', value ->> '$.startedAtUtcMs',
                   value ->> '$.endedAtUtcMs', value ->> '$.state', value ->> '$.totalBytes', value ->> '$.mimeType'
            FROM json_each({payload}, '$.downloads')
            WHERE true
            ON CONFLICT (browser, profile_id, download_id) DO UPDATE SET
                url = excluded.url, final_url = excluded.final_url, filename = excluded.filename,
                started_at_utc_ms = excluded.started_at_utc_ms, ended_at_utc_ms = excluded.ended_at_utc_ms,
                state = excluded.state, total_bytes = excluded.total_bytes, mime_type = excluded.mime_type
            WHERE (downloads.url, downloads.final_url, downloads.filename, downloads.started_at_utc_ms,
                   downloads.ended_at_utc_ms, downloads.state, downloads.total_bytes, downloads.mime_type) IS NOT
                  (excluded.url, excluded.final_url, excluded.filename, excluded.started_at_utc_ms,
                   excluded.ended_at_utc_ms, excluded.state, excluded.total_bytes, excluded.mime_type);
            """;
    }

    private sealed record Download(long? DownloadId, string? Url, string? FinalUrl, string? Filename,
        long StartedAtUtcMs, long? EndedAtUtcMs, string? State, long? TotalBytes, string? MimeType);
}
