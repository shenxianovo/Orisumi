#:project ../src/Collectors/Orisumi.Collectors.Network/Orisumi.Collectors.Network.csproj
#:property AssemblyName=NetworkChecks
#:property JsonSerializerIsReflectionEnabledByDefault=true

using System.Text.Json;
using Microsoft.Data.Sqlite;
using Orisumi.Collectors.Network;

var root = Path.Combine(Path.GetTempPath(), "orisumi-network-checks-" + Guid.NewGuid());
Directory.CreateDirectory(root);
try
{
    ParserChecks();
    SparseChecks();
    StorageChecks();
    UnverifiedIdentityChecks();
    DuplicateTupleChecks();
    NativeIdentityChecks();
    SchemaChecks();
    Console.WriteLine("Network parser, identity, counter, gap and query checks passed.");
}
finally { Directory.Delete(root, recursive: true); }

void Assert(bool valid, string message)
{
    if (!valid) throw new Exception(message);
}

void ParserChecks()
{
    const string csv = """
        ,state,bytes_out,interface,bytes_in,
        some.app.42,,70,,90,
        tcp4 127.0.0.1:123<->127.0.0.1:456,Established,70,lo0,90,
        tcp6 fe80::1%en0.123<->::ffff:127.0.0.1.456,CloseWait,,en0,,
        udp6 *.5353<->*.*,,0,,0,
        "comma,app.43",,0,,0,
        """;
    var parsed = NettopSnapshot.Parse(csv, _ => new(100, "/test"));
    Assert(parsed.Count == 2 && parsed[1].Name == "comma,app", "process names and reordered columns");
    var sockets = parsed[0].Sockets;
    Assert(sockets[0].SentBytes == 70 && sockets[0].ReceivedBytes == 90, "counter direction");
    Assert(sockets[1].Key.RemoteAddress == "::ffff:127.0.0.1" && sockets[1].Key.RemotePort == 456, "IPv6 endpoint");
    Assert(sockets[1].ReceivedBytes is null && sockets[2].Key.RemotePort is null, "unknown counters and wildcard port");
    foreach (var broken in new[] { "", csv.Replace("bytes_out", "missing"), csv.Replace(",70,lo0,90,", ",7KB,lo0,90,"), csv + "\ninvalid,row" })
    {
        try { NettopSnapshot.Parse(broken, _ => new(100, null)); throw new Exception("accepted malformed CSV"); }
        catch (InvalidDataException) { }
    }
}

void StorageChecks()
{
    var store = Path.Combine(root, "store");
    var key = new SocketKey("tcp", 4, "127.0.0.1", 100, "127.0.0.1", 200, "lo0");
    ProcessSample Process(long? birth, long? received, long? sent, string state = "Established", int copies = 1) =>
        new(42, "test", new(birth, "/test"), received, sent,
            Enumerable.Range(0, copies).Select(_ => new SocketSample(key, state, received, sent)).ToList());
    using var writer = new NetworkDatabase(store, "boot", 1, 1000);
    using var db = new SqliteConnection($"Data Source={NetworkDatabase.PathFor(store)};Pooling=False");
    db.Open();
    long Scalar(string sql)
    {
        using var command = db.CreateCommand(); command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }
    writer.Sample(1000, 1000, 0, [Process(10, 10000, 20000)]);
    writer.Sample(2000, 2000, 1000, [Process(10, 10100, 20200)]);
    Assert(Scalar("SELECT COUNT(*) FROM socket_instances") == 1, "same socket should retain identity");
    Assert(Scalar("SELECT COUNT(*) FROM socket_state_observations") == 1, "unchanged state should not repeat");
    Assert(Scalar("SELECT SUM(sent_delta) FROM socket_traffic_intervals") == 200, "initial lifetime counter must not count");
    writer.Sample(3000, 3000, 2000, [Process(10, 10200, 20300, "CloseWait")]);
    writer.Sample(4000, 4000, 3000, [Process(10, 10300, 20400)]);
    Assert(Scalar("SELECT COUNT(*) FROM socket_state_observations") == 3, "state A-B-A history");
    using var output = new StringWriter();
    NetworkQuery.Write(store, DateTimeOffset.FromUnixTimeMilliseconds(1500), DateTimeOffset.FromUnixTimeMilliseconds(4000), true, output);
    using var report = JsonDocument.Parse(output.ToString());
    var summary = report.RootElement.GetProperty("summary")[0];
    Assert(summary.GetProperty("sent_bytes").GetInt64() == 200, "query should exclude straddling interval and include interval ending at boundary");
    Assert(summary.GetProperty("boundary_intervals").GetInt64() == 1, "query must expose crossing interval");
    writer.Sample(5000, 5000, 4000, [Process(10, 10, 20)]);
    Assert(Scalar("SELECT COUNT(*) FROM socket_instances") == 2, "counter reset starts a fresh observed segment");
    writer.Sample(6000, 6000, 5000, [Process(20, 30, 40)]);
    Assert(Scalar("SELECT COUNT(*) FROM process_instances") == 2, "PID reuse must create new process identity");
    writer.Sample(7000, 7000, 6000, [], "source failed");
    writer.Sample(8000, 8000, 7000, [Process(20, 300, 400)]);
    Assert(Scalar("SELECT COUNT(*) FROM socket_traffic_intervals WHERE sample_id=8 AND sent_delta IS NULL") == 1, "gap must not invent traffic");
    writer.Sample(20000, 20000, 19000, [Process(20, 500, 600)]);
    Assert(Scalar("SELECT continuous FROM samples WHERE sample_id=9") == 0, "sleep gap must break continuity");
    using var gapOutput = new StringWriter();
    NetworkQuery.Write(store, DateTimeOffset.FromUnixTimeMilliseconds(10000), DateTimeOffset.FromUnixTimeMilliseconds(15000), false, gapOutput);
    using var gapReport = JsonDocument.Parse(gapOutput.ToString());
    Assert(gapReport.RootElement.GetProperty("sampling_gaps").GetArrayLength() == 1, "query entirely inside a gap must expose that gap");
    writer.Sample(21000, 21000, 20000, [Process(20, 550, 650, copies: 2)]);
    Assert(Scalar("SELECT COUNT(*) FROM socket_instances WHERE ambiguous=1") == 2, "identical tuples are ambiguous");
    writer.Sample(22000, 22000, 21000, [Process(null, 550, 650)]);
    writer.Sample(23000, 23000, 22000, [Process(null, 560, 660)]);
    Assert(Scalar("SELECT COUNT(*) FROM process_instances WHERE started_at_utc_us IS NULL") == 1, "unverified PID keeps one explicitly uncertain observation segment");
    Assert(Scalar("SELECT COUNT(*) FROM process_traffic_intervals WHERE sample_id=12 AND sent_delta IS NULL") == 1, "uncertain process identity must not produce a verified delta");
    writer.Sample(24000, 24000, 23000, [Process(20, null, null)]);
    writer.Sample(25000, 25000, 24000, [Process(20, 600, 700)]);
    Assert(Scalar("SELECT COUNT(*) FROM socket_traffic_intervals WHERE sample_id=14 AND sent_delta IS NULL") == 1, "missing counter is not zero");
    writer.Sample(26000, 26000, 25000, []);
    Assert(Scalar("SELECT COUNT(*) FROM socket_presence_events WHERE sample_id=15 AND present=0") == 1, "disappearance is an observation");
    writer.Stop(26000, "stopped");
    Assert(Scalar("SELECT COUNT(*) FROM capture_runs WHERE stopped_at_utc_ms=26000") == 1, "close run");
    using var other = new NetworkDatabase(store, "boot", 1, 27000);
    other.Sample(27000, 27000, 0, [Process(20, 900, 900)]);
    Assert(Scalar("SELECT COUNT(*) FROM socket_traffic_intervals WHERE sample_id=16 AND sent_delta IS NULL") == 1, "new capture must not bridge stopped session");
}

void SparseChecks()
{
    var store = Path.Combine(root, "sparse");
    var key = new SocketKey("tcp", 4, "127.0.0.1", 100, "127.0.0.1", 200, "lo0");
    var process = new ProcessSample(42, "test", new(10, "/test"), 100, 200,
        [new SocketSample(key, "Established", 100, 200)]);
    using var writer = new NetworkDatabase(store, "boot", 1, 1000);
    writer.Sample(1000, 1000, 0, [process]);
    using var db = new SqliteConnection($"Data Source={NetworkDatabase.PathFor(store)};Pooling=False");
    db.Open();
    long Count(string table)
    {
        using var command = db.CreateCommand(); command.CommandText = $"SELECT COUNT(*) FROM {table}";
        return (long)command.ExecuteScalar()!;
    }
    string[] facts = ["process_instances", "process_presence_events", "process_name_observations", "process_path_observations",
        "process_traffic_observations", "socket_instances", "socket_presence_events", "socket_state_observations", "socket_traffic_observations"];
    var initial = facts.ToDictionary(table => table, Count);
    writer.Sample(2000, 2000, 1000, [process]);
    foreach (var table in facts) Assert(Count(table) == initial[table], $"unchanged {table} must not be written again");
    Assert(Value(db, "SELECT sent_delta FROM socket_traffic_intervals WHERE sample_id=2") == 0, "idle samples reconstruct zero traffic");

    process = process with { SentBytes = 250, Sockets = [process.Sockets[0] with { SentBytes = 250 }] };
    writer.Sample(3000, 3000, 2000, [process]);
    foreach (var entity in new[] { "process", "socket" })
    {
        Assert(Value(db, $"SELECT COUNT(*) FROM {entity}_traffic_observations WHERE direction='sent'") == 2, "sent change stored");
        Assert(Value(db, $"SELECT COUNT(*) FROM {entity}_traffic_observations WHERE direction='received'") == 1, "sent change does not duplicate received");
        Assert(Value(db, $"SELECT interval_start_utc_ms FROM {entity}_traffic_intervals WHERE sample_id=3") == 2000,
            "a change is bounded by the last successful sample, not by the last changed counter");
    }
    using var output = new StringWriter();
    NetworkQuery.Write(store, DateTimeOffset.FromUnixTimeMilliseconds(2000), DateTimeOffset.FromUnixTimeMilliseconds(3000), true, output);
    using var report = JsonDocument.Parse(output.ToString());
    Assert(report.RootElement.GetProperty("summary")[0].GetProperty("sent_bytes").GetInt64() == 50, "sparse query preserves exact interval attribution");
    Assert(report.RootElement.GetProperty("summary")[0].GetProperty("received_bytes").GetInt64() == 0, "known zero is not unknown");

    process = process with { Sockets = [process.Sockets[0] with { State = "CloseWait" }] };
    writer.Sample(4000, 4000, 3000, [process]);
    Assert(Count("socket_state_observations") == 2 && Count("socket_traffic_observations") == 3, "state changes independently of counters");
    process = process with { Name = "renamed" };
    writer.Sample(5000, 5000, 4000, [process]);
    Assert(Count("process_name_observations") == 2 && Count("process_path_observations") == 1, "name changes independently of path");
    process = process with { Identity = new(10, "/new-path") };
    writer.Sample(6000, 6000, 5000, [process]);
    Assert(Count("process_name_observations") == 2 && Count("process_path_observations") == 2, "path changes independently of name");

    process = process with { ReceivedBytes = null, Sockets = [process.Sockets[0] with { ReceivedBytes = null }] };
    writer.Sample(7000, 7000, 6000, [process]);
    writer.Sample(8000, 8000, 7000, [process]);
    Assert(Count("socket_traffic_observations") == 4, "repeated unknown is not another change");
    process = process with { ReceivedBytes = 150, Sockets = [process.Sockets[0] with { ReceivedBytes = 150 }] };
    writer.Sample(9000, 9000, 8000, [process]);
    Assert(Value(db, "SELECT COUNT(*) FROM socket_traffic_intervals WHERE sample_id IN (7,8,9) AND received_delta IS NULL") == 3,
        "unknown counters survive compression and recovery is a new baseline");
    Assert(Value(db, "SELECT COUNT(*) FROM socket_traffic_intervals WHERE sample_id IN (7,8,9) AND sent_delta=0") == 3,
        "missing received counters do not erase known sent counters");
    writer.Sample(10000, 10000, 9000, []);
    writer.Sample(11000, 11000, 10000, [process]);
    Assert(Value(db, "SELECT COUNT(*) FROM process_traffic_intervals WHERE sample_id=10") == 0, "missing process is not carried forward");
    Assert(Value(db, "SELECT COUNT(*) FROM process_traffic_intervals WHERE sample_id=11 AND sent_delta IS NULL") == 1,
        "reappearance establishes a baseline even for the same verified process");
    writer.Sample(12000, 12000, 11000, [], "failure");
    writer.Sample(13000, 13000, 12000, []);
    Assert(Value(db, "SELECT COUNT(*) FROM socket_traffic_intervals WHERE sample_id>=12") == 0, "a failed sample ends coverage of old presence");
    writer.Sample(14000, 14000, 13000, [process]);
    Assert(Value(db, "SELECT COUNT(*) FROM process_traffic_intervals WHERE sample_id=14 AND sent_delta IS NULL") == 1, "recovery never bridges a gap");
}

long Value(SqliteConnection db, string sql)
{
    using var command = db.CreateCommand(); command.CommandText = sql;
    return Convert.ToInt64(command.ExecuteScalar());
}

void UnverifiedIdentityChecks()
{
    var store = Path.Combine(root, "uncertain");
    var key = new SocketKey("tcp", 4, "127.0.0.1", 100, "127.0.0.1", 200, "lo0");
    var process = new ProcessSample(42, "test", new(null, null), 100, 200, [new(key, "Established", 100, 200)]);
    using var writer = new NetworkDatabase(store, "boot", 1, 1000);
    using var db = new SqliteConnection($"Data Source={NetworkDatabase.PathFor(store)};Pooling=False");
    db.Open();
    long time = 0;
    void Add(ProcessSample? sample, string? error = null)
    {
        time += 1000; writer.Sample(time, time, time - 1000, sample is null ? [] : [sample], error);
    }
    Add(process); Add(process); Add(process);
    Assert(Value(db, "SELECT COUNT(*) FROM process_instances") == 1 && Value(db, "SELECT COUNT(*) FROM socket_instances") == 1,
        "missing birth time does not create identities every sample");
    Assert(Value(db, "SELECT COUNT(*) FROM socket_traffic_observations") == 2, "uncertain but unchanged state is sparse too");
    process = process with { SentBytes = 210, Sockets = [process.Sockets[0] with { SentBytes = 210 }] };
    Add(process);
    Assert(Value(db, "SELECT COUNT(*) FROM socket_traffic_intervals WHERE sent_delta IS NOT NULL") == 0, "uncertain socket correspondence never yields a verified delta");
    using var output = new StringWriter();
    NetworkQuery.Write(store, DateTimeOffset.FromUnixTimeMilliseconds(0), DateTimeOffset.FromUnixTimeMilliseconds(5000), false, output);
    using var report = JsonDocument.Parse(output.ToString());
    var row = report.RootElement.GetProperty("summary")[0];
    Assert(row.GetProperty("process_identity_verified").GetInt64() == 0 && row.GetProperty("sent_bytes").ValueKind == JsonValueKind.Null,
        "query exposes uncertainty rather than just an apparently stable ID");
    Add(null); Add(process);
    Assert(Value(db, "SELECT COUNT(*) FROM process_instances") == 2, "unverified PID cannot bridge absence");
    Add(null, "failure"); Add(process);
    Assert(Value(db, "SELECT COUNT(*) FROM process_instances") == 3, "unverified PID cannot bridge a sampling failure");
    process = process with { Name = "another" }; Add(process);
    Assert(Value(db, "SELECT COUNT(*) FROM process_instances") == 4, "changed identity evidence breaks the uncertain segment");
    process = process with { SentBytes = 1 }; Add(process);
    Assert(Value(db, "SELECT COUNT(*) FROM process_instances") == 5, "counter rollback breaks uncertain process continuity");
    process = process with { Identity = new(10, "/test") }; Add(process);
    Assert(Value(db, "SELECT COUNT(*) FROM process_instances") == 6, "newly verified identity does not retroactively certify old observations");
}

void DuplicateTupleChecks()
{
    var store = Path.Combine(root, "duplicates");
    var key = new SocketKey("udp", 4, "*", 100, "*", null, null);
    var process = new ProcessSample(42, "test", new(10, "/test"), 0, 0,
        [new(key, null, null, null), new(key, null, 10, 20)]);
    using var writer = new NetworkDatabase(store, "boot", 1, 1000);
    using var db = new SqliteConnection($"Data Source={NetworkDatabase.PathFor(store)};Pooling=False");
    db.Open();
    writer.Sample(1000, 1000, 0, [process]);
    process = process with { Sockets = process.Sockets.AsEnumerable().Reverse().ToList() };
    writer.Sample(2000, 2000, 1000, [process]);
    Assert(Value(db, "SELECT COUNT(*) FROM socket_instances") == 2 && Value(db, "SELECT COUNT(*) FROM socket_traffic_observations") == 4,
        "unchanged duplicate tuples are a stable multiset, independent of row order");
    Assert(Value(db, "SELECT COUNT(*) FROM socket_traffic_intervals WHERE sent_delta IS NOT NULL") == 0, "duplicate tuples cannot be individually matched");
    process = process with { Sockets = [new(key, null, null, null), new(key, null, 11, 21)] };
    writer.Sample(3000, 3000, 2000, [process]);
    Assert(Value(db, "SELECT COUNT(*) FROM socket_instances") == 4, "changed ambiguous multiset starts new observations instead of guessing identities");
}

void NativeIdentityChecks()
{
    if (!OperatingSystem.IsMacOS()) return;
    var now = DateTimeOffset.UtcNow;
    var own = NettopSnapshot.Identify(Environment.ProcessId, now);
    Assert(own.StartedAtUtcUs is > 0 && own.ExecutablePath is not null, "read own native identity and path");
    using var process = System.Diagnostics.Process.GetCurrentProcess();
    var managed = (process.StartTime.ToUniversalTime().Ticks - DateTime.UnixEpoch.Ticks) / 10;
    Assert(own.StartedAtUtcUs == managed, "native start time matches the readable .NET value at microsecond precision");
    Assert(NettopSnapshot.Identify(1, now).StartedAtUtcUs is > 0, "system process start time is readable without root");
    Assert(NettopSnapshot.Identify(int.MaxValue, now).StartedAtUtcUs is null, "exited PID remains unknown");
    Assert(NettopSnapshot.Identify(Environment.ProcessId, DateTimeOffset.UnixEpoch).StartedAtUtcUs is null, "PID born after snapshot cannot be attributed");
}

void SchemaChecks()
{
    var store = Path.Combine(root, "old-schema");
    Directory.CreateDirectory(Path.Combine(store, "raw"));
    using var db = new SqliteConnection($"Data Source={NetworkDatabase.PathFor(store)};Pooling=False");
    db.Open();
    using var command = db.CreateCommand();
    command.CommandText = "CREATE TABLE capture_runs(original TEXT); INSERT INTO capture_runs VALUES('preserve')";
    command.ExecuteNonQuery();
    try { using var writer = new NetworkDatabase(store, "boot", 1, 1000); throw new Exception("accepted old schema"); }
    catch (IOException) { }
    Assert(Value(db, "SELECT COUNT(*) FROM capture_runs WHERE original='preserve'") == 1, "old databases are rejected without modification");
}
