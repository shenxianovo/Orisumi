#:project ../src/Orisumi.Cli/Orisumi.Cli.csproj
#:property AssemblyName=CaptureCoverageChecks
#:property PublishAot=false
#:property JsonSerializerIsReflectionEnabledByDefault=true

using System.Text.Json;
using Microsoft.Data.Sqlite;
using Orisumi.Cli.Capture;

var temp = Directory.CreateTempSubdirectory("orisumi-coverage-");
try
{
    var raw = Directory.CreateDirectory(Path.Combine(temp.FullName, "raw"));
    using (var db = new SqliteConnection($"Data Source={Path.Combine(raw.FullName, "network.sqlite")};Pooling=False"))
    {
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            CREATE TABLE observations(scope TEXT,mode TEXT,started_at_utc_ms INTEGER,stopped_at_utc_ms INTEGER,evidence TEXT,detail TEXT);
            CREATE VIEW capture_coverage AS SELECT * FROM observations;
            INSERT INTO observations VALUES
                ('connection','snapshot',0,1000,'sampled','before'),
                ('connection','snapshot',1000,2000,'sampled','overlap'),
                ('connection','snapshot',2000,2000,'read_failed','point'),
                ('collector','snapshot',500,NULL,'unconfirmed','unclosed'),
                ('connection','snapshot',3000,4000,'sampled','after');
            """;
        command.ExecuteNonQuery();
    }
    using var output = new StringWriter();
    CaptureCoverage.Write(temp.FullName, DateTimeOffset.FromUnixTimeMilliseconds(1000), DateTimeOffset.FromUnixTimeMilliseconds(3000), output);
    using var report = JsonDocument.Parse(output.ToString());
    var sources = report.RootElement.GetProperty("sources").EnumerateArray().ToArray();
    Check(sources.Count(source => source.GetProperty("status").GetString() == "no_database") == 4, "missing source is explicit");
    var rows = sources.Single(source => source.GetProperty("source").GetString() == "network").GetProperty("evidence").EnumerateArray().ToArray();
    Check(rows.Length == 3, "half-open ranges retain overlap and point events but exclude both outer boundaries");
    Check(rows.Single(row => row.GetProperty("evidence").GetString() == "unconfirmed").GetProperty("stopped_at_utc_ms").ValueKind == JsonValueKind.Null,
        "an unknown end is not filled with the query endpoint");
    Check(Directory.GetFiles(raw.FullName, "*.sqlite").Length == 1, "query never creates missing source databases");
    Console.WriteLine("PASS: coverage bounds, missing sources and unconfirmed intervals");
}
finally { temp.Delete(true); }

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
