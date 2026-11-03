namespace Orisumi.Capturing;

internal static class HeartbeatCapture
{
    private const string Owner = "019d9026-def4-74db-bf9a-f854c16a993e";

    private const string Psql =
        "docker exec -i -e PGOPTIONS='-c default_transaction_read_only=on' heartbeat-db-1 psql -U heartbeat -d heartbeat -At -v ON_ERROR_STOP=1";

    public static async Task<Capture> RunAsync(Store store, string host, TimeRange range)
    {
        var migration = (await QueryAsync(host, "select max(\"MigrationId\") from \"__EFMigrationsHistory\";")).Trim();
        var writer = store.BeginCapture("heartbeat", $"ssh {host}: heartbeat-db-1 owner {Owner}", range, $"heartbeat migration {migration}");
        foreach (var (table, sql) in Queries(range))
            await Shell.ToFileAsync("ssh", [host, Psql], sql, writer.Target($"{table}.jsonl", sql));
        return writer.Complete();
    }

    private static Task<string> QueryAsync(string host, string sql) =>
        Shell.CaptureAsync("ssh", [host, $"{Psql} -c '{sql.Replace("'", "'\\''", StringComparison.Ordinal)}'"]);

    private static IEnumerable<(string Table, string Sql)> Queries(TimeRange range)
    {
        var from = Clock.Iso(range.From);
        var to = Clock.Iso(range.To);
        var segments = $"\"OwnerId\" = '{Owner}' and \"StartTime\" < '{to}' and \"EndTime\" > '{from}'";
        var identities = $"select \"AppIdentityId\" from \"Segments\" where {segments}";
        yield return ("devices", Rows("Devices", $"\"OwnerId\" = '{Owner}'", "\"Id\""));
        yield return ("subjects", Rows("Subjects", $"\"OwnerId\" = '{Owner}'", "\"SubjectId\""));
        yield return ("streams", Rows("Streams", $"\"OwnerId\" = '{Owner}'", "\"StreamId\""));
        yield return ("app-identities", Rows("AppIdentities", $"\"Id\" in ({identities})", "\"Id\""));
        yield return ("apps", Rows("Apps", $"\"Id\" in (select \"AppId\" from \"AppIdentities\" where \"Id\" in ({identities}))", "\"Id\""));
        yield return ("segments", Rows("Segments", segments, "\"StartTime\""));
        yield return ("events", Rows("Events", $"\"OwnerId\" = '{Owner}' and \"Timestamp\" >= '{from}' and \"Timestamp\" < '{to}'", "\"Timestamp\""));
        yield return ("fact-gaps", Rows("FactGaps",
            $"\"OwnerId\" = '{Owner}' and \"End\" > {Clock.Ticks(range.From)} and \"Start\" < {Clock.Ticks(range.To)}", "\"Start\""));
    }

    private static string Rows(string table, string filter, string order) =>
        $"select row_to_json(t) from \"{table}\" t where {filter} order by {order};";
}
