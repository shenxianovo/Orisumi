#:project ../Shared/Desktop/Orisumi.Desktop.csproj
#:project ../Shared/Capture/Orisumi.Capture.csproj
#:property AssemblyName=WindowsSupervisorChecks
#:property PublishAot=false

using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Orisumi.Desktop;
using Orisumi.Core;

if (args is ["fixture", var marker])
{
    return await RunProcess.WithCancellation(async token =>
    {
        Console.WriteLine(marker);
        Console.Error.WriteLine("stderr " + marker);
        try { await Task.Delay(Timeout.Infinite, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        Console.WriteLine("stopped " + marker);
        return 0;
    });
}

var temp = Directory.CreateTempSubdirectory("orisumi-supervisor-");
var instance = Instance.Create(temp.FullName, "test_supervisor");
var store = instance.Store;
var files = new JobFiles(store);
var supervisor = Path.GetFullPath("Host/Platforms/Windows/Supervisor/bin/Debug/net11.0/orisumi-supervisor.exe");
var claude = Path.GetFullPath("Collectors/Claude/bin/Debug/net11.0/orisumi-claude.exe");
var config = temp.CreateSubdirectory("fixture Claude config").FullName;
Directory.CreateDirectory(Path.Combine(config, "projects"));
var fixture = Spec("fixture", [RunProcess.Dotnet, Assembly.GetExecutingAssembly().Location, "fixture", "one"]);
var actual = Spec("claude", [claude, "--store", store.Root, "capture"]);
Process? host = null;
try
{
    Check(ChromeExtension.Id(@"c:\路径\My Ext") == "kdflinljnednibelfnmccpbalmoehcno" &&
        ChromeExtension.Id(@"C:\路径\My Ext\") == "epfmihnfmhabmgmpfajbeelgnionchgh" &&
        ChromeExtension.Id(@"c:\MYEXT~1") == "fcdijjoooijcgfgfpnpfoogogmkpmkid", "Windows extension ID path bytes");
    Set("fixture", fixture);
    Set("claude", actual);
    host = Start();
    var first = await Running("fixture");
    await Until(() => Log(fixture.StandardOutput).Contains("one") && Log(fixture.StandardError).Contains("stderr one"), "redirected logs");
    using (var duplicate = Start())
    {
        await duplicate.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Check(duplicate.ExitCode != 0 && !host.HasExited, "exclusive supervisor lock");
    }
    var initial = await Running("claude");
    await Until(() => Count("confirmed_until_utc_ms IS NOT NULL") == 1, "initial Claude run");
    using (var child = Process.GetProcessById(initial.Pid!.Value))
    {
        child.Kill();
        await child.WaitForExitAsync();
    }
    await Until(() => files.Status().GetValueOrDefault("claude") is { State: "waiting", LastExitCode: not null }, "waiting after crash");
    var restarted = await Running("claude", initial.Pid);
    var delta = restarted.StartedAtUtcMs - initial.StartedAtUtcMs;
    Check(delta is >= 9900 and <= 20000, $"crash throttle: {delta}ms");
    await Until(() => Count("confirmed_until_utc_ms IS NOT NULL AND stop_reason IS NULL AND stopped_at_utc_ms IS NULL") == 2, "unconfirmed killed run");
    Set("claude", null);
    await Until(() => !files.Status().ContainsKey("claude") && Count("stop_reason='completed' AND stopped_at_utc_ms IS NOT NULL") == 1, "graceful Claude stop");
    var changed = fixture with { Command = [.. fixture.Command[..^1], "two"] };
    Set("fixture", changed);
    var replacement = await Running("fixture", first.Pid);
    await Until(() => Log(fixture.StandardOutput).Contains("stopped one") && Log(fixture.StandardOutput).Contains("two"), "append logs across desired changes");
    Check(replacement.Hash == JobFiles.Hash(JsonSerializer.SerializeToUtf8Bytes(changed, JobJson.Default.JobSpec)), "applied hash");
    using (var child = Process.GetProcessById(replacement.Pid!.Value))
    {
        host.Kill();
        await host.WaitForExitAsync();
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Check(!files.Alive(), "job ends children with supervisor");
    }
    host.Dispose();
    host = Start();
    await Running("fixture", replacement.Pid);
    Set("fixture", null);
    await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
    Check(host.ExitCode == 0 && !files.Alive() && files.Status().Count == 0, "empty desired set exits cleanly");
    await CheckCaptureTask();
    await CheckStopEventCallbackMayDispose();
    Console.WriteLine("PASS: status, append logs, lock, throttle, crash and graceful run records, desired changes, job lifetime and empty exit");
    return 0;
}
finally
{
    if (host is { HasExited: false }) { host.Kill(); await host.WaitForExitAsync(); }
    host?.Dispose();
    SqliteConnection.ClearAllPools();
    temp.Delete(true);
}

// A cancellation continuation can run on the stop-event callback and reach CaptureLifetime.Dispose there,
// which must not wait for the callback it is running on.
async Task CheckStopEventCallbackMayDispose()
{
    using var cancellation = new CancellationTokenSource();
    var termination = RunProcess.RegisterTermination(cancellation);
    var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    cancellation.Token.Register(() => { termination.Dispose(); disposed.SetResult(); });
    using (var stop = EventWaitHandle.OpenExisting($@"Local\Orisumi.Stop.{Environment.ProcessId}")) stop.Set();
    try { await disposed.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
    catch (TimeoutException) { throw new Exception("disposing the termination from its own cancellation deadlocked"); }
    Console.WriteLine("PASS: the stop event cancels without deadlocking a disposal that runs on its callback");
}

async Task CheckCaptureTask()
{
    const int fileMissing = unchecked((int)0x80070002), pathMissing = unchecked((int)0x80070003);
    const int denied = unchecked((int)0x80070005), failed = unchecked((int)0x80004005);
    Check(WindowsScheduledTask.Found(0) && !WindowsScheduledTask.Found(fileMissing) && !WindowsScheduledTask.Found(pathMissing), "scheduler lookup HRESULTs");
    foreach (var hr in new[] { denied, failed })
    {
        try { WindowsScheduledTask.Found(hr); }
        catch (Exception error) when (error.HResult == hr) { continue; }
        throw new Exception($"Expected scheduler failure 0x{hr:X8}");
    }

    // Exercise the actual native GetFolder call without altering Task Scheduler.
    var probe = Instance.Create(Path.Combine(temp.FullName, "probe"), "test_" + Guid.NewGuid().ToString("N"));
    Check(WindowsScheduledTask.Capture(probe).Query(default) is null, "read-only COM lookup of an absent instance");

    var task = new FixtureCaptureTask(() => { host?.Dispose(); host = Start(); });
    var jobs = new WindowsCollectorJobs(instance, task);
    await jobs.Set("fixture", fixture, default);
    await jobs.StopAll(default);
    await jobs.Set("fixture", fixture, default);
    await jobs.StopAll(default);
    Check(task.Edits.SequenceEqual(["Register", "Start", "Delete", "Register", "Start", "Delete"]),
        "register again after deleting the last task");

    task.FailCreate = true;
    try { await jobs.Set("fixture", fixture, default); throw new Exception("Expected denied registration"); }
    catch (UnauthorizedAccessException error) when (error.HResult == denied) { }
    Check(File.Exists(files.Desired("fixture")), "registration failure leaves the desired file for cleanup");
    task.Edits.Clear();
    await jobs.StopAll(default);
    Check(!File.Exists(files.Desired("fixture")) && task.Edits.SequenceEqual(["Delete"]),
        "failure cleanup removes desired state and any empty task folder");
}

JobSpec Spec(string name, string[] command) => new(command, store.Root, Path.Combine(store.Logs, name + ".out.log"),
    Path.Combine(store.Logs, name + ".err.log"));

Process Start()
{
    var start = new ProcessStartInfo(supervisor, ["--store", store.Root]) { UseShellExecute = false, CreateNoWindow = true };
    start.Environment["CLAUDE_CONFIG_DIR"] = config;
    return Process.Start(start)!;
}

void Set(string name, JobSpec? spec) => JobFiles.Write(files.Desired(name), spec is null ? null :
    JsonSerializer.SerializeToUtf8Bytes(spec, JobJson.Default.JobSpec));

async Task<JobStatus> Running(string name, int? previous = null)
{
    JobStatus? status = null;
    await Until(() => (status = files.Status().GetValueOrDefault(name)) is { State: "running", Pid: not null } &&
        status.Pid != previous && files.Alive(), name + " running");
    return status!;
}

async Task Until(Func<bool> condition, string description)
{
    var timer = Stopwatch.StartNew();
    while (!condition())
    {
        if (host!.HasExited || timer.Elapsed.TotalSeconds > 20)
            throw new IOException($"{description}: host exited={host.HasExited}; status={Log(files.StatusPath)}; " +
                $"fixture={Log(fixture.StandardError)}; claude={Log(actual.StandardError)}");
        await Task.Delay(75);
    }
}

long Count(string where)
{
    if (store.CurrentDatabase("claude") is not { } path) return 0;
    using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
    connection.Open();
    using var command = connection.CreateCommand();
    command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name='capture_runs'";
    if ((long)command.ExecuteScalar()! == 0) return 0;
    command.CommandText = "SELECT COUNT(*) FROM capture_runs WHERE " + where;
    return (long)command.ExecuteScalar()!;
}

static string Log(string path) => JobFiles.Read(path) is { } bytes ? System.Text.Encoding.UTF8.GetString(bytes) : "";
static void Check(bool value, string message) { if (!value) throw new Exception(message); }

sealed class FixtureCaptureTask(Action start) : IWindowsScheduledTask
{
    public string Name => "fixture";
    public bool FailCreate { get; set; }
    public List<string> Edits { get; } = [];
    private string? xml;

    public ScheduledTaskState? Query(CancellationToken token) => xml is null ? null : new(xml, false);
    public void Register(string? definition, CancellationToken token)
    {
        Edits.Add("Register");
        if (FailCreate) throw new UnauthorizedAccessException();
        xml = definition ?? "<Task/>";
    }
    public void Start(CancellationToken token)
    {
        if (xml is null) throw new InvalidOperationException();
        Edits.Add("Start");
        start();
    }
    public void End(CancellationToken token) => throw new InvalidOperationException();
    public void Delete(CancellationToken token) { Edits.Add("Delete"); xml = null; }
}
