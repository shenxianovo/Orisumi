using System.Text;
using Microsoft.Data.Sqlite;
using Orisumi.Raw;
using ZstdSharp;

namespace Orisumi.Capturing.Tests;

public class CodexSourceTests
{
    [Test]
    public async Task AllKnownMaterialsCanBeRecoveredAfterRemovingTheSource()
    {
        using var sample = new CaptureSample();
        sample.File("sessions/2026/11/parent.jsonl", "{\"type\":\"unknown\",\"thread_id\":\"parent\"}\n半行");
        sample.File("sessions/2026/11/child.jsonl", "{\"parent_thread_id\":\"parent\"}\n");
        var compressed = sample.File("archived_sessions/old.jsonl.zst", "");
        using (var compressor = new Compressor(3))
            File.WriteAllBytes(compressed, compressor.Wrap(Encoding.UTF8.GetBytes("{\"thread_id\":\"old\"}\n")).ToArray());
        var damaged = sample.File("archived_sessions/damaged.jsonl.zst", "");
        File.WriteAllBytes(damaged, [0x28, 0xB5, 0x2F, 0xFD, 0xFF]);
        sample.File("session_index.jsonl", "{\"id\":\"parent\",\"title\":\"改过的标题\"}\n");
        sample.File("attachments/long.txt", "原始长文本\r\n尾行");
        var image = sample.File("generated_images/image.png", "");
        File.WriteAllBytes(image, [0x89, 0x50, 0x4E, 0x47, 0x00, 0xFF]);
        sample.File("visualizations/chart/index.html", "<script src='chart.js'></script>");
        sample.File("visualizations/chart/chart.js", "const values = [1, 2];");
        sample.File("sessions/ignore.txt", "not a rollout");
        sample.File("settings.json", "outside scope");
        var databasePath = Path.Combine(sample.SourceDirectory, "state_5.sqlite");
        Capture capture;
        Dictionary<string, byte[]> expected;
        using (var database = SqliteSample.Create(databasePath))
        {
            SqliteSample.CommitRevision(database, 1);
            var round = sample.Collector.Scan(CodexSource.Create(sample.SourceDirectory));
            await Assert.That(round.Failure).IsNull();
            capture = round.Result!.Capture;
            var entries = sample.Store.Manifest(capture);
            await Assert.That(entries.Select(entry => entry.Path).SequenceEqual([
                "archived_sessions/damaged.jsonl.zst", "archived_sessions/old.jsonl.zst", "attachments/long.txt",
                "generated_images/image.png", "session_index.jsonl", "sessions/2026/11/child.jsonl",
                "sessions/2026/11/parent.jsonl", "state_5.sqlite", "visualizations/chart/chart.js", "visualizations/chart/index.html",
            ])).IsTrue();
            expected = entries.Where(entry => entry.Path != "state_5.sqlite")
                .ToDictionary(entry => entry.Path, entry => File.ReadAllBytes(entry.Origin));
            await Assert.That(entries.All(entry => entry.Origin == Path.Combine(sample.SourceDirectory, entry.Path))).IsTrue();
            await Assert.That(entries.Single(entry => entry.Path == "state_5.sqlite").Method).IsEqualTo("sqlite-online-backup");
            await Assert.That(entries.Where(entry => entry.Path != "state_5.sqlite").All(entry => entry.Method == "file-bytes")).IsTrue();
            await Assert.That(capture.Record.Origin).IsEqualTo(sample.SourceDirectory);
            await Assert.That(capture.Record.CompletedAt >= capture.Record.CapturedAt).IsTrue();
        }
        Directory.Delete(sample.SourceDirectory, recursive: true);
        foreach (var (path, bytes) in expected)
            await Assert.That(Read(sample.Store, capture, path).SequenceEqual(bytes)).IsTrue();
        using (var decompressor = new Decompressor())
            await Assert.That(Encoding.UTF8.GetString(decompressor.Unwrap(Read(sample.Store, capture, "archived_sessions/old.jsonl.zst"))))
                .IsEqualTo("{\"thread_id\":\"old\"}\n");
        var restored = Path.Combine(sample.StoreDirectory, "restored.sqlite");
        File.WriteAllBytes(restored, Read(sample.Store, capture, "state_5.sqlite"));
        using var recovered = SqliteSample.OpenReadOnly(restored);
        await SqliteSample.AssertRevision(recovered, 1);
    }

    [Test]
    public async Task UnchangedMaterialsReuseTheManifestAndLaterChangesPreserveEarlierVersions()
    {
        using var sample = new CaptureSample();
        var log = sample.File("sessions/a.jsonl", "first");
        sample.File("session_index.jsonl", "index");
        using var database = SqliteSample.Create(Path.Combine(sample.SourceDirectory, "state_5.sqlite"));
        SqliteSample.CommitRevision(database, 1);
        var source = CodexSource.Create(sample.SourceDirectory);
        var first = sample.Collector.Scan(source).Result!;
        var second = sample.Collector.Scan(source).Result!;
        await Assert.That(second.NewBlocks).IsEqualTo(0);
        await Assert.That(second.Capture.Record.Manifest!.Hash).IsEqualTo(first.Capture.Record.Manifest!.Hash);
        File.WriteAllText(log, "second");
        SqliteSample.CommitRevision(database, 2);
        var third = sample.Collector.Scan(source).Result!;
        await Assert.That(third.NewBlocks > 0).IsTrue();
        await Assert.That(Encoding.UTF8.GetString(Read(sample.Store, first.Capture, "sessions/a.jsonl"))).IsEqualTo("first");
        await Assert.That(Encoding.UTF8.GetString(Read(sample.Store, third.Capture, "sessions/a.jsonl"))).IsEqualTo("second");
        foreach (var (capture, revision) in new[] { (first.Capture, 1), (third.Capture, 2) })
        {
            var path = Path.Combine(sample.StoreDirectory, $"revision-{revision}.sqlite");
            File.WriteAllBytes(path, Read(sample.Store, capture, "state_5.sqlite"));
            using var restored = SqliteSample.OpenReadOnly(path);
            await SqliteSample.AssertRevision(restored, revision);
        }
    }

    [Test]
    public async Task MissingOptionalDirectoriesAreEmptyAndAnUnreadableOptionalRootIsAFailure()
    {
        using var sample = new CaptureSample();
        sample.File("sessions/a.jsonl", "log");
        sample.File("session_index.jsonl", "index");
        using var database = SqliteSample.Create(Path.Combine(sample.SourceDirectory, "state_5.sqlite"));
        var source = CodexSource.Create(sample.SourceDirectory);
        await Assert.That(sample.Collector.Scan(source).Failure).IsNull();
        sample.File("attachments", "a file where the directory should be");
        var failed = sample.Collector.Scan(source);
        await Assert.That(failed.Failure!.Contains("attachments", StringComparison.Ordinal)).IsTrue();
        await Assert.That(failed.Result!.Capture.Record.Manifest).IsNotNull();
    }

    [Test]
    public async Task CorruptOrMissingDatabaseKeepsOtherMaterialsAndReportsTheGap()
    {
        using var sample = new CaptureSample();
        sample.File("sessions/a.jsonl", "available");
        sample.File("session_index.jsonl", "index");
        var database = sample.File("state_5.sqlite", "not a SQLite database");
        var source = CodexSource.Create(sample.SourceDirectory);
        var corrupt = sample.Collector.Scan(source);
        await Assert.That(corrupt.Failure!.Contains("state_5.sqlite", StringComparison.Ordinal)).IsTrue();
        await Assert.That(sample.Store.Manifest(corrupt.Result!.Capture).Select(entry => entry.Path)
            .SequenceEqual(["session_index.jsonl", "sessions/a.jsonl"])).IsTrue();
        File.Delete(database);
        var missing = sample.Collector.Scan(source);
        await Assert.That(missing.Failure!.Contains("state_5.sqlite", StringComparison.Ordinal)).IsTrue();
        await Assert.That(missing.Result!.Capture.Record.Manifest).IsNotNull();
    }

    [Test]
    public async Task MissingSessionsKeepsTheDatabaseAndReportsTheGap()
    {
        using var sample = new CaptureSample();
        sample.File("session_index.jsonl", "index");
        using var database = SqliteSample.Create(Path.Combine(sample.SourceDirectory, "state_5.sqlite"));
        var round = sample.Collector.Scan(CodexSource.Create(sample.SourceDirectory));
        await Assert.That(round.Failure!.Contains("sessions", StringComparison.Ordinal)).IsTrue();
        await Assert.That(sample.Store.Manifest(round.Result!.Capture).Select(entry => entry.Path)
            .SequenceEqual(["session_index.jsonl", "state_5.sqlite"])).IsTrue();
    }

    [Test]
    public async Task ALinkedDatabasePreservesTheLinkAndReportsTheMissingSnapshot()
    {
        using var sample = new CaptureSample();
        sample.File("sessions/a.jsonl", "log");
        sample.File("session_index.jsonl", "index");
        using var database = SqliteSample.Create(Path.Combine(sample.SourceDirectory, "actual.sqlite"));
        File.CreateSymbolicLink(Path.Combine(sample.SourceDirectory, "state_5.sqlite"), "actual.sqlite");
        var round = sample.Collector.Scan(CodexSource.Create(sample.SourceDirectory));
        var entry = sample.Store.Manifest(round.Result!.Capture).Single(entry => entry.Path == "state_5.sqlite");
        await Assert.That(entry.Method).IsEqualTo("readlink");
        await Assert.That(entry.LinkTarget).IsEqualTo("actual.sqlite");
        await Assert.That(round.Failure!.Contains("未取得数据库快照", StringComparison.Ordinal)).IsTrue();
    }

    private static byte[] Read(RawStore store, Capture capture, string path)
    {
        using var content = store.Open(capture, path);
        using var bytes = new MemoryStream();
        content.CopyTo(bytes);
        return bytes.ToArray();
    }
}

internal static class SqliteSample
{
    public static SqliteConnection Create(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        Execute(connection, """
            PRAGMA journal_mode=WAL;
            PRAGMA wal_autocheckpoint=0;
            PRAGMA user_version=42;
            CREATE TABLE threads(id TEXT PRIMARY KEY, revision INTEGER NOT NULL);
            CREATE TABLE future_state(id INTEGER PRIMARY KEY, revision INTEGER NOT NULL, payload BLOB, nullable TEXT);
            PRAGMA wal_checkpoint(TRUNCATE);
            """);
        return connection;
    }

    public static SqliteConnection OpenReadOnly(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    public static void CommitRevision(SqliteConnection connection, int revision) => Execute(connection, $"""
        BEGIN;
        INSERT OR REPLACE INTO threads VALUES ('thread', {revision});
        INSERT OR REPLACE INTO future_state VALUES (1, {revision}, X'00FF0100', NULL);
        COMMIT;
        """);

    public static async Task AssertRevision(SqliteConnection connection, int revision)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT t.revision, s.revision, s.payload, s.nullable FROM threads t JOIN future_state s ON s.id=1";
        using (var reader = command.ExecuteReader())
        {
            await Assert.That(reader.Read()).IsTrue();
            await Assert.That(reader.GetInt32(0)).IsEqualTo(revision);
            await Assert.That(reader.GetInt32(1)).IsEqualTo(revision);
            await Assert.That(((byte[])reader[2]).SequenceEqual(new byte[] { 0x00, 0xFF, 0x01, 0x00 })).IsTrue();
            await Assert.That(reader.IsDBNull(3)).IsTrue();
        }
        command.CommandText = "PRAGMA user_version";
        await Assert.That((long)command.ExecuteScalar()!).IsEqualTo(42);
        command.CommandText = "PRAGMA quick_check";
        await Assert.That((string)command.ExecuteScalar()!).IsEqualTo("ok");
    }

    public static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
