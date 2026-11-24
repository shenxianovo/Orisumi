#:project ../Host/Cli/Orisumi.Cli.csproj
#:project ../Shared/Capture/Orisumi.Capture.csproj
#:property PublishAot=false
#:property AssemblyName=SchemaChecks

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using System.Text.Json;
using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using Orisumi.Capture.Storage;
using Orisumi.Cli;
using Orisumi.Platform;

[assembly: SupportedOSPlatform("macos")]

var root = Directory.GetParent(Path.GetDirectoryName(SourceFile())!)!.FullName;
var temporary = Directory.CreateTempSubdirectory("orisumi-schema-");
const string prefix = "Orisumi.Collectors.";
var projects = Directory.GetDirectories(Path.Combine(root, "Collectors"))
    .Select(path => Path.GetFileName(path)!).Order(StringComparer.Ordinal).ToArray();
var solution = XDocument.Load(Path.Combine(root, "Orisumi.slnx")).Descendants("Project")
    .Select(project => (string?)project.Attribute("Path")).ToArray();
var programs = await CollectorPrograms.Read(temporary.FullName, CancellationToken.None, Path.Combine(root, "Host/Cli/bin/Debug/net11.0"));
Check(programs.Length == projects.Count(project => Platforms.Runs(project.ToLowerInvariant(), Platforms.CurrentRid)),
    "build one executable per collector for this platform");
var expectedArguments = new Dictionary<string, string[]>
{
    ["browser"] = ["capture"],
    ["claude"] = ["capture"],
    ["codex"] = ["capture"],
    ["macos"] = ["foreground", "--windows", "--input", "--microphone", "--audio-output", "--camera"],
};
Check(!typeof(CollectorPrograms).Assembly.GetReferencedAssemblies().Any(reference => reference.Name!.StartsWith("orisumi-", StringComparison.Ordinal)),
    "host does not reference collectors");
Console.WriteLine("PASS: each collector has a standalone build and the host assembly has no collector reference");

// capture coverage reads these columns from every source; extra columns follow them.
string[] coverageColumns = ["scope", "mode", "started_at_utc_ms", "stopped_at_utc_ms", "evidence", "detail"];
try
{
    var host = Path.Combine(root, "Host/Cli/bin/Debug/net11.0/orisumi.dll");
    var hostDescription = await ProgramDescription.Read(RunProcess.Dotnet, [host], default);
    Check(hostDescription is { Name: "host", Arguments: null }, "host describes only its name and version");
    await DescriptionShape(host, "name", "version");
    await InvalidDescription("""{"name":"host","version":"1","arguments":["capture"]}""");
    await InvalidDescription("""{"name":"browser","version":"1"}""");
    await InvalidDescription("""{"name":"browser","version":"1","arguments":[]}""");
    await InvalidDescription("""{"name":"browser","version":"1","arguments":["capture",null]}""");
    Console.WriteLine("PASS: host describe fields and invalid program descriptions");
    foreach (var project in projects)
    {
        var source = project;
        Check(solution.Count(path => path == $"Collectors/{project}/{prefix}{project}.csproj") == 1,
            project + " appears in Orisumi.slnx once");
        var name = source.ToLowerInvariant();
        if (!Platforms.Runs(name, Platforms.CurrentRid)) continue;
        var projectDirectory = Path.Combine(root, "Collectors", project) + Path.DirectorySeparatorChar;
        var program = programs.Single(program => program.Path.StartsWith(projectDirectory, StringComparison.Ordinal));
        Check(program.Description.Name == name && Path.GetFileName(program.Path) == $"orisumi-{name}.dll", project + " executable and describe name");
        if (expectedArguments.TryGetValue(name, out var arguments))
            Check(program.Description.Arguments!.SequenceEqual(arguments), project + " describe arguments");
        await DescriptionShape(program.Path, "arguments", "name", "version");
        // Each database has the shared Shared/Capture/Schema tables; loaders order both sets by file name.
        string[] schemas = [
            Path.Combine(root, "Collectors", project, "Schema"),
            Path.Combine(root, "Shared/Capture/Schema"),
        ];
        using var expected = Open(":memory:");
        foreach (var group in new[] { "Tables", "Indexes", "Views" })
            foreach (var file in schemas.Select(schema => Path.Combine(schema, group)).Where(Directory.Exists)
                         .SelectMany(directory => Directory.EnumerateFiles(directory, "*.sql")).OrderBy(Path.GetFileName, StringComparer.Ordinal))
                Execute(expected, File.ReadAllText(file).Replace("'{platform}'", $"'{Platforms.Current}'", StringComparison.Ordinal));
        Check(TextRows(expected, "SELECT name FROM pragma_table_info('capture_coverage') ORDER BY cid").Take(coverageColumns.Length)
            .SequenceEqual(coverageColumns), source + " capture_coverage columns");
        var assembly = Assembly.LoadFrom(program.Path);
        Check(program.Description.Version == assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion &&
            System.Text.RegularExpressions.Regex.IsMatch(program.Description.Version, @"^0\.1\.0\+[0-9a-f]{40}$"),
            source + " describe reports the informational version with commit");
        Console.WriteLine($"PASS: describe {name} {program.Description.Version} [{string.Join(", ", program.Description.Arguments!)}]");
        Check(assembly.GetReferencedAssemblies().Any(reference => reference.Name == "ZstdSharp") == (source == "Codex"),
            source + " Zstandard assembly dependency");
        Check(TextRows(expected, "SELECT name FROM pragma_table_info('capture_coverage')")
            .Contains("confirmed_until_utc_ms"), source + " capture_coverage includes run confirmation");
        foreach (var view in TextRows(expected, "SELECT name FROM sqlite_schema WHERE type='view'"))
            Execute(expected, $"SELECT * FROM \"{view}\" LIMIT 0");
        await SchemaShape(program.Path, expected);
        if (name == "browser") SchemaUpgrade(temporary.FullName, assembly, expected);

        var ensureCreated = (SqliteConnection connection, string id, string platform, string? hardware) =>
            SqliteSchema.EnsureCreated(connection, assembly, id, platform, hardware);
        var newId = Guid.NewGuid().ToString("N");
        var path = Path.Combine(temporary.FullName, newId + ".sqlite");
        var platform = Platforms.Current;
        const string hardwareUuid = "00000000-0000-0000-0000-000000000001";
        const string identity = "SELECT database_id || ':' || platform || ':' || hardware_uuid FROM database_identity";
        var expectedIdentity = $"{newId}:{platform}:{hardwareUuid}";
        using (var actual = Open(path))
        {
            // Compare the packaged initializer's result with the entire source schema.
            ensureCreated(actual, newId, platform, hardwareUuid);
            const string objects = "SELECT type || ':' || name || ':' || sql FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%' ORDER BY type,name";
            Check(TextRows(actual, objects).SequenceEqual(TextRows(expected, objects)), source + " packaged schema differs from source");
            foreach (var view in TextRows(actual, "SELECT name FROM sqlite_schema WHERE type='view'"))
                Execute(actual, $"SELECT * FROM \"{view}\" LIMIT 0");
            Check(TextRows(actual, "PRAGMA foreign_key_check").Count == 0, source + " foreign keys");
            Check(TextRows(actual, identity).SequenceEqual([expectedIdentity]), source + " database identity records the given ID and host");
            // Reopening must not recreate objects or lose an existing capture interval.
            Execute(actual, $"""
                INSERT INTO host_observations(observation_id,observed_at_utc_ms,platform,store_path) VALUES(1,1,'{platform}','test');
                INSERT INTO capture_runs(started_at_utc_ms,host_observation_id) VALUES(1,1);
                """);
        }
        using (var reopened = Open(path))
        {
            var schemaVersion = Number(reopened, "PRAGMA schema_version");
            ensureCreated(reopened, Guid.NewGuid().ToString("N"), platform, null);
            Check(Number(reopened, "PRAGMA schema_version") == schemaVersion, source + " matching schema is not written on reopen");
            Check(Number(reopened, "SELECT COUNT(*) FROM capture_runs WHERE collector_version IS NULL") == 1,
                source + " an older run retains an unknown collector version on reopen");
            Check(TextRows(reopened, identity).SequenceEqual([expectedIdentity]), source + " database identity changed on reopen");
        }
        Console.WriteLine($"PASS: {source} complete schema, views and reopen");
    }
}
finally { temporary.Delete(recursive: true); }

static void SchemaUpgrade(string temporary, Assembly assembly, SqliteConnection expected)
{
    var collector = new Orisumi.Capture.Hosting.Collector("browser", assembly);
    var expectedObjects = TextRows(expected, "SELECT sql FROM sqlite_schema WHERE sql IS NOT NULL ORDER BY rowid");
    const string columns = """
        SELECT t.name || ':' || quote(c.name) || ':' || quote(c.type) || ':' || c."notnull" || ':' || quote(c.dflt_value) || ':' || c.pk
        FROM sqlite_schema AS t, pragma_table_info(t.name) AS c
        WHERE t.type='table' AND t.name <> 'local_data' ORDER BY t.name,c.cid
        """;
    const string queries = """
        SELECT sql FROM sqlite_schema WHERE type IN ('index','view') AND sql IS NOT NULL ORDER BY type,name
        """;
    var upgrade = Instance.Create(Path.Combine(temporary, "upgrade"), "dev").Store;
    var (path, id, _, _, _) = upgrade.WritableDatabase(collector.Source);
    using (var old = Open(path))
    {
        PrepareOld(old, sql => sql.Replace("    collector_version TEXT,\n", "", StringComparison.Ordinal)
            .Replace(",\n    machine_guid TEXT", "", StringComparison.Ordinal)
            .Replace(",\n    user_sid TEXT", "", StringComparison.Ordinal));
        Execute(old, $"""
            INSERT INTO host_observations(observation_id,observed_at_utc_ms,platform,store_path) VALUES(1,1,'{Platforms.Current}','test');
            INSERT INTO capture_runs(started_at_utc_ms,host_observation_id) VALUES(42,1);
            CREATE TABLE local_data(value TEXT UNIQUE);
            INSERT INTO local_data VALUES('keep');
            """);
        using var identity = old.CreateCommand();
        identity.Execute("INSERT INTO database_identity VALUES(1,$p0,1,$p1,NULL)", id, Platforms.Current);
    }
    using (var upgraded = CollectorDatabase.Open(upgrade.Root, collector))
    {
        Check(TextRows(upgraded, columns).SequenceEqual(TextRows(expected, columns)), "upgraded tables have the fresh schema's columns");
        Check(TextRows(upgraded, queries).SequenceEqual(TextRows(expected, queries)), "upgraded views and named indexes match a fresh schema");
        Check(Number(upgraded, "SELECT started_at_utc_ms FROM capture_runs WHERE collector_version IS NULL") == 42, "existing run survives the upgrade");
        Check(TextRows(upgraded, "SELECT database_id || ':' || created_at_utc_ms FROM database_identity").SequenceEqual([$"{id}:1"]),
            "existing database identity survives the upgrade");
        Check(TextRows(upgraded, "SELECT value FROM local_data").SequenceEqual(["keep"]), "extra table survives the upgrade");
        Check(Number(upgraded, "SELECT count(*) FROM sqlite_schema WHERE tbl_name='local_data' AND type='index'") == 1,
            "SQLite auto-index survives the upgrade");
    }
    Check(upgrade.CurrentDatabase(collector.Source) == path && Path.GetFileNameWithoutExtension(path) == id, "upgrade keeps the current database");
    using (var reopened = Open(path))
    {
        var schemaVersion = Number(reopened, "PRAGMA schema_version");
        using var matching = CollectorDatabase.Open(upgrade.Root, collector);
        Check(Number(matching, "PRAGMA schema_version") == schemaVersion, "upgraded schema is not written on reopen");
    }
    Console.WriteLine("PASS: additive upgrade preserves rows, identity and extra tables, replaces views and indexes, and is idempotent");

    (string Before, string After, string[] Differences)[] refusals =
    [
        ("browser TEXT NOT NULL", "browser TEXT NOT NULL, removed TEXT", ["browser_runs.removed: column removed"]),
        ("browser TEXT NOT NULL", "old_browser TEXT NOT NULL", ["browser_runs.old_browser: column removed", "browser_runs.browser: new NOT NULL column has no default"]),
        ("browser TEXT NOT NULL", "browser INTEGER NOT NULL", ["browser_runs.browser: type changed"]),
        ("browser TEXT NOT NULL", "browser TEXT", ["browser_runs.browser: notnull changed"]),
        ("browser TEXT NOT NULL", "browser TEXT NOT NULL DEFAULT 'old'", ["browser_runs.browser: default changed"]),
        ("INTEGER PRIMARY KEY REFERENCES", "INTEGER REFERENCES", ["browser_runs.run_id: pk changed"]),
        ("    browser TEXT NOT NULL,\n", "", ["browser_runs.browser: new NOT NULL column has no default"]),
        (" REFERENCES capture_runs(run_id)", "", ["browser_runs.run_id: column definition changed"]),
        ("browser TEXT NOT NULL", "browser INTEGER DEFAULT 1", ["browser_runs.browser: type changed", "browser_runs.browser: notnull changed", "browser_runs.browser: default changed"]),
    ];
    for (var i = 0; i < refusals.Length; i++)
    {
        var (before, after, differences) = refusals[i];
        var instance = Instance.Create(Path.Combine(temporary, $"refuse-{i}"), i == 0 ? "prod" : "dev");
        var (oldPath, _, _, _, _) = instance.Store.WritableDatabase(collector.Source);
        using (var old = Open(oldPath))
        {
            PrepareOld(old, sql => sql.StartsWith("CREATE TABLE browser_runs ", StringComparison.Ordinal)
                ? sql.Replace(before, after, StringComparison.Ordinal) : sql);
            Execute(old, $"INSERT INTO database_identity VALUES(1,'00000000000000000000000000000001',1,'{Platforms.Current}',NULL,NULL)");
        }
        var original = File.ReadAllBytes(oldPath);
        try
        {
            using var unexpected = CollectorDatabase.Open(instance.Store.Root, collector);
            throw new Exception("accepted an incompatible schema: " + after);
        }
        catch (IOException error)
        {
            foreach (var difference in differences)
                Check(error.Message.Contains(difference, StringComparison.Ordinal), "missing schema diagnostic: " + error.Message);
            Check(error.Message.Contains("orisumi-dev reset", StringComparison.Ordinal) == (instance.Name == "dev"),
                "only dev instances suggest reset");
        }
        Check(File.ReadAllBytes(oldPath).SequenceEqual(original), "refused schema leaves database bytes unchanged");
    }
    Console.WriteLine("PASS: incompatible table changes report each difference and leave the database unchanged");

    void PrepareOld(SqliteConnection old, Func<string, string> tableSql)
    {
        foreach (var sql in expectedObjects)
        {
            if (sql.StartsWith("CREATE TABLE capture_read_failures ", StringComparison.Ordinal) ||
                sql.StartsWith("CREATE INDEX visits_by_time ", StringComparison.Ordinal) ||
                sql.StartsWith("CREATE INDEX browser_runs_by_profile ", StringComparison.Ordinal) ||
                sql.StartsWith("CREATE VIEW capture_coverage ", StringComparison.Ordinal)) continue;
            Execute(old, sql.StartsWith("CREATE TABLE ", StringComparison.Ordinal) ? tableSql(sql) : sql);
        }
        Execute(old, """
            CREATE INDEX browser_runs_by_profile ON browser_runs(run_id);
            CREATE INDEX obsolete_index ON browser_runs(browser_session);
            CREATE VIEW capture_coverage AS SELECT value FROM missing_view_source;
            CREATE VIEW obsolete_view AS SELECT 1;
            """);
    }
}

static async Task DescriptionShape(string executable, params string[] fields)
{
    using var output = new StringWriter();
    Check(await RunProcess.Run(RunProcess.Dotnet, [executable, "describe"], output, default) == 0, "describe exits successfully");
    using var json = JsonDocument.Parse(output.ToString());
    Check(json.RootElement.EnumerateObject().Select(field => field.Name).Order(StringComparer.Ordinal).SequenceEqual(fields),
        executable + " describe JSON fields");
}

static async Task SchemaShape(string executable, SqliteConnection expected)
{
    using var output = new StringWriter();
    Check(await RunProcess.Run(RunProcess.Dotnet, [executable, "schema"], output, default) == 0, "schema exits successfully");
    using var json = JsonDocument.Parse(output.ToString());
    var tables = json.RootElement.EnumerateArray().ToArray();
    var names = TextRows(expected, "SELECT name FROM sqlite_schema WHERE type='table' ORDER BY name");
    Check(tables.Select(table => table.GetProperty("name").GetString()).SequenceEqual(names),
        "schema lists only tables in name order");
    foreach (var table in tables)
    {
        using var query = expected.CreateCommand();
        query.CommandText = """SELECT name, type, "notnull", dflt_value, pk FROM pragma_table_info($name) ORDER BY cid""";
        query.Parameters.AddWithValue("$name", table.GetProperty("name").GetString()!);
        using var reader = query.ExecuteReader();
        foreach (var column in table.GetProperty("columns").EnumerateArray())
        {
            Check(reader.Read() && column.GetProperty("name").GetString() == reader.GetString(0) &&
                column.GetProperty("type").GetString() == reader.GetString(1) &&
                column.GetProperty("notnull").GetInt64() == reader.GetInt64(2) &&
                column.GetProperty("default").GetString() == (reader.IsDBNull(3) ? null : reader.GetString(3)) &&
                column.GetProperty("pk").GetInt64() == reader.GetInt64(4), "schema reproduces pragma_table_info in cid order");
        }
        Check(!reader.Read(), "schema includes every column");
    }
}

static async Task InvalidDescription(string json)
{
    try { await ProgramDescription.Read("/bin/sh", ["-c", "printf '%s\\n' \"$1\"", "_", json], default); }
    catch (InvalidDataException) { return; }
    throw new Exception("accepted invalid describe payload " + json);
}

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
