using System.Xml.Linq;
using Orisumi.Platform;

namespace Orisumi.Cli.Capture;

internal static class CaptureStartup
{
    internal static async Task Start(Instance instance, SystemLocations locations, TextWriter output, CancellationToken token,
        string? baseDirectory = null)
    {
        var store = instance.Store;
        using var guard = Store.WaitLock(store.AppLock);
        var programs = await CollectorPrograms.Read(store.Root, token, baseDirectory);
        if (programs.Any(program => program.Description.Name == "browser"))
            ChromeExtension.CheckConfiguration(ChromeExtension.DirectoryFor(instance), instance);
        await Launchd.StopCapture(instance, locations, token);

        try
        {
            foreach (var program in programs)
            {
                await StartAgent(instance, program, locations, token);
                output.WriteLine($"Started {instance.CaptureAgentLabel(program.Description.Name)}");
            }
        }
        catch
        {
            try { await Launchd.StopCapture(instance, locations, CancellationToken.None); }
            catch (IOException error) { await output.WriteLineAsync($"Capture cleanup: {error.Message}"); }
            throw;
        }
    }

    internal static async Task Stop(Instance instance, SystemLocations locations, TextWriter output, CancellationToken token)
    {
        using var guard = Store.WaitLock(instance.Store.AppLock);
        await Launchd.StopCapture(instance, locations, token);
        output.WriteLine($"Stopped capture for {instance.Name}");
    }

    internal static async Task StartAgent(Instance instance, CollectorProgram program, SystemLocations locations, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var store = instance.Store;
        var name = program.Description.Name;
        var label = instance.CaptureAgentLabel(name);
        var path = Launchd.CaptureAgentPlist(instance, locations, name);
        Directory.CreateDirectory(store.Logs);
        Directory.CreateDirectory(locations.Agents);
        LaunchAgent(instance, label, program.Command(store.Root, program.Description.Arguments!), name).Save(path);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        await Launchd.Bootstrap(locations, path, token);
    }

    internal static XDocument LaunchAgent(Instance instance, string label, string[] command, string name)
    {
        var store = instance.Store;
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
        return Launchd.Plist(instance, dictionary);
    }
}
