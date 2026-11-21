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
            extension = ChromeExtension.DirectoryFor(instance, checkout);
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
                var label = instance.CaptureAgentLabel(name);
                var path = Path.Combine(locations.Agents, label + ".plist");
                LaunchAgent(label, program.Command(store.Root, program.Description.Arguments!), store, name).Save(path);
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                var domain = Launchd.Domain;
                var bootstrap = await Launchd.Run(locations, token, "bootstrap", domain, path);
                if (bootstrap.Code != 0) throw Launchd.Error("bootstrap", $"{domain} {path}", bootstrap.Error);
                output.WriteLine($"Started {label}");
            }
            if (extension is not null)
            {
                var id = ChromeExtension.Id(extension);
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
        var agents = Launchd.CaptureAgentPlists(instance, locations).ToArray();

        await Task.WhenAll(agents.Select(StopAgent));

        async Task StopAgent(string plist)
        {
            var service = Launchd.Service(Path.GetFileNameWithoutExtension(plist));
            var before = await Launchd.Print(locations, service, token);
            if (before is not null)
            {
                var identity = Launchd.Pid(before) is int pid ? ProcessIdentity.Of(pid) : null;
                var bootout = await Launchd.Run(locations, token, "bootout", service);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(30));
                try
                {
                    while (true)
                    {
                        var current = await Launchd.Print(locations, service, deadline.Token);
                        if (current is null) break;
                        if (bootout.Code != 0) throw Launchd.Error("bootout", service, bootout.Error);
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
    }

    internal static XDocument LaunchAgent(string label, string[] command, Store store, string name)
    {
        if (File.Exists(store.HostExecutable))
            command = [store.HostExecutable, LaunchCommand.Name, "--", .. command];
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
        return Launchd.Plist(dictionary);
    }
}
