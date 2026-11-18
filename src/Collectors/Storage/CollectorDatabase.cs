using Microsoft.Data.Sqlite;

namespace Orisumi.Collectors.Storage;

// Each source writes its own STORE/raw/<source>.sqlite; the connection rules live in capture.mdx.
internal static class CollectorDatabase
{
    internal static string PathFor(string store, string source) =>
        Path.GetFullPath(Path.Combine(store, "raw", source + ".sqlite"));

    internal static SqliteConnection Open(string store, string source)
    {
        var path = PathFor(store, source);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        CreatePrivateFile(path);
        // A file: data source enables URI filenames, so read-only sources can be attached with ?mode=ro.
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = new Uri(path).AbsoluteUri, Mode = SqliteOpenMode.ReadWrite, ForeignKeys = true, Pooling = false, DefaultTimeout = 3,
        }.ToString());
        try
        {
            connection.Open();
            SqliteSchema.EnsureCreated(connection);
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

    // One writer per source and store; a second process fails instead of waiting.
    internal static FileStream Lock(string store, string source)
    {
        var path = PathFor(store, source);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return new FileStream(path + ".capture.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    // Parameters bind positionally as $p0, $p1, …; null becomes SQL NULL.
    internal static object? Scalar(this SqliteCommand command, string sql, params object?[] values)
    {
        Bind(command, sql, values);
        return command.ExecuteScalar();
    }

    internal static int Execute(this SqliteCommand command, string sql, params object?[] values)
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

    private static void CreatePrivateFile(string path)
    {
        // Opening an existing file through FileStream can conflict with SQLite's locks.
        if (File.Exists(path)) return;
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.ReadWrite,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite };
        try { using var file = File.Open(path, options); }
        catch (IOException) when (File.Exists(path)) { } // Another writer created the file first.
    }
}
