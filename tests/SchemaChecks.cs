#:project ../src/Orisumi.Cli/Orisumi.Cli.csproj
#:property PublishAot=false

using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;

var root = Directory.GetParent(Path.GetDirectoryName(SourceFile())!)!.FullName;
var temporary = Directory.CreateTempSubdirectory("orisumi-schema-");
try
{
    foreach (var source in new[] { "Browser", "Claude", "Codex", "Network" })
    {
        var assembly = Assembly.Load("Orisumi.Collectors." + source);
        var initializer = assembly.GetType("Orisumi.Collectors.Storage.SqliteSchema", throwOnError: true)!;
        var ensureCreated = initializer.GetMethod("EnsureCreated", BindingFlags.Static | BindingFlags.NonPublic)!
            .CreateDelegate<Action<SqliteConnection>>();
        var checkVersion = initializer.GetMethod("CheckVersion", BindingFlags.Static | BindingFlags.NonPublic)!
            .CreateDelegate<Action<SqliteConnection>>();
        var schema = Path.Combine(root, "src/Collectors", assembly.GetName().Name!, "Schema");
        var version = long.Parse(File.ReadAllText(Path.Combine(schema, "Version.txt")));
        var path = Path.Combine(temporary.FullName, source + ".sqlite");
        using (var actual = Open(path))
        using (var expected = Open(":memory:"))
        {
            // Compare the packaged initializer's result with the entire source schema.
            foreach (var group in new[] { "Tables", "Indexes", "Views" })
                foreach (var file in Directory.EnumerateFiles(Path.Combine(schema, group), "*.sql").Order(StringComparer.Ordinal))
                    Execute(expected, File.ReadAllText(file));
            ensureCreated(actual);
            const string objects = "SELECT type || ':' || name || ':' || sql FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%' ORDER BY type,name";
            Check(TextRows(actual, objects).SequenceEqual(TextRows(expected, objects)), source + " packaged schema differs from source");
            Check(Number(actual, "PRAGMA user_version") == version, source + " version");
            foreach (var view in TextRows(actual, "SELECT name FROM sqlite_schema WHERE type='view'"))
                Execute(actual, $"SELECT * FROM \"{view}\" LIMIT 0");
            Check(TextRows(actual, "PRAGMA foreign_key_check").Count == 0, source + " foreign keys");
            // Reopening must not recreate objects or lose an existing capture interval.
            var insertRun = source switch
            {
                "Browser" => "INSERT INTO capture_runs VALUES('test','chrome','profile','bookmarks',1,NULL)",
                "Network" => "INSERT INTO capture_runs VALUES('test','nettop','boot',5,1,NULL,NULL)",
                "Codex" => "INSERT INTO capture_runs(started_at_utc_ms,scope,trigger) VALUES(1,'all','manual')",
                _ => "INSERT INTO capture_runs(started_at_utc_ms) VALUES(1)",
            };
            Execute(actual, insertRun);
        }
        using (var reopened = Open(path))
        {
            ensureCreated(reopened);
            checkVersion(reopened);
            Check(Number(reopened, "SELECT COUNT(*) FROM capture_runs") == 1, source + " lost data on reopen");
            Execute(reopened, "PRAGMA user_version=999");
            Reject(() => ensureCreated(reopened));
            Reject(() => checkVersion(reopened));
            Check(Number(reopened, "PRAGMA user_version") == 999, source + " changed rejected version");
            Check(Number(reopened, "SELECT COUNT(*) FROM capture_runs") == 1, source + " changed rejected data");
        }
        using (var unversioned = Open(":memory:"))
        {
            Execute(unversioned, "CREATE TABLE old_facts(value TEXT); INSERT INTO old_facts VALUES('preserve')");
            Reject(() => ensureCreated(unversioned));
            Check(TextRows(unversioned, "SELECT value FROM old_facts").Single() == "preserve", source + " changed unversioned data");
            Check(Number(unversioned, "SELECT COUNT(*) FROM sqlite_schema") == 1, source + " extended an unversioned database");
            Check(Number(unversioned, "PRAGMA user_version") == 0, source + " stamped an unversioned database");
        }
        Console.WriteLine($"PASS: {source} complete schema, views, reopen and version rejection");
    }
}
finally { temporary.Delete(recursive: true); }

static string SourceFile([CallerFilePath] string path = "") => path;
static void Check(bool valid, string message) { if (!valid) throw new Exception(message); }
static void Reject(Action action)
{
    try { action(); }
    catch (IOException error) when (error.Message.Contains("Unsupported")) { return; }
    throw new Exception("Unsupported schema was accepted");
}
static SqliteConnection Open(string path)
{
    var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, ForeignKeys = true, Pooling = false }.ToString());
    db.Open();
    return db;
}
static void Execute(SqliteConnection db, string sql)
{
    using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
}
static long Number(SqliteConnection db, string sql)
{
    using var command = db.CreateCommand(); command.CommandText = sql; return (long)command.ExecuteScalar()!;
}
static List<string> TextRows(SqliteConnection db, string sql)
{
    using var command = db.CreateCommand(); command.CommandText = sql;
    using var reader = command.ExecuteReader();
    var result = new List<string>();
    while (reader.Read()) result.Add(reader.GetString(0));
    return result;
}
