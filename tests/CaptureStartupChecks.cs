#:project ../src/Orisumi.Cli/Orisumi.Cli.csproj
#:property AssemblyName=CaptureStartupChecks
#:property PublishAot=false

using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Xml.Linq;
using Orisumi.Cli;
using Orisumi.Cli.Capture;
using Orisumi.Platform;

[assembly: SupportedOSPlatform("macos")]

if (!OperatingSystem.IsMacOS())
{
    Console.WriteLine("Skipped: capture startup requires macOS.");
    return;
}
var dotnet = RunProcess.Dotnet;
var assembly = Assembly.GetExecutingAssembly().Location;
var host = Path.GetFullPath("src/Orisumi.Cli/bin/Debug/net11.0/orisumi.dll");
if (args is ["--child", var signalName, .. var literal])
{
    var stopped = new TaskCompletionSource();
    using var signal = PosixSignalRegistration.Create(Enum.Parse<PosixSignal>(signalName), context =>
    {
        context.Cancel = true;
        stopped.TrySetResult();
    });
    var input = await Console.In.ReadToEndAsync();
    Console.WriteLine(JsonSerializer.Serialize(literal));
    Console.WriteLine(input);
    Console.Error.WriteLine("child stderr");
    Console.WriteLine(Native.getpgid(0));
    await stopped.Task;
    Console.WriteLine(signalName);
    Environment.ExitCode = 23;
    return;
}

var temp = Directory.CreateTempSubdirectory("orisumi-startup-");
try
{
    await ConfigurationChecks();
    await ProxyChecks();
    Console.WriteLine("Capture startup configuration and launch proxy checks passed.");
}
finally { temp.Delete(recursive: true); }

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

async Task ConfigurationChecks()
{
    var store = new Store(Path.Combine(temp.FullName, "store & spaces ' 引号"));
    var programs = await CollectorPrograms.Read(store.Root, CancellationToken.None, Path.GetDirectoryName(host));
    Check(programs.Select(program => program.Description.Name).Order().SequenceEqual(["browser", "claude", "codex", "macos"]),
        "all collector descriptions provide a launch command");
    foreach (var bundle in new[] { false, true })
    {
        if (bundle)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(store.HostExecutable)!);
            File.WriteAllText(store.HostExecutable, "");
        }
        foreach (var program in programs)
        {
            var (name, _, command) = program.Description;
            var label = $"dev.orisumi.test_startup.{name}";
            var path = Path.Combine(temp.FullName, name + ".plist");
            CaptureStartup.LaunchAgent(label, program.Command(store.Root, command!), store, name).Save(path);
            var lint = await Process.RunAndCaptureTextAsync("/usr/bin/plutil", ["-lint", path], CancellationToken.None);
            Check(lint.ExitStatus.ExitCode == 0, lint.StandardError);
            var elements = XDocument.Load(path).Root!.Element("dict")!.Elements().ToArray();
            var fields = Enumerable.Range(0, elements.Length / 2).ToDictionary(i => elements[i * 2].Value, i => elements[i * 2 + 1]);
            string[] prefix = bundle ? [store.HostExecutable, "launch", "--"] : [];
            Check(fields["Label"].Value == label, "collector uses its instance LaunchAgent label");
            Check(fields["ProgramArguments"].Elements().Select(item => item.Value)
                .SequenceEqual([.. prefix, dotnet, program.Path, "--store", store.Root, .. command!]), "argv boundaries survive XML encoding");
            Check(fields["WorkingDirectory"].Value == store.Root, "collector starts in its store");
            Check(fields["StandardOutPath"].Value == Path.Combine(store.Logs, name + ".out.log") &&
                fields["StandardErrorPath"].Value == Path.Combine(store.Logs, name + ".err.log"), "per-source log paths");
            Check(fields["RunAtLoad"].Name == "true" && fields["KeepAlive"].Name == "true" &&
                fields["ThrottleInterval"].Value == "10" && fields["ExitTimeOut"].Value == "30", "launchd keeps collectors running");
        }
    }
    Console.WriteLine("PASS: all collector LaunchAgents preserve arguments with and without the bundle launcher");
}

async Task ProxyChecks()
{
    string[] literal = ["", "--", "--store", "-h", "literal 'quotes' \"double\" ; $(not-a-command) 中文", "@missing-response-file"];
    foreach (var signal in new[] { PosixSignal.SIGTERM, PosixSignal.SIGINT, PosixSignal.SIGHUP, PosixSignal.SIGQUIT })
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var start = new ProcessStartInfo(dotnet, [host, "launch", "--", dotnet, assembly, "--child", signal.ToString(), .. literal])
        {
            UseShellExecute = false, WorkingDirectory = temp.FullName,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using var launcher = Process.Start(start)!;
        try
        {
            await launcher.StandardInput.WriteAsync("inherited stdin");
            launcher.StandardInput.Close();
            var output = launcher.StandardOutput;
            var line = await output.ReadLineAsync(timeout.Token);
            Check(line is not null && JsonSerializer.Deserialize<string[]>(line)!.SequenceEqual(literal), "launch preserves literal arguments");
            Check(await output.ReadLineAsync(timeout.Token) == "inherited stdin", "launch inherits stdin and stdout");
            Check(await launcher.StandardError.ReadLineAsync(timeout.Token) == "child stderr", "launch inherits stderr");
            Check(int.Parse((await output.ReadLineAsync(timeout.Token))!) == Native.getpgid(launcher.Id), "child shares the launcher's process group");
            launcher.Signal(signal);
            Check(await output.ReadLineAsync(timeout.Token) == signal.ToString(), $"launcher forwards {signal}");
            await launcher.WaitForExitAsync(timeout.Token);
            Check(launcher.ExitCode == 23, "launch returns the child's exit code");
        }
        finally
        {
            if (!launcher.HasExited) { launcher.Kill(entireProcessTree: true); await launcher.WaitForExitAsync(); }
        }
    }

    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    var signaled = await Process.RunAndCaptureTextAsync(dotnet,
        [host, "launch", "--", "/bin/sh", "-c", "kill -TERM $$"], deadline.Token);
    Check(signaled.ExitStatus is { ExitCode: 143, Canceled: false }, "launch preserves a signal-derived exit status");
    Console.WriteLine("PASS: launch preserves literal arguments, stdin/stdout/stderr, process group and exit status and forwards TERM/INT/HUP/QUIT");
}

static class Native
{
    [DllImport("/usr/lib/libSystem.B.dylib")] internal static extern int getpgid(int pid);
}
