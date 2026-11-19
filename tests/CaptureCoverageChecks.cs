#:project ../src/Orisumi.Cli/Orisumi.Cli.csproj
#:property AssemblyName=CaptureCoverageChecks
#:property PublishAot=false
#:property JsonSerializerIsReflectionEnabledByDefault=true

using System.Text.Json;
using Microsoft.Data.Sqlite;
using Orisumi.Cli.Capture;
using Orisumi.Platform;

var temp = Directory.CreateTempSubdirectory("orisumi-coverage-");
try
{
    var store = new Store(temp.FullName);
    var claude = Directory.CreateDirectory(store.Observations("claude"));
    // An earlier database of the same source keeps its own evidence.
    Coverage("0123456789abcdef0123456789abcdef", """
        ('project_logs','replay',0,1000,'imported','before'),
        ('project_logs','replay',1000,2000,'imported','overlap'),
        ('project_logs','replay',2000,2000,'read_failed','point'),
        ('project_logs','replay',500,NULL,'unconfirmed','unclosed'),
        ('project_logs','replay',3000,4000,'imported','after')
        """);
    Coverage("fedcba9876543210fedcba9876543210", "('project_logs','replay',2500,2600,'imported','later database')");
    using var output = new StringWriter();
    CaptureCoverage.Write(temp.FullName, DateTimeOffset.FromUnixTimeMilliseconds(1000), DateTimeOffset.FromUnixTimeMilliseconds(3000), output);
    using var report = JsonDocument.Parse(output.ToString());
    var sources = report.RootElement.GetProperty("sources").EnumerateArray().ToArray();
    Check(sources.Count(source => source.GetProperty("status").GetString() == "no_database") == CaptureSources.All.Length - 1, "missing source is explicit");
    var queried = sources.Where(source => source.GetProperty("source").GetString() == "claude").ToArray();
    Check(queried.Select(source => source.GetProperty("database").GetString())
        .SequenceEqual(["0123456789abcdef0123456789abcdef", "fedcba9876543210fedcba9876543210"]), "every database of a source is queried and named");
    Check(queried[1].GetProperty("evidence").GetArrayLength() == 1, "a later database reports its own evidence");
    var rows = queried[0].GetProperty("evidence").EnumerateArray().ToArray();
    Check(rows.Length == 3, "half-open ranges retain overlap and point events but exclude both outer boundaries");
    Check(rows.Single(row => row.GetProperty("evidence").GetString() == "unconfirmed").GetProperty("stopped_at_utc_ms").ValueKind == JsonValueKind.Null,
        "an unknown end is not filled with the query endpoint");
    Check(Directory.GetDirectories(Path.Combine(temp.FullName, "observations")).Length == 1 && !Directory.Exists(store.State),
        "query never creates missing source databases");
    Console.WriteLine("PASS: coverage bounds, missing sources, several databases and unconfirmed intervals");

    void Coverage(string databaseId, string rows)
    {
        using var db = new SqliteConnection($"Data Source={Path.Combine(claude.FullName, databaseId + ".sqlite")};Pooling=False");
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
