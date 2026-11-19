using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Orisumi.Collectors.Storage;

namespace Orisumi.Collectors.Browser;

internal sealed class BrowserDatabase : IDisposable
{
    private const string Source = "browser";
    private readonly CaptureWriter writer;
    private readonly string profile;
    private readonly string queue;
    private readonly string browserSession;

    internal BrowserDatabase(string store, Hello hello, long now)
    {
        if (hello.ProfileId == Guid.Empty || hello.QueueId == Guid.Empty || hello.BrowserSession == Guid.Empty)
            throw new InvalidDataException("invalid browser connection identity");
        profile = hello.ProfileId.ToString();
        queue = hello.QueueId.ToString();
        browserSession = hello.BrowserSession.ToString();
        writer = new CaptureWriter(store, Source, (command, run) =>
        {
            command.Execute("INSERT INTO browser_runs VALUES($p0,'chrome',$p1,$p2)", run, profile, browserSession);
            command.Execute("INSERT OR IGNORE INTO _history_cursors VALUES($p0,$p1,$p1,$p1)", profile, now);
        }, lockIdentity: profile);
    }

    internal sealed record Hello(Guid ProfileId, Guid QueueId, Guid BrowserSession);
    internal sealed record Delivery(long Sequence, long ObservedAtUtcMs, Guid BrowserSession,
        string Scope, string Kind, JsonElement Data);
    internal sealed record Reply(bool Ok = true, long? Sequence = null, HistoryDatabase.Range? History = null, string? Error = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Rejected = false);

    internal Reply Receive(JsonElement payload, long now, out bool polledTabs)
    {
        polledTabs = false;
        try
        {
            var entry = payload.Deserialize<Delivery>(JsonSerializerOptions.Web)!;
            var reply = Receive(entry, now);
            polledTabs = !reply.Rejected && entry is { Scope: "tabs", Kind: "snapshot" } &&
                entry.BrowserSession.ToString() == browserSession;
            return reply;
        }
        catch (Exception error) when (PermanentError(error) || error is IOException { InnerException: { } inner } && PermanentError(inner))
        {
            if (payload.Field("sequence") is not { ValueKind: JsonValueKind.Number } number ||
                !number.TryGetInt64(out var sequence) || sequence <= 0) throw;
            var rejection = error is IOException ? error.InnerException! : error;
            var scope = payload.Text("scope");
            if (scope is not ("tabs" or "bookmarks" or "downloads" or "history")) scope = Source;
            var observedAt = payload.Field("observedAtUtcMs");
            var time = observedAt.ValueKind == JsonValueKind.Number && observedAt.TryGetInt64(out var timestamp) && timestamp > 0
                ? timestamp : now;
            return writer.Run((command, run) =>
            {
                command.Execute("""
                    INSERT INTO browser_rejected_deliveries(run_id,profile_id,queue_id,sequence,scope,observed_at_utc_ms,payload_json,error)
                    VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7)
                    ON CONFLICT(profile_id,queue_id,sequence) DO NOTHING
                    """, run, profile, queue, sequence, scope, time, payload.GetRawText(), rejection.Message);
                ConfirmDelivery(command, sequence);
                return new Reply(Sequence: sequence, Rejected: true);
            }, confirm: false);
        }
    }

    private static bool PermanentError(Exception error) => error is InvalidDataException or JsonException or FormatException;

    private Reply Receive(Delivery entry, long now)
    {
        if (entry.Sequence <= 0 || entry.ObservedAtUtcMs <= 0 || entry.BrowserSession == Guid.Empty ||
            entry.Scope is not ("tabs" or "bookmarks" or "downloads" or "history") || string.IsNullOrEmpty(entry.Kind))
            throw new InvalidDataException("invalid browser delivery");

        var reply = writer.Run((command, run) =>
        {
            var change = new Observation(command, run, profile, queue, entry);
            var last = (long?)command.Scalar("SELECT sequence FROM _browser_deliveries WHERE profile_id=$p0 AND queue_id=$p1", profile, queue);
            if (entry.Sequence <= last && command.Scalar("""
                SELECT 1 FROM browser_rejected_deliveries WHERE profile_id=$p0 AND queue_id=$p1 AND sequence=$p2
                """, profile, queue, entry.Sequence) is not null)
                return new Reply(Sequence: entry.Sequence, Rejected: true);
            if (entry.Sequence > (last ?? 0))
            {
                if (entry.Kind == "read_failed")
                {
                    command.Execute("INSERT INTO capture_read_failures(run_id,observed_at_utc_ms,error) VALUES($p0,$p1,$p2)",
                        run, entry.ObservedAtUtcMs, entry.Scope + ": " + (entry.Data.ValueKind == JsonValueKind.String
                            ? entry.Data.GetString() : throw new InvalidDataException("invalid browser read failure")));
                }
                else
                {
                    switch (entry.Scope)
                    {
                        case "tabs": TabDatabase.Capture(change); break;
                        case "bookmarks": BookmarkDatabase.Capture(change); break;
                        case "downloads": DownloadDatabase.Capture(change); break;
                        case "history": HistoryDatabase.Capture(change); break;
                    }
                }
                ConfirmDelivery(command, entry.Sequence);
            }
            if (entry.Kind != "read_failed")
                command.Execute("UPDATE capture_runs SET confirmed_until_utc_ms=$p0 WHERE run_id=$p1", now, run);
            return new Reply(Sequence: entry.Sequence, History: entry is { Scope: "history", Kind: "range" }
                ? HistoryDatabase.Next(command, profile, now) : null);
        }, confirm: false);

        return reply;
    }

    private void ConfirmDelivery(SqliteCommand command, long sequence) =>
        command.Execute("""
            INSERT INTO _browser_deliveries VALUES($p0,$p1,$p2)
            ON CONFLICT(profile_id,queue_id) DO UPDATE SET sequence=MAX(sequence,excluded.sequence)
            """, profile, queue, sequence);

    internal Reply PollHistory(long now) =>
        writer.Run((command, _) => new Reply(History: HistoryDatabase.Next(command, profile, now)), confirm: false);

    internal void SnapshotTimeout(long started) => writer.Run((command, run) =>
        command.Execute("""
            INSERT INTO capture_channel_events(run_id,scope,mode,started_at_utc_ms,evidence)
            VALUES($p0,'tabs','snapshot',$p1,'snapshot_timeout')
            """, run, started), confirm: false);

    internal void Stop(long now, Exception? error)
    {
        try
        {
            writer.Run((command, run) => command.Execute("""
                UPDATE capture_channel_events SET stopped_at_utc_ms=MAX(started_at_utc_ms,$p0)
                WHERE run_id=$p1 AND evidence='snapshot_timeout' AND stopped_at_utc_ms IS NULL
                """, now, run), confirm: false);
        }
        finally { writer.Stop(error); }
    }

    public void Dispose() => writer.Dispose();

    internal sealed class Observation(SqliteCommand command, long run, string profile, string queue, Delivery entry)
    {
        internal SqliteCommand Command => command;
        internal long Run => run;
        internal string Profile => profile;
        internal string Session { get; } = entry.BrowserSession.ToString();
        internal string EventId => queue + ":" + entry.Sequence;
        internal long Sequence => entry.Sequence;
        internal long Time => entry.ObservedAtUtcMs;
        internal string Kind => entry.Kind;
        internal JsonElement Data => entry.Data;
        internal bool IsSnapshot => Kind == "snapshot";

        // Scope progress is persisted separately from the last known values in the fact tables.
        internal bool Snapshot(bool differs)
        {
            command.CommandText = """
                SELECT run_id,observed_at_utc_ms,browser_session FROM _browser_snapshots
                WHERE profile_id=$p0 AND scope=$p1
                """;
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$p0", profile);
            command.Parameters.AddWithValue("$p1", entry.Scope);
            (long Run, long Time, string Session)? previous = null;
            using (var row = command.ExecuteReader())
                if (row.Read()) previous = (row.GetInt64(0), row.GetInt64(1), row.GetString(2));

            var reconciles = differs && previous is { } last && (entry.Scope != "tabs" || last.Session == Session);
            var resumed = entry.Scope == "tabs" && command.Execute("""
                UPDATE capture_channel_events SET stopped_at_utc_ms=MAX(started_at_utc_ms,$p0)
                WHERE run_id=$p1 AND evidence='snapshot_timeout' AND stopped_at_utc_ms IS NULL
                """, Time, run) > 0;
            if (reconciles && !resumed)
                Gap(entry.Scope, "snapshot", "reconciliation_changed", Math.Min(previous!.Value.Time, Time));
            command.Execute("""
                INSERT INTO _browser_snapshots VALUES($p0,$p1,$p2,$p3,$p4)
                ON CONFLICT(profile_id,scope) DO UPDATE SET browser_session=excluded.browser_session,
                    run_id=excluded.run_id, observed_at_utc_ms=excluded.observed_at_utc_ms
                """, profile, entry.Scope, Session, run, Time);
            return previous?.Run != run || previous?.Session != Session && entry.Scope == "tabs" || reconciles || resumed;
        }

        internal void Gap(string scope, string mode, string evidence, long from) =>
            command.Execute("""
                INSERT INTO capture_channel_events(run_id,scope,mode,started_at_utc_ms,stopped_at_utc_ms,evidence)
                VALUES($p0,$p1,$p2,$p3,$p4,$p5)
                """, run, scope, mode, from, Time, evidence);
    }
}

internal static class ChromeValue
{
    internal static JsonElement Field(this JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field) ? field : default;
    internal static bool Has(this JsonElement value, string name) => value.Field(name).ValueKind != JsonValueKind.Undefined;
    internal static string? Text(this JsonElement value, string name) => value.Field(name) is { ValueKind: JsonValueKind.String } field ? field.GetString() : null;
    internal static int? Integer(this JsonElement value, string name) => value.Field(name) is { ValueKind: JsonValueKind.Number } field ? field.GetInt32() : null;
    internal static long RequiredInteger(this JsonElement value, string name) =>
        value.Field(name) is { ValueKind: JsonValueKind.Number } field
            ? field.GetInt64() : throw new InvalidDataException($"missing Chrome {name}");

    internal static JsonElement.ArrayEnumerator Elements(this JsonElement value) =>
        value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : throw new InvalidDataException("expected Chrome array");

    internal static bool? Flag(this JsonElement value, string name) => value.Field(name).ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };
}
