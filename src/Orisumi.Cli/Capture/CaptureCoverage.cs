using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Orisumi.Platform;

namespace Orisumi.Cli.Capture;

internal static class CaptureCoverage
{
    internal static void Write(string store, DateTimeOffset from, DateTimeOffset to, IEnumerable<string> installed, TextWriter output)
    {
        if (from >= to) throw new ArgumentException("--from 必须早于 --to。");
        using var bytes = new MemoryStream();
        using var json = new Utf8JsonWriter(bytes, new JsonWriterOptions { Indented = true });
        json.WriteStartObject();
        json.WriteString("from", from);
        json.WriteString("to", to);
        json.WriteStartArray("sources");
        var location = new Store(store);
        foreach (var source in location.Sources().Union(installed).Order(StringComparer.Ordinal))
        {
            var paths = location.Databases(source);
            if (paths.Count == 0) WriteSource(source, null);
            foreach (var path in paths) WriteSource(source, path);
        }
        json.WriteEndArray();
        json.WriteEndObject();
        json.Flush();
        output.WriteLine(Encoding.UTF8.GetString(bytes.GetBuffer(), 0, (int)bytes.Length));

        void WriteSource(string source, string? path)
        {
            json.WriteStartObject();
            json.WriteString("source", source);
            json.WriteString("database", Path.GetFileNameWithoutExtension(path));
            json.WriteString("status", path is null ? "no_database" : "queried");
            json.WriteStartArray("evidence");
            if (path is not null) Query(json, path, from, to);
            json.WriteEndArray();
            json.WriteEndObject();
        }
    }

    private static void Query(Utf8JsonWriter json, string path, DateTimeOffset from, DateTimeOffset to)
    {
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
        while (reader.Read())
        {
            json.WriteStartObject();
            for (var i = 0; i < reader.FieldCount; i++)
            {
                json.WritePropertyName(reader.GetName(i));
                switch (reader.GetValue(i))
                {
                    case DBNull: json.WriteNullValue(); break;
                    case long integer: json.WriteNumberValue(integer); break;
                    case double real: json.WriteNumberValue(real); break;
                    case byte[] blob: json.WriteBase64StringValue(blob); break;
                    case string value: json.WriteStringValue(value); break;
                    default: throw new InvalidDataException($"未知的 SQLite 值类型：{reader.GetName(i)}");
                }
            }
            json.WriteEndObject();
        }
    }

    private static long CeilingMilliseconds(DateTimeOffset value)
    {
        var ticks = value.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks;
        var milliseconds = Math.DivRem(ticks, TimeSpan.TicksPerMillisecond, out var remainder);
        return milliseconds + (remainder > 0 ? 1 : 0);
    }
}
