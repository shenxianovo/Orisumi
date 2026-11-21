#:project ../src/Orisumi.Cli/Orisumi.Cli.csproj
#:property AssemblyName=CaptureCoverageChecks
#:property PublishAot=false
#:property JsonSerializerIsReflectionEnabledByDefault=true

using System.Diagnostics;
using System.Text.Json;
using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using Orisumi.Cli;
using Orisumi.Platform;

[assembly: SupportedOSPlatform("macos")]

var temp = Directory.CreateTempSubdirectory("orisumi-coverage-");
try
{
    var store = new Store(temp.FullName);
    const string copied = "other-device-only";
    // An earlier database of the same source keeps its own evidence.
    Coverage("claude", "0123456789abcdef0123456789abcdef", """
        ('project_logs','replay',0,1000,'imported','before'),
        ('project_logs','replay',1000,2000,'imported','overlap'),
        ('project_logs','replay',2000,2000,'read_failed','point'),
        ('project_logs','replay',500,NULL,'unconfirmed','unclosed'),
        ('project_logs','replay',3000,4000,'imported','after')
        """);
    Coverage("claude", "fedcba9876543210fedcba9876543210", "('project_logs','replay',2500,2600,'imported','later database')");
    Coverage(copied, "abcdefabcdefabcdefabcdefabcdefab", "('copied','replay',1000,2000,'imported','copied from another device')",
        "other-hardware");
    Coverage(copied, "abcdefabcdefabcdefabcdefabcdef00", "('copied','replay',1000,2000,'imported','written here')",
        HostIdentity.Read().HardwareUuid ?? "unknown");
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    var coverage = await Process.RunAndCaptureTextAsync(RunProcess.Dotnet, ["src/Orisumi.Cli/bin/Debug/net11.0/orisumi.dll",
        "--store", store.Root, "capture", "coverage", "--from", "1970-01-01T00:00:01Z", "--to", "1970-01-01T00:00:03Z"], timeout.Token);
    Check(coverage.ExitStatus is { ExitCode: 0, Canceled: false }, coverage.StandardError);
    using var report = JsonDocument.Parse(coverage.StandardOutput);
    var sources = report.RootElement.GetProperty("sources").EnumerateArray().ToArray();
    Check(sources.Where(source => source.GetProperty("status").GetString() == "no_database")
        .Select(source => source.GetProperty("source").GetString()).SequenceEqual(["browser", "codex", "macos"]),
        "each installed collector missing a database has an explicit row");
    var copiedRows = new[] { "abcdefabcdefabcdefabcdefabcdefab", "abcdefabcdefabcdefabcdefabcdef00" }
        .Select(id => sources.Single(source => source.GetProperty("database").GetString() == id)).ToArray();
    Check(copiedRows[0].GetProperty("status").GetString() == "queried" &&
        copiedRows[0].GetProperty("evidence")[0].GetProperty("detail").GetString() == "copied from another device",
        "a copied source without a collector is queried");
    Check(copiedRows[0].GetProperty("platform").GetString() == "macos" &&
        copiedRows[0].GetProperty("hardware_uuid").GetString() == "other-hardware" && !copiedRows[0].GetProperty("this_host").GetBoolean() &&
        copiedRows[1].GetProperty("this_host").GetBoolean(), "each database names the host it was created on and whether that is this one");
    Check(sources.Where(source => source.GetProperty("source").GetString() != copied).All(source =>
        source.GetProperty("this_host").GetBoolean() && source.GetProperty("platform").ValueKind == JsonValueKind.Null),
        "missing databases and databases without an identity count as this host's");
    var queried = sources.Where(source => source.GetProperty("source").GetString() == "claude").ToArray();
    Check(queried.Select(source => source.GetProperty("database").GetString())
        .SequenceEqual(["0123456789abcdef0123456789abcdef", "fedcba9876543210fedcba9876543210"]), "every database of a source is queried and named");
    Check(queried[1].GetProperty("evidence").GetArrayLength() == 1, "a later database reports its own evidence");
    var rows = queried[0].GetProperty("evidence").EnumerateArray().ToArray();
    Check(rows.Length == 3, "half-open ranges retain overlap and point events but exclude both outer boundaries");
    Check(rows.Single(row => row.GetProperty("evidence").GetString() == "unconfirmed").GetProperty("stopped_at_utc_ms").ValueKind == JsonValueKind.Null,
        "an unknown end is not filled with the query endpoint");
    Check(Directory.GetDirectories(Path.Combine(temp.FullName, "observations")).Length == 2 && !Directory.Exists(store.State),
        "query never creates missing source databases");
    Console.WriteLine("PASS: coverage bounds, missing sources, copied source without a collector, database hosts, several databases and unconfirmed intervals");

    void Coverage(string source, string databaseId, string rows, string? hardwareUuid = null)
    {
        var directory = Directory.CreateDirectory(store.Observations(source));
        using var db = new SqliteConnection($"Data Source={Path.Combine(directory.FullName, databaseId + ".sqlite")};Pooling=False");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = $"""
            CREATE TABLE observations(scope TEXT,mode TEXT,started_at_utc_ms INTEGER,stopped_at_utc_ms INTEGER,evidence TEXT,detail TEXT);
            CREATE VIEW capture_coverage AS SELECT * FROM observations;
            INSERT INTO observations VALUES {rows};
            {(hardwareUuid is null ? "" : $"""
                CREATE TABLE database_identity (singleton INTEGER PRIMARY KEY, database_id TEXT, created_at_utc_ms INTEGER,
                    platform TEXT, hardware_uuid TEXT);
                INSERT INTO database_identity VALUES (1, '{databaseId}', 0, 'macos', '{hardwareUuid}');
                """)}
            """;
        command.ExecuteNonQuery();
    }
}
finally { temp.Delete(true); }

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
