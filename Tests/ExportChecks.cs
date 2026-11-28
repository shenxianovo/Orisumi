#:project ../Host/Cli/Orisumi.Cli.csproj
#:property AssemblyName=ExportChecks
#:property PublishAot=false
#:property JsonSerializerIsReflectionEnabledByDefault=true

using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Orisumi.Cli;
using Orisumi.Core;

var temp = Directory.CreateTempSubdirectory("orisumi-export-");
try
{
    var store = new Store(Path.Combine(temp.FullName, "store"));
    var destination = new Store(store.Root + "-user's snapshots");
    (string Source, string Id, string Value)[] fixtures =
    [
        ("claude", Guid.NewGuid().ToString("N"), "claude current"),
        ("claude", Guid.NewGuid().ToString("N"), "claude older"),
        ("codex", Guid.NewGuid().ToString("N"), "codex current"),
        ("codex", Guid.NewGuid().ToString("N"), "codex older"),
        ("other-device-only", Guid.NewGuid().ToString("N"), "copied from another device"),
    ];
    foreach (var (source, id, value) in fixtures)
    {
        CreateDatabase(Database(store, source, id), id, value);
    }
    var current = Directory.CreateDirectory(Path.Combine(store.State, "current"));
    foreach (var (source, id, _) in fixtures.Where((_, index) => index is 0 or 2))
    {
        File.WriteAllText(Path.Combine(current.FullName, source + ".json"), JsonSerializer.Serialize(new { databaseId = id, platform = Platforms.Current }));
        Check(store.CurrentDatabase(source) == Database(store, source, id), source + " current database fixture");
    }
    var log = Directory.CreateDirectory(store.Logs);
    File.WriteAllText(Path.Combine(log.FullName, "capture.log"), "log fixture");

    var (liveSource, liveId, liveValue) = fixtures[0];
    var livePath = Database(store, liveSource, liveId);
    var liveCopy = Database(destination, liveSource, liveId);
    using var writer = Open(livePath, SqliteOpenMode.ReadWrite);
    Check(Execute(writer, "PRAGMA journal_mode=WAL") is "wal", "writer WAL fixture");
    Execute(writer, "PRAGMA wal_autocheckpoint=0");
    Execute(writer, "PRAGMA wal_checkpoint(TRUNCATE)");
    var main = Hash(livePath);
    Execute(writer, "INSERT INTO observations VALUES ('only in WAL')");
    Check(File.Exists(livePath + "-wal") && new FileInfo(livePath + "-wal").Length > 0 && Hash(livePath) == main,
        "committed row is still in WAL");

    var before = SourceHashes();
    using (var report = Export(0))
    {
        Check(report.RootElement.GetProperty("store").GetString() == store.Root &&
            report.RootElement.GetProperty("destination").GetString() == destination.Root, "report identifies source and target stores");
        var rows = ReportRows(report);
        Check(rows.Length == fixtures.Length, "reports every fixture database");
        foreach (var (source, id, value) in fixtures)
        {
            var copy = Database(destination, source, id);
            var row = Result(rows, source, id, "exported");
            Check(row.GetProperty("bytes").GetInt64() == new FileInfo(copy).Length && row.GetProperty("detail").ValueKind == JsonValueKind.Null,
                "export reports the snapshot size");
            var values = id == liveId ? value + ",only in WAL" : value;
            CheckSnapshot(copy, id, values);
        }
    }
    Check(Directory.GetFiles(destination.Root, "*", SearchOption.AllDirectories).Length == fixtures.Length &&
        Directory.GetDirectories(destination.Root).SequenceEqual([Path.Combine(destination.Root, "observations")]),
        "only the observation snapshots are exported");
    Check(SourceHashes().SequenceEqual(before), "first export changed a source database");
    Console.WriteLine("PASS: current and older databases, uncheckpointed WAL rows and a copied source without a collector");

    Execute(writer, "INSERT INTO observations VALUES ('later row')");
    Check(Hash(livePath) == main, "later row is still in WAL");
    var partial = liveCopy + ".partial";
    File.WriteAllText(partial, "interrupted snapshot");
    File.WriteAllText(partial + "-journal", "interrupted journal");
    using (var report = Export(0))
        Result(ReportRows(report), liveSource, liveId, "exported");
    CheckSnapshot(liveCopy, liveId, liveValue + ",only in WAL,later row");
    Check(!File.Exists(partial) && !File.Exists(partial + "-journal"), "stale partial files are removed");
    Check(SourceHashes().SequenceEqual(before), "re-export changed a source database");
    Console.WriteLine("PASS: re-export replaces the snapshot and stale partial, leaves a standalone DELETE-mode file and preserves source hashes");

    var beforePaths = Directory.GetFileSystemEntries(store.Root, "*", SearchOption.AllDirectories).Order().ToArray();
    var alias = Path.Combine(temp.FullName, "store alias");
    Directory.CreateSymbolicLink(alias, store.Root);
    foreach (var refused in new[] { store.Root + Path.DirectorySeparatorChar, Path.Combine(store.Root, "new", "inside"), Path.Combine(alias, "inside") })
    {
        using var output = new StringWriter();
        try
        {
            StoreExport.Write(store.Root, refused, output);
            throw new Exception("export accepted a destination inside the store");
        }
        catch (ArgumentException) { }
        Check(output.ToString().Length == 0, "refused export emitted a report");
    }
    Check(Directory.GetFileSystemEntries(store.Root, "*", SearchOption.AllDirectories).Order().SequenceEqual(beforePaths) &&
        SourceHashes().SequenceEqual(before), "refused exports left the source tree unchanged");
    Console.WriteLine("PASS: destination equal to, inside or symlinked into the source store is refused before writing");

    var empty = Guid.NewGuid().ToString("N");
    using (File.Create(Database(store, "claude", empty))) { }
    var missingRow = Guid.NewGuid().ToString("N");
    using (var db = Open(Database(store, "claude", missingRow), SqliteOpenMode.ReadWriteCreate))
        Execute(db, "CREATE TABLE database_identity(database_id TEXT NOT NULL)");
    var mismatch = Guid.NewGuid().ToString("N");
    CreateDatabase(Database(store, "claude", mismatch), Guid.NewGuid().ToString("N"), "wrong identity");
    var unchanged = Hash(Database(store, "claude", mismatch));
    using (var report = Export(1))
    {
        var rows = ReportRows(report);
        Result(rows, "claude", empty, "skipped");
        Result(rows, "claude", missingRow, "skipped");
        Result(rows, "claude", mismatch, "failed");
        Check(rows.Count(row => row.GetProperty("status").GetString() == "exported") == fixtures.Length, "valid databases still export");
    }
    Check(new[] { empty, missingRow, mismatch }.All(id => !File.Exists(Database(destination, "claude", id))),
        "invalid or incomplete databases do not produce a snapshot");
    Check(Hash(Database(store, "claude", mismatch)) == unchanged && SourceHashes().SequenceEqual(before), "mixed result changed a source database");
    Console.WriteLine("PASS: missing identities are skipped, a source identity mismatch fails, and other databases still export");

    var (otherSource, otherId, _) = fixtures[2];
    var conflict = Database(destination, otherSource, otherId);
    using (var db = Open(conflict, SqliteOpenMode.ReadWrite))
        Execute(db, $"UPDATE database_identity SET database_id='{Guid.NewGuid():N}'");
    var original = Hash(conflict);
    var blocked = Database(destination, fixtures[3].Source, fixtures[3].Id);
    File.Delete(blocked);
    Directory.CreateDirectory(blocked);
    using (var report = Export(1))
    {
        var rows = ReportRows(report);
        Result(rows, otherSource, otherId, "failed");
        Result(rows, fixtures[3].Source, fixtures[3].Id, "failed");
        Result(rows, liveSource, liveId, "exported");
    }
    Check(Hash(conflict) == original && !File.Exists(blocked + ".partial"), "bad destination identity or failed rename must preserve the final path and remove the partial");
    Console.WriteLine("PASS: a conflicting destination is preserved and a failed publish removes its partial file");

    JsonDocument Export(int expectedCode)
    {
        using var output = new StringWriter();
        var code = StoreExport.Write(store.Root, destination.Root, output);
        Check(code == expectedCode, $"expected export exit {expectedCode}, got {code}: {output}");
        return JsonDocument.Parse(output.ToString());
    }

    string[] SourceHashes() => fixtures.Select(fixture => Hash(Database(store, fixture.Source, fixture.Id))).ToArray();
}
finally { temp.Delete(recursive: true); }

static JsonElement[] ReportRows(JsonDocument report) => report.RootElement.GetProperty("databases").EnumerateArray().ToArray();

static JsonElement Result(JsonElement[] rows, string source, string id, string status)
{
    var row = rows.Single(row => row.GetProperty("source").GetString() == source && row.GetProperty("database").GetString() == id);
    Check(row.GetProperty("status").GetString() == status, row.ToString());
    if (status != "exported")
        Check(row.GetProperty("bytes").ValueKind == JsonValueKind.Null && !string.IsNullOrWhiteSpace(row.GetProperty("detail").GetString()), row.ToString());
    return row;
}

static void CheckSnapshot(string path, string id, string values)
{
    using (var copy = Open(path, SqliteOpenMode.ReadOnly))
    {
        Check(Execute(copy, "SELECT database_id FROM database_identity") as string == id, "snapshot identity differs");
        Check(Execute(copy, "SELECT group_concat(value, ',') FROM (SELECT value FROM observations ORDER BY rowid)") as string == values,
            "snapshot rows differ");
        Check(Execute(copy, "PRAGMA journal_mode") is "delete", "snapshot journal mode");
    }
    Check(!File.Exists(path + "-wal") && !File.Exists(path + "-shm"), "snapshot has WAL sidecars");
}

static void CreateDatabase(string path, string id, string value)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    using var db = Open(path, SqliteOpenMode.ReadWriteCreate);
    using var command = db.CreateCommand();
    command.CommandText = """
        CREATE TABLE database_identity(singleton INTEGER PRIMARY KEY CHECK(singleton=1), database_id TEXT NOT NULL);
        INSERT INTO database_identity VALUES (1, $id);
        CREATE TABLE observations(value TEXT NOT NULL);
        INSERT INTO observations VALUES ($value);
        """;
    command.Parameters.AddWithValue("$id", id);
    command.Parameters.AddWithValue("$value", value);
    command.ExecuteNonQuery();
}

static SqliteConnection Open(string path, SqliteOpenMode mode)
{
    var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false }.ToString());
    db.Open();
    return db;
}

static object? Execute(SqliteConnection db, string sql)
{
    using var command = db.CreateCommand();
    command.CommandText = sql;
    return command.ExecuteScalar();
}

static string Database(Store store, string source, string id) => Path.Combine(store.Observations(source), id + ".sqlite");
static string Hash(string path)
{
    using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    return Convert.ToHexString(SHA256.HashData(file));
}
static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
