#:project ../src/Orisumi.Cli/Orisumi.Cli.csproj
#:property PublishAot=false

using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using Orisumi.Cli.Capture;

var root = Directory.GetParent(Path.GetDirectoryName(SourceFile())!)!.FullName;
var temporary = Directory.CreateTempSubdirectory("orisumi-schema-");
const string prefix = "Orisumi.Collectors.";
// Every collector project is listed once in CaptureSources and in the solution.
var projects = Directory.GetDirectories(Path.Combine(root, "src/Collectors"), prefix + "*")
    .Select(path => Path.GetFileName(path)!).Order(StringComparer.Ordinal).ToArray();
Check(projects.Select(project => project[prefix.Length..].ToLowerInvariant()).Order(StringComparer.Ordinal)
    .SequenceEqual(CaptureSources.All.Select(source => source.Name).Order(StringComparer.Ordinal)), "CaptureSources lists every collector project");
var solution = File.ReadAllText(Path.Combine(root, "Orisumi.slnx"));
foreach (var project in projects)
    Check(solution.Contains($"src/Collectors/{project}/{project}.csproj"), project + " is missing from Orisumi.slnx");
Console.WriteLine("PASS: collector list, solution and capture sources agree");

// capture coverage reads these columns from every source; extra columns follow them.
string[] coverageColumns = ["scope", "mode", "started_at_utc_ms", "stopped_at_utc_ms", "evidence", "detail"];
try
{
    foreach (var project in projects)
    {
        var source = project[prefix.Length..];
        var schema = Path.Combine(root, "src/Collectors", project, "Schema");
        using var expected = Open(":memory:");
        foreach (var group in new[] { "Tables", "Indexes", "Views" })
            foreach (var file in Directory.EnumerateFiles(Path.Combine(schema, group), "*.sql").Order(StringComparer.Ordinal))
                Execute(expected, File.ReadAllText(file));
        Check(TextRows(expected, "SELECT name FROM pragma_table_info('capture_coverage') ORDER BY cid").Take(coverageColumns.Length)
            .SequenceEqual(coverageColumns), source + " capture_coverage columns");
        foreach (var view in TextRows(expected, "SELECT name FROM sqlite_schema WHERE type='view'"))
            Execute(expected, $"SELECT * FROM \"{view}\" LIMIT 0");

        var initializer = Assembly.Load(project).GetType("Orisumi.Collectors.Storage.SqliteSchema");
        if (initializer is null)
        {
            // Native collectors load the same files with their own toolchain.
            Console.WriteLine($"PASS: {source} schema files, views and coverage columns");
            continue;
        }
        var ensureCreated = initializer.GetMethod("EnsureCreated", BindingFlags.Static | BindingFlags.NonPublic)!
            .CreateDelegate<Action<SqliteConnection>>();
        var path = Path.Combine(temporary.FullName, source + ".sqlite");
        using (var actual = Open(path))
        {
            // Compare the packaged initializer's result with the entire source schema.
            ensureCreated(actual);
            const string objects = "SELECT type || ':' || name || ':' || sql FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%' ORDER BY type,name";
            Check(TextRows(actual, objects).SequenceEqual(TextRows(expected, objects)), source + " packaged schema differs from source");
            foreach (var view in TextRows(actual, "SELECT name FROM sqlite_schema WHERE type='view'"))
                Execute(actual, $"SELECT * FROM \"{view}\" LIMIT 0");
            Check(TextRows(actual, "PRAGMA foreign_key_check").Count == 0, source + " foreign keys");
            // Reopening must not recreate objects or lose an existing capture interval.
            var insertRun = source switch
            {
                "Browser" => "INSERT INTO capture_runs VALUES('test','chrome','profile','bookmarks',1,NULL)",
                "Codex" => "INSERT INTO capture_runs(started_at_utc_ms,scope,trigger) VALUES(1,'all','manual')",
                _ => "INSERT INTO capture_runs(started_at_utc_ms) VALUES(1)",
            };
            Execute(actual, insertRun);
        }
        using (var reopened = Open(path))
        {
            ensureCreated(reopened);
            Check(Number(reopened, "SELECT COUNT(*) FROM capture_runs") == 1, source + " lost data on reopen");
        }
        Console.WriteLine($"PASS: {source} complete schema, views and reopen");
    }
}
finally { temporary.Delete(recursive: true); }

static string SourceFile([CallerFilePath] string path = "") => path;
static void Check(bool valid, string message) { if (!valid) throw new Exception(message); }
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
