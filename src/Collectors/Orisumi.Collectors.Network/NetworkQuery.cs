using Orisumi.Collectors.Storage;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Orisumi.Collectors.Network;

public static class NetworkQuery
{
    public static void Write(string store, DateTimeOffset from, DateTimeOffset to, bool details, TextWriter output)
    {
        if (from >= to) throw new ArgumentException("--from must be earlier than --to.");
        var path = NetworkDatabase.PathFor(store);
        if (!File.Exists(path)) throw new FileNotFoundException("No network capture database found.", path);
        using var database = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false,
        }.ToString());
        database.Open();
        SqliteSchema.CheckVersion(database);
        using var transaction = database.BeginTransaction(deferred: true);
        List<Dictionary<string, object?>> Query(string sql)
        {
            using var command = database.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.Parameters.AddWithValue("$from", CeilingMilliseconds(from));
            command.Parameters.AddWithValue("$to", CeilingMilliseconds(to));
            using var reader = command.ExecuteReader();
            var rows = new List<Dictionary<string, object?>>();
            while (reader.Read())
            {
                var row = new Dictionary<string, object?>();
                for (var column = 0; column < reader.FieldCount; column++)
                    row.Add(reader.GetName(column), reader.IsDBNull(column) ? null : reader.GetValue(column));
                rows.Add(row);
            }
            return rows;
        }
        const string selected = """
            WITH selected AS (
                SELECT t.*, i.process_instance_id,i.protocol,i.ip_version,i.local_address,i.local_port,
                    i.remote_address,i.remote_port,i.interface,i.ambiguous,
                    (t.interval_start_utc_ms >= $from AND t.interval_end_utc_ms <= $to) AS fully_contained
                FROM socket_traffic_intervals t JOIN socket_instances i USING(socket_id)
                WHERE (t.interval_start_utc_ms < $to AND t.interval_end_utc_ms > $from)
                   OR (t.interval_start_utc_ms IS NULL AND t.interval_end_utc_ms >= $from AND t.interval_end_utc_ms < $to)
            )
            """;
        var report = new Dictionary<string, object?>
        {
            ["source"] = "nettop",
            ["from"] = from,
            ["to"] = to,
            ["runs"] = Query("""
                SELECT r.*, MIN(s.observed_at_utc_ms) AS first_sample_at_utc_ms,
                    MAX(s.observed_at_utc_ms) AS last_sample_at_utc_ms,
                    COUNT(s.sample_id) AS sample_count,
                    SUM(CASE WHEN s.error IS NOT NULL THEN 1 ELSE 0 END) AS failed_samples
                FROM capture_runs r LEFT JOIN samples s USING(run_id)
                WHERE r.started_at_utc_ms < $to
                AND COALESCE(r.stopped_at_utc_ms,(SELECT MAX(n.observed_at_utc_ms) FROM samples n WHERE n.run_id=r.run_id),r.started_at_utc_ms) >= $from
                GROUP BY r.run_id ORDER BY r.started_at_utc_ms
                """),
            ["sampling_gaps"] = Query("""
                WITH coverage AS (
                    SELECT *, LAG(observed_at_utc_ms) OVER (PARTITION BY run_id ORDER BY run_sequence) AS previous_sample_at_utc_ms
                    FROM samples
                )
                SELECT sample_id,run_id,run_sequence,previous_sample_at_utc_ms,started_at_utc_ms,observed_at_utc_ms,error
                FROM coverage WHERE (continuous=0 OR error IS NOT NULL)
                AND ((observed_at_utc_ms >= $from AND observed_at_utc_ms < $to)
                    OR (MIN(previous_sample_at_utc_ms,observed_at_utc_ms) < $to
                        AND MAX(previous_sample_at_utc_ms,observed_at_utc_ms) > $from))
                ORDER BY sample_id
                """),
            ["summary"] = Query(selected + """
                , grouped AS (
                    SELECT t.process_instance_id,t.protocol,t.remote_address,t.remote_port,t.interface,
                        COUNT(DISTINCT t.socket_id) AS observed_socket_segments,
                        MIN(t.sample_id) AS first_sample_id,MAX(t.sample_id) AS last_sample_id,
                        SUM(CASE WHEN t.fully_contained THEN t.received_delta END) AS received_bytes,
                        SUM(CASE WHEN t.fully_contained THEN t.sent_delta END) AS sent_bytes,
                        SUM(CASE WHEN t.interval_start_utc_ms IS NOT NULL AND NOT t.fully_contained THEN 1 ELSE 0 END) AS boundary_intervals,
                        SUM(CASE WHEN t.received_delta IS NULL OR t.sent_delta IS NULL THEN 1 ELSE 0 END) AS unknown_intervals,
                        MAX(t.ambiguous) AS ambiguous
                    FROM selected t
                    GROUP BY t.process_instance_id,t.protocol,t.remote_address,t.remote_port,t.interface
                )
                SELECT g.*,p.run_id,p.pid,p.started_at_utc_us,
                    (p.started_at_utc_us IS NOT NULL) AS process_identity_verified,
                    (SELECT source_name FROM process_name_observations m WHERE m.process_instance_id=p.process_instance_id
                        AND m.sample_id <= g.last_sample_id ORDER BY sample_id DESC LIMIT 1) AS source_name,
                    (SELECT executable_path FROM process_path_observations m WHERE m.process_instance_id=p.process_instance_id
                        AND m.sample_id <= g.last_sample_id ORDER BY sample_id DESC LIMIT 1) AS executable_path
                FROM grouped g JOIN process_instances p USING(process_instance_id)
                ORDER BY sent_bytes DESC,received_bytes DESC,p.process_instance_id
                """),
        };
        if (details)
        {
            report["socket_presence"] = Query("""
                SELECT * FROM socket_history WHERE observed_at_utc_ms >= $from AND observed_at_utc_ms < $to
                ORDER BY sample_id,socket_id
                """);
            report["socket_states"] = Query("""
                SELECT t.*,s.run_id,s.run_sequence,s.observed_at_utc_ms
                FROM socket_state_observations t JOIN samples s USING(sample_id)
                WHERE s.observed_at_utc_ms >= $from AND s.observed_at_utc_ms < $to
                ORDER BY t.sample_id,t.socket_id
                """);
            report["socket_intervals"] = Query(selected + "SELECT * FROM selected ORDER BY sample_id,socket_id");
            report["process_intervals"] = Query("""
                SELECT * FROM process_traffic_intervals
                WHERE (interval_start_utc_ms < $to AND interval_end_utc_ms > $from)
                   OR (interval_start_utc_ms IS NULL AND interval_end_utc_ms >= $from AND interval_end_utc_ms < $to)
                ORDER BY sample_id,process_instance_id
                """);
        }
        transaction.Commit();
        output.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static long CeilingMilliseconds(DateTimeOffset value) => value.ToUnixTimeMilliseconds() +
        (value.UtcTicks % TimeSpan.TicksPerMillisecond == 0 ? 0 : 1);
}
