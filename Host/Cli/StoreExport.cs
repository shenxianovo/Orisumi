using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Orisumi.Platform;

namespace Orisumi.Cli;

internal static partial class StoreExport
{
    internal static int Write(string store, string destination, TextWriter output)
    {
        var sourceStore = new Store(store);
        var destinationStore = new Store(destination);
        // Compared after resolving symlinks, so a link into the store cannot overwrite the databases being read.
        if (DirectoryPaths.Contains(DirectoryPaths.Resolve(sourceStore.Root), DirectoryPaths.Resolve(destinationStore.Root)))
            throw new ArgumentException("导出目标必须位于存储目录之外。");

        var databases = new List<DatabaseResult>();
        foreach (var source in sourceStore.Sources().Order(StringComparer.Ordinal))
            foreach (var path in sourceStore.Databases(source))
                databases.Add(Export(source, path, destinationStore.Observations(source)));

        output.WriteLine(JsonSerializer.Serialize(new ExportResult(sourceStore.Root, destinationStore.Root, databases), ExportJson.Default.ExportResult));
        return databases.Any(database => database.Status == "failed") ? 1 : 0;
    }

    private static DatabaseResult Export(string source, string path, string directory)
    {
        var database = Path.GetFileNameWithoutExtension(path);
        try
        {
            using var connection = Open(path, SqliteOpenMode.ReadOnly);
            var identity = ReadIdentity(connection);
            if (identity is null) return new(source, database, "skipped", null, "缺少 database_identity 表或记录。");
            CheckIdentity(identity, database);

            Directory.CreateDirectory(directory);
            var final = Path.Combine(directory, Path.GetFileName(path));
            var bytes = Snapshot(connection, database, final);
            return new(source, database, "exported", bytes, null);
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or InvalidDataException or SqliteException)
        {
            return new(source, database, "failed", null, error.Message);
        }
    }

    internal static void Backup(Store source, Store destination, CancellationToken token)
    {
        foreach (var name in source.Sources().Order(StringComparer.Ordinal))
        {
            foreach (var path in source.Databases(name))
            {
                token.ThrowIfCancellationRequested();
                using var connection = Open(path, SqliteOpenMode.ReadOnly);
                var directory = Directory.CreateDirectory(destination.Observations(name));
                Snapshot(connection, null, Path.Combine(directory.FullName, Path.GetFileName(path)));
            }
        }
    }

    private static long Snapshot(SqliteConnection source, string? database, string final)
    {
        // A partial file left by an interrupted export is replaced.
        var partial = final + ".partial";
        try
        {
            DeletePartial(partial);
            if (new FileInfo(final).LinkTarget is not null)
                throw new IOException("目标库是符号链接。");
            CheckDestinationJournals(final);
            if (File.Exists(final))
            {
                // Only an earlier snapshot of the same database is replaced.
                using var previous = Open(final, SqliteOpenMode.ReadOnly);
                if (database is not null) CheckIdentity(ReadIdentity(previous), database);
            }

            using (var command = source.CreateCommand())
            {
                command.CommandText = "VACUUM INTO $partial";
                command.Parameters.AddWithValue("$partial", partial);
                command.ExecuteNonQuery();
            }

            using (var copy = Open(partial, SqliteOpenMode.ReadWrite))
            using (var command = copy.CreateCommand())
            {
                command.CommandText = "PRAGMA journal_mode=DELETE";
                if (command.ExecuteScalar() is not "delete") throw new InvalidDataException("快照无法切换到 DELETE 日志模式。");
                command.CommandText = "PRAGMA integrity_check";
                if (command.ExecuteScalar() is not "ok") throw new InvalidDataException("快照未通过 SQLite 完整性检查。");
                if (database is not null) CheckIdentity(ReadIdentity(copy), database);
            }

            var bytes = new FileInfo(partial).Length;
            File.Move(partial, final, overwrite: true);
            return bytes;
        }
        catch
        {
            DeletePartial(partial);
            throw;
        }
    }

    private static void CheckDestinationJournals(string path)
    {
        if (File.Exists(path + "-journal") || File.Exists(path + "-wal") || File.Exists(path + "-shm"))
            throw new IOException("目标库仍有 SQLite 日志文件。");
    }

    private static void DeletePartial(string path)
    {
        File.Delete(path);
        File.Delete(path + "-journal");
        File.Delete(path + "-wal");
        File.Delete(path + "-shm");
    }

    private static SqliteConnection Open(string path, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = mode, Pooling = false, DefaultTimeout = 3,
        }.ToString());
        try
        {
            connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static string? ReadIdentity(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_schema WHERE type='table' AND name='database_identity'";
        if (command.ExecuteScalar() is null) return null;
        command.CommandText = "SELECT database_id FROM database_identity";
        return command.ExecuteScalar() switch
        {
            null => null,
            string database => database,
            _ => throw new InvalidDataException("database_identity.database_id 不是文本。"),
        };
    }

    private static void CheckIdentity(string? identity, string database)
    {
        if (identity != database)
            throw new InvalidDataException($"database_identity.database_id（{identity ?? "缺失"}）与文件名（{database}）不符。");
    }

    private sealed record ExportResult(string Store, string Destination, List<DatabaseResult> Databases);

    [JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true)]
    [JsonSerializable(typeof(ExportResult))]
    private sealed partial class ExportJson : JsonSerializerContext;

    private sealed record DatabaseResult(string Source, string Database, string Status, long? Bytes, string? Detail);
}
