using System.Globalization;
using System.Text.Json;
using Orisumi.Capture.Storage;
using Observation = Orisumi.Collectors.Browser.BrowserDatabase.Observation;

namespace Orisumi.Collectors.Browser;

internal static class DownloadDatabase
{
    private sealed record Response(string? Url, long? Bytes, string? Mime);
    private sealed record Status(string? State, long? End);
    private sealed record PathValue(string? Path);
    private sealed record Download(long Key = 0, bool? Listed = null, string? Url = null, long? Start = null,
        Response? Response = null, Status? Status = null, PathValue? Path = null);

    internal static void Capture(Observation observation)
    {
        var command = observation.Command;
        var data = observation.Data;
        var snapshot = observation.IsSnapshot;
        var id = data.Integer("id");
        if (!snapshot && (id is null || id < 0)) throw new InvalidDataException("missing download ID");

        command.CommandText = """
            SELECT d.download_id,d.download_key,d.url,d.started_at_utc_ms,
              (SELECT kind NOT IN ('erased','snapshot_missing') FROM download_observations o WHERE o.download_key=d.download_key
               AND kind<>'changed' ORDER BY observation_id DESC LIMIT 1) AS listed,
              p.observation_id,p.final_url,p.total_bytes,p.mime_type,
              s.observation_id,s.state,s.ended_at_utc_ms,
              f.observation_id,f.local_path
            FROM downloads d
            LEFT JOIN download_response_observations p ON p.observation_id=(
                SELECT o.observation_id FROM download_observations o JOIN download_response_observations USING(observation_id)
                WHERE o.download_key=d.download_key ORDER BY o.observation_id DESC LIMIT 1)
            LEFT JOIN download_state_observations s ON s.observation_id=(
                SELECT o.observation_id FROM download_observations o JOIN download_state_observations USING(observation_id)
                WHERE o.download_key=d.download_key ORDER BY o.observation_id DESC LIMIT 1)
            LEFT JOIN download_path_observations f ON f.observation_id=(
                SELECT o.observation_id FROM download_observations o JOIN download_path_observations USING(observation_id)
                WHERE o.download_key=d.download_key ORDER BY o.observation_id DESC LIMIT 1)
            WHERE d.browser='chrome' AND d.profile_id=$p0
            """;
        if (!snapshot) command.CommandText += " AND d.download_id=$p1";
        command.Parameters.Clear();
        command.Parameters.AddWithValue("$p0", observation.Profile);
        if (!snapshot) command.Parameters.AddWithValue("$p1", id!.Value);
        var before = new Dictionary<int, Download>();
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                string? Text(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
                long? Number(int i) => reader.IsDBNull(i) ? null : reader.GetInt64(i);
                before.Add(reader.GetInt32(0), new Download(reader.GetInt64(1), Number(4) is long listed ? listed == 1 : null,
                    Text(2), Number(3), Number(5) is null ? null : new Response(Text(6), Number(7), Text(8)),
                    Number(9) is null ? null : new Status(Text(10), Number(11)), Number(12) is null ? null : new PathValue(Text(13))));
            }

        Download Observe(int downloadId, JsonElement item, bool full)
        {
            var old = before.GetValueOrDefault(downloadId) ?? new Download();
            if (observation.Kind == "erased") return old with { Listed = false };
            JsonElement Field(string name)
            {
                var value = item.Field(name);
                return full || value.ValueKind == JsonValueKind.Undefined ? value :
                    value.Has("current") ? value.Field("current") : JsonNull;
            }
            string? Text(string name, string? fallback = null) => Field(name) switch
            {
                { ValueKind: JsonValueKind.String } value => value.GetString(),
                { ValueKind: JsonValueKind.Undefined } => fallback,
                _ => null,
            };
            long? Bytes(long? fallback) => Field("totalBytes") switch
            {
                { ValueKind: JsonValueKind.Number } value when value.GetDouble() >= 0 => (long)value.GetDouble(),
                { ValueKind: JsonValueKind.Undefined } => fallback,
                _ => null,
            };
            bool Has(string name) => full || item.Has(name);
            var response = Has("finalUrl") || Has("totalBytes") || Has("mime")
                ? new Response(Text("finalUrl", old.Response?.Url), Bytes(old.Response?.Bytes), Text("mime", old.Response?.Mime))
                : old.Response;
            var state = Text("state", old.Status?.State);
            var end = Has("endTime") ? Time(Text("endTime")) : old.Status?.End;
            var status = Has("state") || Has("endTime") ? new Status(state, state == "in_progress" ? null : end) : old.Status;
            var path = Has("filename") ? new PathValue(Text("filename")) : old.Path;
            return old with { Listed = full ? true : old.Listed, Url = old.Url ?? Text("url"),
                Start = old.Start ?? Time(Text("startTime")), Response = response, Status = status, Path = path };
        }
        var after = new Dictionary<int, Download>();
        switch (observation.Kind)
        {
            case "snapshot":
                foreach (var item in data.Elements())
                {
                    var itemId = item.Integer("id") ?? throw new InvalidDataException("missing download ID");
                    after.Add(itemId, Observe(itemId, item, full: true));
                }
                foreach (var (itemId, previous) in before)
                    if (previous.Listed == true) after.TryAdd(itemId, previous with { Listed = false });
                break;
            case "created":
            case "changed":
            case "erased":
                after.Add(id!.Value, Observe(id.Value, data, full: observation.Kind == "created"));
                break;
            default: throw new InvalidDataException("invalid download event");
        }
        var differs = after.Any(pair => !before.TryGetValue(pair.Key, out var old) ||
            pair.Value.Listed != old.Listed || pair.Value.Response != old.Response || pair.Value.Status != old.Status ||
            pair.Value.Path != old.Path);
        var baseline = snapshot && observation.Snapshot(differs);

        foreach (var (downloadId, value) in after)
        {
            var old = before.GetValueOrDefault(downloadId) ?? new Download();
            var initial = value.Listed == true && baseline;
            if (!initial && old == value) continue;
            var key = (long)command.Scalar("""
                INSERT INTO downloads(browser,profile_id,download_id,url,started_at_utc_ms) VALUES('chrome',$p0,$p1,$p2,$p3)
                ON CONFLICT(browser,profile_id,download_id) DO UPDATE SET
                    url=COALESCE(downloads.url,excluded.url),started_at_utc_ms=COALESCE(downloads.started_at_utc_ms,excluded.started_at_utc_ms)
                RETURNING download_key
                """, observation.Profile, downloadId, value.Url, value.Start)!;
            var kind = snapshot && value.Listed == false ? "snapshot_missing" : observation.Kind;
            var observed = (long)command.Scalar("""
                INSERT INTO download_observations(event_id,run_id,run_sequence,observed_at_utc_ms,kind,download_key)
                VALUES($p0,$p1,$p2,$p3,$p4,$p5) RETURNING observation_id
                """, observation.EventId + ":" + downloadId, observation.Run, observation.Sequence, observation.Time, kind, key)!;
            if (value.Response is { } response && (initial || response != old.Response))
                command.Execute("INSERT INTO download_response_observations VALUES($p0,$p1,$p2,$p3)", observed, response.Url, response.Bytes, response.Mime);
            if (value.Status is { } status && (initial || status != old.Status))
                command.Execute("INSERT INTO download_state_observations VALUES($p0,$p1,$p2)", observed, status.State, status.End);
            if (value.Path is { } path && (initial || path != old.Path))
                command.Execute("INSERT INTO download_path_observations VALUES($p0,$p1)", observed, path.Path);
        }
    }

    private static long? Time(string? value) => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
        DateTimeStyles.RoundtripKind, out var parsed) ? parsed.ToUnixTimeMilliseconds() : null;

    private static readonly JsonElement JsonNull = JsonElement.Parse("null");
}
