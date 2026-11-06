using Microsoft.Data.Sqlite;

namespace Orisumi.Capturing.Tests;

public class SqliteSnapshotReaderTests
{
    [Test]
    public async Task CommittedWalContentsProduceAnIndependentSnapshotAndDisposeRemovesIt()
    {
        using var sample = new CaptureSample();
        var path = Path.Combine(sample.SourceDirectory, "state.sqlite");
        using var source = SqliteSample.Create(path);
        SqliteSample.CommitRevision(source, 7);
        await Assert.That(new FileInfo(path + "-wal").Length > 0).IsTrue();
        var bare = Path.Combine(sample.SourceDirectory, "bare.sqlite");
        File.Copy(path, bare);
        using (var mainOnly = SqliteSample.OpenReadOnly(bare))
        using (var command = mainOnly.CreateCommand())
        {
            command.CommandText = "SELECT count(*) FROM threads";
            await Assert.That((long)command.ExecuteScalar()!).IsEqualTo(0);
        }
        var temporary = Directory.CreateDirectory(Path.Combine(sample.SourceDirectory, "snapshots")).FullName;
        var reader = new SqliteSnapshotReader(1, temporary);
        string snapshotPath;
        using (var snapshot = reader.Read(path, CancellationToken.None))
        {
            snapshotPath = snapshot.Name;
            var recovered = Path.Combine(sample.SourceDirectory, "recovered.sqlite");
            using (var output = File.Create(recovered)) snapshot.CopyTo(output);
            using var database = SqliteSample.OpenReadOnly(recovered);
            await SqliteSample.AssertRevision(database, 7);
            await Assert.That(Directory.GetFiles(temporary).SequenceEqual([snapshotPath])).IsTrue();
        }
        await Assert.That(File.Exists(snapshotPath)).IsFalse();
        await Assert.That(Directory.GetFileSystemEntries(temporary).Length).IsEqualTo(0);
    }

    [Test]
    public async Task ConcurrentCommitsKeepRelatedTablesInOneSnapshot()
    {
        using var sample = new CaptureSample();
        var path = Path.Combine(sample.SourceDirectory, "state.sqlite");
        using var source = SqliteSample.Create(path);
        SqliteSample.CommitRevision(source, 1);
        using var stop = new CancellationTokenSource();
        var writing = Task.Run(() =>
        {
            var revision = 1;
            while (!stop.IsCancellationRequested) SqliteSample.CommitRevision(source, ++revision);
        });
        try
        {
            var reader = new SqliteSnapshotReader(1);
            for (var i = 0; i < 5; i++)
            {
                using var snapshot = reader.Read(path, CancellationToken.None);
                var restored = Path.Combine(sample.SourceDirectory, $"restored-{i}.sqlite");
                using (var output = File.Create(restored)) snapshot.CopyTo(output);
                using var database = SqliteSample.OpenReadOnly(restored);
                using var command = database.CreateCommand();
                command.CommandText = "SELECT revision FROM threads";
                await SqliteSample.AssertRevision(database, Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        finally
        {
            stop.Cancel();
            await writing;
        }
    }

    [Test]
    public async Task CorruptAndMissingDatabasesLeaveNoTemporaryFilesOrCreatedSource()
    {
        using var sample = new CaptureSample();
        var temporary = Directory.CreateDirectory(Path.Combine(sample.SourceDirectory, "snapshots")).FullName;
        var reader = new SqliteSnapshotReader(1, temporary);
        var corrupt = sample.File("broken.sqlite", "not a database");
        await Assert.That(() => reader.Read(corrupt, CancellationToken.None)).Throws<IOException>();
        var missing = Path.Combine(sample.SourceDirectory, "missing.sqlite");
        await Assert.That(() => reader.Read(missing, CancellationToken.None)).Throws<IOException>();
        await Assert.That(File.Exists(missing)).IsFalse();
        await Assert.That(Directory.GetFileSystemEntries(temporary).Length).IsEqualTo(0);
    }

    [Test]
    public async Task BusyDatabaseRetriesAreCancellableAndDiscardTemporaryFiles()
    {
        using var sample = new CaptureSample();
        var path = Path.Combine(sample.SourceDirectory, "state.sqlite");
        using var source = SqliteSample.Create(path);
        SqliteSample.Execute(source, "PRAGMA journal_mode=DELETE; BEGIN EXCLUSIVE;");
        var temporary = Directory.CreateDirectory(Path.Combine(sample.SourceDirectory, "snapshots")).FullName;
        var reader = new SqliteSnapshotReader(20, temporary);
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        await Assert.That(() => reader.Read(path, stop.Token)).Throws<OperationCanceledException>();
        await Assert.That(Directory.GetFileSystemEntries(temporary).Length).IsEqualTo(0);
        SqliteSample.Execute(source, "ROLLBACK;");
    }

    [Test]
    public async Task AnExclusiveDatabaseLockDoesNotDiscardOtherCapturedMaterials()
    {
        using var sample = new CaptureSample();
        sample.File("sessions/a.jsonl", "log");
        sample.File("session_index.jsonl", "index");
        using var source = SqliteSample.Create(Path.Combine(sample.SourceDirectory, "state_5.sqlite"));
        SqliteSample.Execute(source, "PRAGMA journal_mode=DELETE; BEGIN EXCLUSIVE;");
        var round = sample.Collector.Scan(CodexSource.Create(sample.SourceDirectory));
        await Assert.That(round.Failure!.Contains("state_5.sqlite", StringComparison.Ordinal)).IsTrue();
        await Assert.That(sample.Store.Manifest(round.Result!.Capture).Select(entry => entry.Path)
            .SequenceEqual(["session_index.jsonl", "sessions/a.jsonl"])).IsTrue();
        SqliteSample.Execute(source, "ROLLBACK;");
    }
}
