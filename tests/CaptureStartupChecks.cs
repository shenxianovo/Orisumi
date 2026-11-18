#:project ../src/Orisumi.Cli/Orisumi.Cli.csproj
#:property AssemblyName=CaptureStartupChecks
#:property PublishAot=false

using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using Orisumi.Cli;
using Orisumi.Cli.Capture;

if (!OperatingSystem.IsMacOS())
{
    Console.WriteLine("Skipped: capture startup requires macOS.");
    return;
}
var dotnet = SourceLauncher.Dotnet;
var assembly = Assembly.GetExecutingAssembly().Location;
if (args is ["--child", var readyFile, var text])
{
    var stopped = new TaskCompletionSource();
    using var signal = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
    {
        context.Cancel = true;
        stopped.TrySetResult();
    });
    Check(await Console.In.ReadToEndAsync() == "", "background stdin must be /dev/null");
    Console.WriteLine(text);
    Console.Error.WriteLine("child stderr");
    File.WriteAllText(readyFile, Native.getpgrp().ToString());
    await stopped.Task;
    Console.WriteLine("stopped");
    return;
}
if (args is ["--launch-only", var stdout, var stderr, var ready, var value])
{
    Console.WriteLine(DetachedProcess.Start([dotnet, assembly, "--child", ready, value], stdout, stderr));
    return;
}

var temp = Directory.CreateTempSubdirectory("orisumi-startup-");
try
{
    await ConfigurationChecks();
    await BackgroundChecks();
    Console.WriteLine("Capture startup configuration, detached logging, signals and PID ownership checks passed.");
}
finally { temp.Delete(recursive: true); }

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

async Task ConfigurationChecks()
{
    var store = Path.Combine(temp.FullName, "store & spaces ' 引号");
    foreach (var source in CaptureSources.All)
    {
        if (source is not { Name: var name, Agent: { } command }) continue;
        var config = CaptureStartup.LaunchAgent(name, dotnet, assembly, temp.FullName, store, command);
        var path = Path.Combine(temp.FullName, name + ".plist");
        config.Save(path);
        await Run("/usr/bin/plutil", ["-lint", path]);
        var elements = XDocument.Load(path).Root!.Element("dict")!.Elements().ToArray();
        var fields = Enumerable.Range(0, elements.Length / 2).ToDictionary(i => elements[i * 2].Value, i => elements[i * 2 + 1]);
        Check(fields["ProgramArguments"].Elements().Select(item => item.Value).SequenceEqual([dotnet, assembly, "--store", store, .. command]), "argv boundaries survive XML encoding");
        Check(fields["StandardErrorPath"].Value == Path.Combine(store, "logs", name + ".err.log"), "per-source log path");
        Check(fields["RunAtLoad"].Name == "true", "capture starts immediately");
        Check(fields["KeepAlive"].Name == "true" && fields["ThrottleInterval"].Value == "10" && !fields.ContainsKey("StartInterval"), "collectors remain running between source changes");
    }
    Console.WriteLine("PASS: LaunchAgent schedules and literal arguments");
}

async Task BackgroundChecks()
{
    var logs = Directory.CreateDirectory(Path.Combine(temp.FullName, "logs & ' spaces")).FullName;
    var stdout = Path.Combine(logs, "out.log");
    var stderr = Path.Combine(logs, "err.log");
    var ready = Path.Combine(logs, "ready");
    const string text = "literal 'quotes' \"double\" ; $(not-a-command) 中文";
    File.WriteAllText(stdout, "previous log\n");
    var result = await Run(dotnet, [assembly, "--launch-only", stdout, stderr, ready, text]);
    var pid = int.Parse(result.Trim());
    using var child = Process.GetProcessById(pid);
    try
    {
        var wait = Stopwatch.StartNew();
        while (!File.Exists(ready) && wait.Elapsed < TimeSpan.FromSeconds(10))
        {
            Check(!child.HasExited, "detached child exited before readiness");
            await Task.Delay(50);
        }
        Check(File.Exists(ready), "child must survive the launcher's exit");
        Check(File.ReadAllText(ready) == pid.ToString(), "child has its own process group");
        Check(File.ReadAllText(stdout) == "previous log\n" + text + "\n", "logs append and arguments are literal");
        Check(File.ReadAllText(stderr) == "child stderr\n", "stderr goes directly to its file");
        await Run("/bin/kill", ["-HUP", pid.ToString()]);
        await Task.Delay(100);
        Check(!child.HasExited, "terminal hangup must not stop the collector");
        var pidFile = Path.Combine(temp.FullName, "foreground.pid");
        // A reused PID (here: this test process) has another start time and is left alone.
        File.WriteAllText(pidFile, $"{Environment.ProcessId} 1");
        await CaptureStartup.StopForeground(pidFile, CancellationToken.None);
        CaptureStartup.WritePidFile(pidFile, pid);
        await CaptureStartup.StopForeground(pidFile, CancellationToken.None);
        Check(child.HasExited && File.ReadAllText(stdout).EndsWith("stopped\n"), "matching collector stops gracefully");
    }
    finally
    {
        if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); }
    }
    var failed = DetachedProcess.Start(["/usr/bin/false"], stdout, stderr);
    await Task.Delay(200);
    Check(DetachedProcess.HasExited(failed), "early startup failures are detected and reaped");
    Console.WriteLine("PASS: detached process survives launcher exit, literal logging, HUP, TERM and stale PID file");
}

static async Task<string> Run(string executable, string[] arguments)
{
    var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var process = Process.Start(start)!;
    var output = process.StandardOutput.ReadToEndAsync();
    var error = process.StandardError.ReadToEndAsync();
    try
    {
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Check(process.ExitCode == 0, await error);
        return await output;
    }
    finally
    {
        if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
    }
}

static class Native
{
    [DllImport("/usr/lib/libSystem.B.dylib")] internal static extern int getpgrp();
}
