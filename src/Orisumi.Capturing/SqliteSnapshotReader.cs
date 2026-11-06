using Microsoft.Data.Sqlite;

namespace Orisumi.Capturing;

internal sealed class SqliteSnapshotReader(int retries, string? temporaryDirectory = null)
{
    public FileStream Read(string path, CancellationToken token)
    {
        for (long attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                return ReadOnce(path, token);
            }
            catch (SqliteException error) when (error.SqliteErrorCode is 5 or 6 && attempt < retries)
            {
                if (token.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(50))) token.ThrowIfCancellationRequested();
            }
            catch (Exception error) when (error is SqliteException or IOException or UnauthorizedAccessException)
            {
                token.ThrowIfCancellationRequested();
                throw new IOException($"{path}：SQLite 快照失败：{error.Message}", error);
            }
        }
    }

    private FileStream ReadOnce(string path, CancellationToken token)
    {
        var temporary = Path.Combine(temporaryDirectory ?? Path.GetTempPath(), $"orisumi-sqlite-{Guid.NewGuid():N}");
        FileStream? snapshot = null;
        try
        {
            using (var source = Open(path, SqliteOpenMode.ReadOnly))
            using (var destination = Open(temporary, SqliteOpenMode.ReadWriteCreate))
            {
                token.ThrowIfCancellationRequested();
                source.BackupDatabase(destination);
            }
            token.ThrowIfCancellationRequested();
            DeleteSidecars(temporary);
            snapshot = new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.Read, 65536,
                FileOptions.DeleteOnClose | FileOptions.SequentialScan);
            return snapshot;
        }
        finally
        {
            if (snapshot is null)
            {
                File.Delete(temporary);
                DeleteSidecars(temporary);
            }
        }
    }

    private static void DeleteSidecars(string path)
    {
        File.Delete(path + "-wal");
        File.Delete(path + "-shm");
        File.Delete(path + "-journal");
    }

    private static SqliteConnection Open(string path, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(path),
            Mode = mode,
            Pooling = false,
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
}
