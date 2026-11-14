using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Orisumi.Cli.Capture;

internal static class CaptureCoverage
{
    internal static void Write(string store, DateTimeOffset from, DateTimeOffset to, TextWriter output)
    {
        if (from >= to) throw new ArgumentException("--from 必须早于 --to。");
        var sources = new List<object>();
        foreach (var source in new[] { "claude", "codex", "browser", "macos", "network" })
        {
            var path = Path.GetFullPath(Path.Combine(store, "raw", source + ".sqlite"));
            if (!File.Exists(path))
            {
                sources.Add(new { source, status = "no_database", evidence = Array.Empty<object>() });
                continue;
            }
            using var database = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false,
            }.ToString());
            database.Open();
            using var command = database.CreateCommand();
            command.CommandText = """
                SELECT *
                FROM capture_coverage
                WHERE started_at_utc_ms < $to
                  AND (stopped_at_utc_ms IS NULL OR stopped_at_utc_ms > $from
                       OR (stopped_at_utc_ms=started_at_utc_ms AND started_at_utc_ms >= $from))
                ORDER BY started_at_utc_ms,scope,evidence
                """;
            command.Parameters.AddWithValue("$from", CeilingMilliseconds(from));
            command.Parameters.AddWithValue("$to", CeilingMilliseconds(to));
            using var reader = command.ExecuteReader();
            var rows = new List<Dictionary<string, object?>>();
            while (reader.Read())
            {
                var row = new Dictionary<string, object?>();
                for (var i = 0; i < reader.FieldCount; i++)
                    row.Add(reader.GetName(i), reader.IsDBNull(i) ? null : reader.GetValue(i));
                rows.Add(row);
            }
            sources.Add(new { source, status = "queried", evidence = rows });
        }
        output.WriteLine(JsonSerializer.Serialize(new { from, to, sources }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static long CeilingMilliseconds(DateTimeOffset value)
    {
        var ticks = value.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks;
        var milliseconds = Math.DivRem(ticks, TimeSpan.TicksPerMillisecond, out var remainder);
        return milliseconds + (remainder > 0 ? 1 : 0);
    }
}
