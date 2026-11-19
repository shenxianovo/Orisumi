using System.Text.Json;
using Microsoft.Data.Sqlite;
using Orisumi.Collectors.Storage;
using Observation = Orisumi.Collectors.Browser.BrowserDatabase.Observation;

namespace Orisumi.Collectors.Browser;

internal static class HistoryDatabase
{
    private const long Minute = 60_000;
    internal sealed record Range(string Direction, long Cursor, long StartTime, long EndTime);

    internal static Range? Next(SqliteCommand command, string profile, long now)
    {
        var forward = Cursor(command, profile, "forward_until_utc_ms");
        if (now - forward >= Minute / 2) return new("forward", forward, Math.Max(0, forward - Minute), Math.Min(now, forward + 60 * Minute));
        var before = Cursor(command, profile, "backfill_before_utc_ms");
        return before > 0 ? new("backfill", before, 0, before) : null;
    }

    internal static void Capture(Observation observation)
    {
        if (observation.Kind == "removed")
        {
            observation.Gap("history", "replay", "source_records_removed", observation.Time);
            return;
        }
        if (observation.Kind != "range") throw new InvalidDataException("invalid history delivery");
        var data = observation.Data;
        var direction = data.Text("direction");
        var column = direction switch
        {
            "forward" => "forward_until_utc_ms",
            "backfill" => "backfill_before_utc_ms",
            _ => throw new InvalidDataException("invalid history range direction"),
        };
        var command = observation.Command;
        var profile = observation.Profile;
        var expected = data.RequiredInteger("cursor");
        var start = data.RequiredInteger("startTime");
        var end = data.RequiredInteger("endTime");
        var current = Cursor(command, profile, column);
        // A queued range from a replaced store can still contribute retained facts, but cannot
        // advance a cursor from a different position.
        var forward = direction == "forward";
        if (start < 0 || end < start || expected <= 0 ||
            (forward ? start != Math.Max(0, expected - Minute) || end <= expected : end != expected || start >= expected))
            throw new InvalidDataException("invalid history range boundaries");

        foreach (var page in data.Field("pages").Elements())
        {
            var url = page.Text("url");
            if (string.IsNullOrEmpty(url)) throw new InvalidDataException("missing history URL");
            var title = page.Text("title");
            if (!string.IsNullOrWhiteSpace(title))
                command.Execute("""
                    INSERT INTO history_page_title_observations(browser,profile_id,url,title,observed_at_utc_ms)
                    SELECT 'chrome',$p0,$p1,$p2,$p3
                    WHERE NOT EXISTS (SELECT 1 FROM history_pages WHERE browser='chrome' AND profile_id=$p0 AND url=$p1
                        AND (title=$p2 OR observed_at_utc_ms>$p3))
                    """, profile, url, title, observation.Time);
            foreach (var visit in page.Field("visits").Elements())
            {
                var visitId = visit.Text("visitId");
                if (string.IsNullOrEmpty(visitId) || visit.Field("visitTime") is not { ValueKind: JsonValueKind.Number } time || time.GetDouble() < 1)
                    throw new InvalidDataException("invalid Chrome visit");
                var referrer = visit.Text("referringVisitId");
                command.Execute("""
                    INSERT INTO visits(browser,profile_id,visit_id,visited_at_utc_ms,url,transition,referring_visit_id,is_local)
                    VALUES('chrome',$p0,$p1,$p2,$p3,$p4,$p5,$p6)
                    ON CONFLICT(browser,profile_id,visit_id) DO UPDATE SET
                        transition=COALESCE(visits.transition,excluded.transition),
                        referring_visit_id=COALESCE(visits.referring_visit_id,excluded.referring_visit_id),
                        is_local=COALESCE(visits.is_local,excluded.is_local)
                    WHERE (visits.transition IS NULL AND excluded.transition IS NOT NULL)
                       OR (visits.referring_visit_id IS NULL AND excluded.referring_visit_id IS NOT NULL)
                       OR (visits.is_local IS NULL AND excluded.is_local IS NOT NULL)
                    """, profile, visitId, (long)time.GetDouble(), url, visit.Text("transition"),
                    referrer switch { "0" => "", "" => null, _ => referrer }, visit.Flag("isLocal"));
            }
        }
        if (current == expected)
            command.Execute($"UPDATE _history_cursors SET {column}=$p0 WHERE profile_id=$p1", forward ? end : start, profile);
    }

    private static long Cursor(SqliteCommand command, string profile, string column) =>
        (long)command.Scalar($"SELECT {column} FROM _history_cursors WHERE profile_id=$p0", profile)!;
}
