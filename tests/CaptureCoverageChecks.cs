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
    Coverage(copied, "abcdefabcdefabcdefabcdefabcdefab", "('copied','replay',1000,2000,'imported','copied from another device')");
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    var coverage = await Process.RunAndCaptureTextAsync(RunProcess.Dotnet, ["src/Orisumi.Cli/bin/Debug/net11.0/orisumi.dll",
        "--store", store.Root, "capture", "coverage", "--from", "1970-01-01T00:00:01Z", "--to", "1970-01-01T00:00:03Z"], timeout.Token);
    Check(coverage.ExitStatus is { ExitCode: 0, Canceled: false }, coverage.StandardError);
    using var report = JsonDocument.Parse(coverage.StandardOutput);
    var sources = report.RootElement.GetProperty("sources").EnumerateArray().ToArray();
    Check(sources.Where(source => source.GetProperty("status").GetString() == "no_database")
        .Select(source => source.GetProperty("source").GetString()).SequenceEqual(["browser", "codex", "macos"]),
        "each installed collector missing a database has an explicit row");
    var copiedRow = sources.Single(source => source.GetProperty("source").GetString() == copied);
    Check(copiedRow.GetProperty("status").GetString() == "queried" &&
        copiedRow.GetProperty("evidence")[0].GetProperty("detail").GetString() == "copied from another device",
        "a copied source without a collector is queried");
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
    Console.WriteLine("PASS: coverage bounds, missing sources, copied source without a collector, several databases and unconfirmed intervals");

    void Coverage(string source, string databaseId, string rows)
    {
        var directory = Directory.CreateDirectory(store.Observations(source));
        using var db = new SqliteConnection($"Data Source={Path.Combine(directory.FullName, databaseId + ".sqlite")};Pooling=False");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = $"""
            CREATE TABLE observations(scope TEXT,mode TEXT,started_at_utc_ms INTEGER,stopped_at_utc_ms INTEGER,evidence TEXT,detail TEXT);
            CREATE VIEW capture_coverage AS SELECT * FROM observations;
            INSERT INTO observations VALUES {rows};
            """;
        command.ExecuteNonQuery();
    }
}
finally { temp.Delete(true); }

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
