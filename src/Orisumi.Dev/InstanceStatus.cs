using Microsoft.Data.Sqlite;
using Orisumi.Platform;

namespace Orisumi.Dev;

internal static class InstanceStatus
{
    internal static async Task Write(string name, Store store, TextWriter output, CancellationToken token)
    {
        await output.WriteLineAsync($"{name} ({store.Root})");
        var installed = new List<string>();
        // What is installed in this instance, not what the checkout could release.
        string[] components = ["host", .. Directory.Exists(store.Collectors)
            ? Directory.GetDirectories(store.Collectors).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal).ToArray()
            : []];
        foreach (var component in components)
        {
            var program = InstanceWorkflow.InstalledProgram(store, component);
            if (!File.Exists(program)) continue;
            var description = await ProgramDescription.Read(program, [], token);
            await output.WriteLineAsync($"  {component} {description.Version}");
            if (component != "host") installed.Add(component);
        }
        var after = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeMilliseconds();
        foreach (var source in store.Sources().Union(installed).Order(StringComparer.Ordinal))
        {
            var run = Read(store.Databases(source), after);
            await output.WriteLineAsync($"  {source}: collector_version={run.Version ?? "-"} " +
                $"confirmed_until_utc_ms={run.Confirmed?.ToString() ?? "-"} read_failures_last_hour={run.Failures}");
        }
    }

    private static (string? Version, long? Confirmed, long Failures) Read(IReadOnlyList<string> paths, long after)
    {
        long latest = long.MinValue;
        string? version = null;
        long? confirmed = null;
        long failures = 0;
        foreach (var path in paths)
        {
            using var database = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 3,
            }.ToString());
            database.Open();
            using var command = database.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE type='table' AND name='capture_runs'";
            if (command.ExecuteScalar() is 0L) continue;
            command.CommandText = """
                SELECT started_at_utc_ms, collector_version, confirmed_until_utc_ms FROM capture_runs
                ORDER BY started_at_utc_ms DESC, run_id DESC LIMIT 1
                """;
            using (var reader = command.ExecuteReader())
                if (reader.Read() && reader.GetInt64(0) > latest)
                {
                    latest = reader.GetInt64(0);
                    version = reader.IsDBNull(1) ? null : reader.GetString(1);
                    confirmed = reader.IsDBNull(2) ? null : reader.GetInt64(2);
                }
            command.CommandText = "SELECT COUNT(*) FROM capture_read_failures WHERE observed_at_utc_ms >= $after";
            command.Parameters.AddWithValue("$after", after);
            failures += (long)command.ExecuteScalar()!;
        }
        return (version, confirmed, failures);
    }
}
