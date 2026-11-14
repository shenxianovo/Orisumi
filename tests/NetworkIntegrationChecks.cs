#:project ../src/Collectors/Orisumi.Collectors.Network/Orisumi.Collectors.Network.csproj

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;

if (!OperatingSystem.IsMacOS())
{
    Console.WriteLine("Skipped: nettop requires macOS.");
    return;
}
var root = Directory.GetParent(Path.GetDirectoryName(SourceFile())!)!.FullName;
var cli = Path.Combine(root, "src/Orisumi.Cli/bin/Debug/net11.0/Orisumi");
if (!File.Exists(cli)) throw new Exception("Run dotnet build Orisumi.slnx first.");
await LiveChecks();
await ArgumentChecks();
Console.WriteLine("Network CLI, live TCP/UDP, exclusive capture, shutdown and duration checks passed.");

static string SourceFile([CallerFilePath] string path = "") => path;
static void Assert(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
CliProcess Start(string store, params string[] arguments) => new(cli, ["--store", store, "network", .. arguments]);
async Task<(int Code, string Output, string Error)> Run(string store, params string[] arguments)
{
    using var process = Start(store, arguments);
    return await process.Complete();
}
static SqliteConnection Open(string store)
{
    var db = new SqliteConnection(new SqliteConnectionStringBuilder
    {
        DataSource = Path.Combine(store, "raw/network.sqlite"), Mode = SqliteOpenMode.ReadOnly, Pooling = false,
    }.ToString());
    db.Open();
    return db;
}
static long Number(SqliteConnection db, string sql)
{
    using var command = db.CreateCommand(); command.CommandText = sql;
    return Convert.ToInt64(command.ExecuteScalar());
}

async Task LiveChecks()
{
    var store = Path.Combine(Path.GetTempPath(), "orisumi-network-live-" + Guid.NewGuid());
    Directory.CreateDirectory(store);
    var started = DateTimeOffset.UtcNow.AddSeconds(-1);
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    using var client = new TcpClient();
    using var stop = new CancellationTokenSource();
    Task? traffic = null;
    try
    {
        await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        using var server = await listener.AcceptTcpClientAsync();
        using var udpClient = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var udpServer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        udpClient.Connect((IPEndPoint)udpServer.Client.LocalEndPoint!);
        udpServer.Connect((IPEndPoint)udpClient.Client.LocalEndPoint!);
        traffic = Generate();
        using var collector = Start(store, "capture", "--interval", "1");
        try
        {
            var wait = Stopwatch.StartNew();
            var ready = false;
            while (wait.Elapsed < TimeSpan.FromSeconds(15))
            {
                if (collector.HasExited) throw new Exception($"Collector exited: {(await collector.Complete()).Error}");
                if (File.Exists(Path.Combine(store, "raw/network.sqlite")))
                {
                    using var db = Open(store);
                    if (Number(db, "SELECT COUNT(*) FROM sqlite_master WHERE name='samples'") == 1 &&
                        Number(db, "SELECT COUNT(*) FROM samples") >= 4)
                    {
                        ready = true;
                        break;
                    }
                }
                await Task.Delay(100);
            }
            Assert(ready, "No complete samples arrived");
            var duplicate = await Run(store, "capture", "1");
            Assert(duplicate.Code != 0, "A second collector must not acquire the same store");
            await collector.Terminate();
            var result = await collector.Complete();
            Assert(result.Code == 0, result.Error);
            using (var db = Open(store))
            {
                Assert(Number(db, "SELECT COUNT(*) FROM capture_runs") == 1, "one capture run");
                Assert(Number(db, "SELECT COUNT(*) FROM capture_runs WHERE stopped_at_utc_ms IS NOT NULL") == 1, "SIGTERM closes the run");
                Assert(Number(db, "SELECT COUNT(*) FROM samples WHERE error IS NOT NULL") == 0, "no failed samples");
                foreach (var protocol in new[] { "tcp", "udp" })
                {
                    using var command = db.CreateCommand();
                    command.CommandText = """
                        SELECT SUM(t.sent_delta) FROM socket_traffic_intervals t
                        JOIN socket_instances i USING(socket_id) JOIN process_instances p USING(process_instance_id)
                        WHERE p.pid=$pid AND i.protocol=$protocol
                        """;
                    command.Parameters.AddWithValue("$pid", Environment.ProcessId);
                    command.Parameters.AddWithValue("$protocol", protocol);
                    var bytes = command.ExecuteScalar();
                    Assert(bytes is long count && count > 0, $"No {protocol} traffic attributed to the test process");
                }
            }
            var query = await Run(store, "query", "--from", started.ToString("O"), "--to", DateTimeOffset.UtcNow.AddSeconds(1).ToString("O"), "--details");
            Assert(query.Code == 0, query.Error);
            using var report = JsonDocument.Parse(query.Output);
            Assert(report.RootElement.GetProperty("source").GetString() == "nettop", "query source");
            Assert(report.RootElement.GetProperty("summary").EnumerateArray().Any(row =>
                row.GetProperty("pid").GetInt64() == Environment.ProcessId &&
                row.GetProperty("sent_bytes").ValueKind == JsonValueKind.Number && row.GetProperty("sent_bytes").GetInt64() > 0),
                "CLI query includes test traffic");
            Assert(report.RootElement.GetProperty("socket_intervals").GetArrayLength() > 0, "CLI details include intervals");
        }
        finally
        {
            // Finish traffic before disposing its sockets; surface any actual traffic failure.
            stop.Cancel();
            await traffic;
            traffic = null;
        }

        async Task Generate()
        {
            var tcpBytes = new byte[4096];
            var udpBytes = new byte[1024];
            var received = new byte[tcpBytes.Length];
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    await client.GetStream().WriteAsync(tcpBytes, stop.Token);
                    await server.GetStream().ReadExactlyAsync(received, stop.Token);
                    await udpClient.SendAsync(udpBytes.AsMemory(), stop.Token);
                    await udpServer.ReceiveAsync(stop.Token);
                    await Task.Delay(20, stop.Token);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
    }
    finally
    {
        stop.Cancel();
        if (traffic is not null) await traffic;
        listener.Stop();
        Directory.Delete(store, recursive: true);
    }
}

async Task ArgumentChecks()
{
    var store = Path.Combine(Path.GetTempPath(), "orisumi-network-duration-" + Guid.NewGuid());
    Directory.CreateDirectory(store);
    try
    {
        string[][] invalid = [["capture", "0"], ["capture", "1", "--interval", "0"],
            ["query", "--from", "2026-11-13T00:00:00", "--to", "2026-11-14T00:00:00Z"]];
        foreach (var arguments in invalid)
            Assert((await Run(store, arguments)).Code != 0, "Invalid arguments must fail");
        var result = await Run(store, "capture", "2", "--interval", "1");
        Assert(result.Code == 0, result.Error);
        using var db = Open(store);
        Assert(Number(db, "SELECT COUNT(*) FROM samples") > 0, "duration capture writes samples");
        Assert(Number(db, "SELECT COUNT(*) FROM capture_runs WHERE stopped_at_utc_ms IS NOT NULL") == 1, "duration capture closes its run");
    }
    finally { Directory.Delete(store, recursive: true); }
}

sealed class CliProcess : IDisposable
{
    private readonly Process process;
    private readonly Task<string> output;
    private readonly Task<string> error;
    internal bool HasExited => process.HasExited;

    internal CliProcess(string executable, string[] arguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        process = Process.Start(start) ?? throw new Exception($"Cannot start {executable}");
        output = process.StandardOutput.ReadToEndAsync();
        error = process.StandardError.ReadToEndAsync();
    }

    internal async Task<(int Code, string Output, string Error)> Complete()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await process.WaitForExitAsync(timeout.Token);
        return (process.ExitCode, await output, await error);
    }

    internal async Task Terminate()
    {
        using var signal = new CliProcess("/bin/kill", ["-TERM", process.Id.ToString()]);
        var result = await signal.Complete();
        if (result.Code != 0) throw new Exception(result.Error);
    }

    public void Dispose()
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
        }
        process.Dispose();
    }
}
