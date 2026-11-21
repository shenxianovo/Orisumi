using Microsoft.Data.Sqlite;
using Orisumi.Capture.Hosting;
using Orisumi.Platform;

namespace Orisumi.Capture.Storage;

// Opens the database the source writes in the store (overview/storage.mdx); the connection rules live in capture/index.mdx.
public static class CollectorDatabase
{
    public static SqliteConnection Open(string store, Collector collector)
    {
        var (path, databaseId, platform, hardwareUuid) = new Store(store).WritableDatabase(collector.Source);
        // A file: data source enables URI filenames, so read-only sources can be attached with ?mode=ro.
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = new Uri(path).AbsoluteUri, Mode = SqliteOpenMode.ReadWrite, ForeignKeys = true, Pooling = false, DefaultTimeout = 3,
        }.ToString());
        try
        {
            connection.Open();
            SqliteSchema.EnsureCreated(connection, collector.Assembly, databaseId, platform, hardwareUuid, store);
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode = WAL";
            command.ExecuteNonQuery();
            return connection;
        }
        catch (SqliteException error)
        {
            connection.Dispose();
            throw new IOException($"could not open {path}: {error.Message}", error);
        }
        catch { connection.Dispose(); throw; }
    }

    // Parameters bind positionally as $p0, $p1, …; null becomes SQL NULL.
    public static object? Scalar(this SqliteCommand command, string sql, params object?[] values)
    {
        Bind(command, sql, values);
        return command.ExecuteScalar();
    }

    public static int Execute(this SqliteCommand command, string sql, params object?[] values)
    {
        Bind(command, sql, values);
        return command.ExecuteNonQuery();
    }

    private static void Bind(SqliteCommand command, string sql, object?[] values)
    {
        command.CommandText = sql;
        command.Parameters.Clear();
        for (var i = 0; i < values.Length; i++) command.Parameters.AddWithValue($"$p{i}", values[i] ?? DBNull.Value);
    }
}
