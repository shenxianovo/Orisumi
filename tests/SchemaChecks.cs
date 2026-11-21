#:project ../src/Orisumi.Cli/Orisumi.Cli.csproj
#:property PublishAot=false
#:property AssemblyName=SchemaChecks

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using System.Text.Json;
using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using Orisumi.Cli;
using Orisumi.Platform;

[assembly: SupportedOSPlatform("macos")]

var root = Directory.GetParent(Path.GetDirectoryName(SourceFile())!)!.FullName;
var temporary = Directory.CreateTempSubdirectory("orisumi-schema-");
const string prefix = "Orisumi.Collectors.";
var projects = Directory.GetDirectories(Path.Combine(root, "src/Collectors"), prefix + "*")
    .Select(path => Path.GetFileName(path)!).Order(StringComparer.Ordinal).ToArray();
var solution = XDocument.Load(Path.Combine(root, "Orisumi.slnx")).Descendants("Project")
    .Select(project => (string?)project.Attribute("Path")).ToArray();
var programs = await CollectorPrograms.Read(temporary.FullName, CancellationToken.None, Path.Combine(root, "src/Orisumi.Cli/bin/Debug/net11.0"));
Check(programs.Length == projects.Length, "build one executable per collector");
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
    var host = Path.Combine(root, "src/Orisumi.Cli/bin/Debug/net11.0/orisumi.dll");
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
        var source = project[prefix.Length..];
        Check(solution.Count(path => path == $"src/Collectors/{project}/{project}.csproj") == 1,
            project + " appears in Orisumi.slnx once");
        var projectDirectory = Path.Combine(root, "src/Collectors", project) + Path.DirectorySeparatorChar;
        var program = programs.Single(program => program.Path.StartsWith(projectDirectory, StringComparison.Ordinal));
        var name = source.ToLowerInvariant();
        Check(program.Description.Name == name && Path.GetFileName(program.Path) == $"orisumi-{name}.dll", project + " executable and describe name");
        if (expectedArguments.TryGetValue(name, out var arguments))
            Check(program.Description.Arguments!.SequenceEqual(arguments), project + " describe arguments");
        await DescriptionShape(program.Path, "arguments", "name", "version");
        // Each database has the shared Storage/Schema tables; loaders order both sets by file name.
        string[] schemas = [
            Path.Combine(root, "src/Collectors", project, "Schema"),
            Path.Combine(root, "src/Collectors/Storage/Schema"),
        ];
        using var expected = Open(":memory:");
        foreach (var group in new[] { "Tables", "Indexes", "Views" })
            foreach (var file in schemas.Select(schema => Path.Combine(schema, group)).Where(Directory.Exists)
                         .SelectMany(directory => Directory.EnumerateFiles(directory, "*.sql")).OrderBy(Path.GetFileName, StringComparer.Ordinal))
                Execute(expected, File.ReadAllText(file));
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

        var initializer = assembly.GetType("Orisumi.Collectors.Storage.SqliteSchema")!;
        var ensureCreated = initializer.GetMethod("EnsureCreated", BindingFlags.Static | BindingFlags.NonPublic)!
            .CreateDelegate<Action<SqliteConnection, string, string, string?>>();
        var newId = Guid.NewGuid().ToString("N");
        var path = Path.Combine(temporary.FullName, newId + ".sqlite");
        const string platform = "macos";
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
            Execute(actual, """
                INSERT INTO host_observations(observation_id,observed_at_utc_ms,platform,store_path) VALUES(1,1,'macos','test');
                INSERT INTO capture_runs(started_at_utc_ms,host_observation_id) VALUES(1,1);
                """);
        }
        using (var reopened = Open(path))
        {
            ensureCreated(reopened, Guid.NewGuid().ToString("N"), platform, null);
            Check(Number(reopened, "SELECT COUNT(*) FROM capture_runs WHERE collector_version IS NULL") == 1,
                source + " an older run retains an unknown collector version on reopen");
            Check(TextRows(reopened, identity).SequenceEqual([expectedIdentity]), source + " database identity changed on reopen");
        }
        Console.WriteLine($"PASS: {source} complete schema, views and reopen");
    }
}
finally { temporary.Delete(recursive: true); }

static async Task DescriptionShape(string executable, params string[] fields)
{
    using var output = new StringWriter();
    Check(await RunProcess.Run(RunProcess.Dotnet, [executable, "describe"], output, default) == 0, "describe exits successfully");
    using var json = JsonDocument.Parse(output.ToString());
    Check(json.RootElement.EnumerateObject().Select(field => field.Name).Order(StringComparer.Ordinal).SequenceEqual(fields),
        executable + " describe JSON fields");
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
