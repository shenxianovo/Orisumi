using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using Orisumi.Platform;

namespace Orisumi.Cli.Capture;

internal static class CaptureStartup
{
    internal static async Task Start(Instance instance, SystemLocations locations, TextWriter output, CancellationToken token,
        string? baseDirectory = null)
    {
        var store = instance.Store;
        using var guard = Store.WaitLock(store.LockFile("app"));
        var programs = await CollectorPrograms.Read(store.Root, token, baseDirectory);
        var browser = programs.SingleOrDefault(program => program.Description.Name == "browser");
        string? extension = null;
        if (browser is not null)
        {
            var checkout = CollectorPrograms.Checkout(baseDirectory ?? AppContext.BaseDirectory);
            extension = DirectoryPaths.Resolve(checkout is null ? Path.Combine(Path.GetDirectoryName(browser.Path)!, "extension")
                : Path.Combine(checkout, ".local", "extension"));
            ChromeExtension.CheckConfiguration(extension, instance);
        }
        await StopCapture(instance, locations, token);

        Directory.CreateDirectory(store.Logs);
        Directory.CreateDirectory(locations.Agents);
        try
        {
            foreach (var program in programs)
            {
                token.ThrowIfCancellationRequested();
                var name = program.Description.Name;
                var start = program.Description.Start!;
                var command = program.Command(store.Root, start.Arguments);
                if (start.Mode == "resident")
                {
                    var label = instance.AgentLabel(name);
                    var path = Path.Combine(locations.Agents, label + ".plist");
                    LaunchAgent(label, command, store, name).Save(path);
                    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                    await Launchctl(locations, token, "bootstrap", Domain, path);
                    output.WriteLine($"Started {label}");
                }
                else if (start.Mode == "terminal")
                {
                    var error = Path.Combine(store.Logs, name + ".err.log");
                    var pid = DetachedProcess.Start(command, Path.Combine(store.Logs, name + ".out.log"), error);
                    var identity = ProcessIdentity.Of(pid);
                    identity?.Write(store.ProcessFile(name));
                    await Task.Delay(500, CancellationToken.None);
                    if (DetachedProcess.HasExited(pid) || identity is null) throw new IOException($"{name} 采集启动失败，请查看 {error}。");
                    output.WriteLine($"Started {name} collector: pid {pid}");
                }
            }
            if (browser is not null)
            {
                var id = ChromeExtension.Id(extension!);
                ChromeExtension.Register(instance, id, browser.Command(store.Root, ["native-host", "--registered"]), locations);
                output.WriteLine($"Load unpacked Chrome extension: {extension}");
                output.WriteLine($"Expected Chrome extension ID: {id}");
            }
        }
        catch
        {
            try { await StopCapture(instance, locations, CancellationToken.None); }
            catch (IOException error) { await output.WriteLineAsync($"Capture cleanup: {error.Message}"); }
            throw;
        }
    }

    internal static async Task Stop(Instance instance, SystemLocations locations, TextWriter output, CancellationToken token)
    {
        using var guard = Store.WaitLock(instance.Store.LockFile("app"));
        await StopCapture(instance, locations, token);
        output.WriteLine($"Stopped capture for {instance.Name}");
    }

    private static async Task StopCapture(Instance instance, SystemLocations locations, CancellationToken token)
    {
        var store = instance.Store;
        string[] browsers = [];
        using (Store.WaitLock(store.LockFile("chrome-registration")))
        {
            if (Directory.Exists(locations.ChromeManifests)) File.Delete(ChromeExtension.Manifest(instance, locations));
            File.Delete(store.ChromeLauncher);
            if (Directory.Exists(store.ChromeProcesses)) browsers = Directory.GetFiles(store.ChromeProcesses, "*.pid");
        }
        var terminal = Directory.GetFiles(store.State, "*.pid");
        var agents = Directory.Exists(locations.Agents)
            ? Directory.GetFiles(locations.Agents, instance.AgentLabel("*") + ".plist") : [];

        await Task.WhenAll(agents.Select(StopAgent).Concat(browsers.Concat(terminal).Select(StopProcess)));

        async Task StopAgent(string plist)
        {
            var service = $"{Domain}/{Path.GetFileNameWithoutExtension(plist)}";
            var before = await LaunchctlResult(locations, token, "print", service);
            if (before.Code is not (0 or 113)) throw LaunchError("print", service, before.Error);
            if (before.Code == 0)
            {
                var pidLine = before.Output.Split('\n').Select(line => line.Trim())
                    .FirstOrDefault(line => line.StartsWith("pid = ", StringComparison.Ordinal));
                var identity = pidLine is not null && int.TryParse(pidLine.AsSpan(6), CultureInfo.InvariantCulture, out var pid)
                    ? ProcessIdentity.Of(pid) : null;
                var bootout = await LaunchctlResult(locations, token, "bootout", service);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(30));
                try
                {
                    while (true)
                    {
                        var current = await LaunchctlResult(locations, deadline.Token, "print", service);
                        if (current.Code == 113) break; // The service is absent from this launchd domain.
                        if (current.Code != 0) throw LaunchError("print", service, current.Error);
                        if (bootout.Code != 0) throw LaunchError("bootout", service, bootout.Error);
                        await Task.Delay(100, deadline.Token);
                    }
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    throw new IOException($"{service} 未能在 30 秒内停止。");
                }
                if (identity is not null) await identity.Wait(token);
            }
            File.Delete(plist);
        }

        async Task StopProcess(string pidFile)
        {
            if (ProcessIdentity.Read(pidFile) is { } identity) await identity.Wait(token, terminate: true);
            File.Delete(pidFile);
        }
    }

    internal static XDocument LaunchAgent(string label, string[] command, Store store, string name)
    {
        var dictionary = new XElement("dict",
            new XElement("key", "Label"), new XElement("string", label),
            new XElement("key", "ProgramArguments"), new XElement("array",
                command.Select(value => new XElement("string", value))),
            new XElement("key", "WorkingDirectory"), new XElement("string", store.Root),
            new XElement("key", "RunAtLoad"), new XElement("true"),
            new XElement("key", "StandardOutPath"), new XElement("string", Path.Combine(store.Logs, name + ".out.log")),
            new XElement("key", "StandardErrorPath"), new XElement("string", Path.Combine(store.Logs, name + ".err.log")),
            new XElement("key", "KeepAlive"), new XElement("true"),
            new XElement("key", "ThrottleInterval"), new XElement("integer", 10),
            new XElement("key", "ExitTimeOut"), new XElement("integer", 30));
        return new XDocument(new XDeclaration("1.0", "UTF-8", null),
            new XDocumentType("plist", "-//Apple//DTD PLIST 1.0//EN", "http://www.apple.com/DTDs/PropertyList-1.0.dtd", null),
            new XElement("plist", new XAttribute("version", "1.0"), dictionary));
    }

    private static IOException LaunchError(string action, string service, string error) =>
        new($"launchctl {action} {service}: {error.Trim()}");

    private static string Domain => $"gui/{getuid()}";

    private static async Task Launchctl(SystemLocations locations, CancellationToken token, params string[] arguments)
    {
        var result = await LaunchctlResult(locations, token, arguments);
        if (result.Code != 0) throw new IOException($"launchctl {string.Join(' ', arguments)}: {result.Error.Trim()}");
    }

    private static async Task<(int Code, string Output, string Error)> LaunchctlResult(SystemLocations locations,
        CancellationToken cancellation, params string[] arguments)
    {
        var result = await Process.RunAndCaptureTextAsync(locations.Launchctl, arguments, cancellation);
        if (result.ExitStatus.Canceled) throw new OperationCanceledException(cancellation);
        return (result.ExitStatus.ExitCode, result.StandardOutput, result.StandardError);
    }

    [DllImport("/usr/lib/libSystem.B.dylib")]
    private static extern uint getuid();
}
