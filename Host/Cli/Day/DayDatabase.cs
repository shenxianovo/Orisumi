using Microsoft.Data.Sqlite;
using Orisumi.Cli.Capture;

namespace Orisumi.Cli.Day;

internal sealed class DayDatabase : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly SqliteTransaction transaction;
    private readonly Dictionary<string, HashSet<string>> columns = new(StringComparer.Ordinal);

    internal DayDatabase(string path, DayRange day)
    {
        Day = day;
        connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 3,
        }.ToString());
        try
        {
            connection.Open();
            transaction = connection.BeginTransaction(deferred: true);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    internal DayRange Day { get; }
    internal SortedSet<string> Missing { get; } = new(StringComparer.Ordinal);

    internal DatabaseHost Host() => Has("database_identity", "platform", "hardware_uuid")
        ? DatabaseHost.Read(connection, transaction) : new(null, null);

    internal bool Has(string table, params string[] required)
    {
        if (!columns.TryGetValue(table, out var available))
        {
            try
            {
                available = Read($"PRAGMA table_xinfo('{table}')", row => row.GetString(1)).ToHashSet(StringComparer.Ordinal);
            }
            catch (SqliteException error) when (error.SqliteErrorCode == 1 &&
                (error.Message.Contains("no such table", StringComparison.Ordinal) ||
                 error.Message.Contains("no such column", StringComparison.Ordinal)))
            {
                available = [];
            }
            columns.Add(table, available);
        }
        if (available.Count == 0)
        {
            Missing.Add(table);
            return false;
        }
        foreach (var column in required)
            if (!available.Contains(column)) Missing.Add($"{table}.{column}");
        return required.All(available.Contains);
    }

    internal string Column(string table, string column, string prefix = "") =>
        Has(table, column) ? prefix + column : "NULL";

    internal IEnumerable<T> Read<T>(string sql, Func<SqliteDataReader, T> read, bool bounded = false)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        if (bounded)
        {
            command.Parameters.AddWithValue("$from", Day.From);
            command.Parameters.AddWithValue("$to", Day.To);
        }
        using var reader = command.ExecuteReader();
        while (reader.Read()) yield return read(reader);
    }

    internal Dictionary<long, DaySpan<long>> Runs()
    {
        var stopped = Column("capture_runs", "stopped_at_utc_ms");
        var confirmed = Column("capture_runs", "confirmed_until_utc_ms");
        if (!Has("capture_runs", "run_id", "started_at_utc_ms")) return [];
        return Read($"""
            SELECT started_at_utc_ms, COALESCE({stopped},{confirmed}) AS ended, run_id
            FROM capture_runs WHERE started_at_utc_ms < $to AND ended > MAX($from,started_at_utc_ms)
            """, row => new DaySpan<long>(row.GetInt64(0), row.GetInt64(1), row.GetInt64(2)), bounded: true)
            .ToDictionary(run => run.Value);
    }

    internal IEnumerable<DayChange<T>> Changes<T>(string table, string value, Func<SqliteDataReader, T> read, params string[] fields) =>
        Has(table, ["run_id", "run_sequence", "observed_at_utc_ms", .. fields])
            ? Read($"SELECT run_id, run_sequence, observed_at_utc_ms, {value} FROM {table}",
                row => new DayChange<T>(Position(row), read(row))) : [];

    internal static DayPosition Position(SqliteDataReader row) => new(row.GetInt64(0), row.GetInt64(1), row.GetInt64(2));
    internal static string? Text(SqliteDataReader row, int column) => row.IsDBNull(column) ? null : row.GetString(column);
    internal static long? Number(SqliteDataReader row, int column) => row.IsDBNull(column) ? null : row.GetInt64(column);

    public void Dispose()
    {
        transaction.Dispose();
        connection.Dispose();
    }
}
