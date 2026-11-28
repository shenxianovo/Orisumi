using System.Globalization;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Orisumi.Core;

namespace Orisumi.Desktop;

internal sealed class MacCollectorJobs(Instance instance, SystemLocations locations) : ICollectorJobs
{
    private sealed record Snapshot(string Name, byte[]? Plist, bool Loaded, string Detail, int? ProcessId, JsonValue? Exit)
        : SavedJob(Name, Plist is not null, Loaded, Detail, ProcessId, Exit);

    public async Task<IReadOnlyDictionary<string, SavedJob>> Inspect(IEnumerable<string> sources, CancellationToken token)
    {
        var installed = ReleaseManifest.ReadInstalled(instance.Store)?.Components.Keys.AsEnumerable() ?? [];
        var names = Launchd.CaptureAgentPlists(instance, locations)
            .Select(path => Path.GetFileNameWithoutExtension(path)[instance.CaptureAgentPrefix.Length..])
            .Union(installed.Where(name => name != "host" && Platforms.Runs(name, Platforms.CurrentRid)))
            .Union(sources);
        var jobs = new Dictionary<string, SavedJob>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            var path = Launchd.CaptureAgentPlist(instance, locations, name);
            var registration = File.Exists(path) ? File.ReadAllBytes(path) : null;
            var service = Launchd.Service(instance.CaptureAgentLabel(name));
            var details = await Launchd.Print(locations, service, token);
            if (details is null)
            {
                jobs.Add(name, new Snapshot(name, registration, false, "not_loaded", null, null));
                continue;
            }
            var state = Launchd.Field(details, "state") ?? throw new InvalidDataException($"launchctl print {service}: missing state");
            var exit = state == "running" ? null : Launchd.Field(details, "last exit code");
            JsonValue? exitCode = exit switch
            {
                null or "(never exited)" => null,
                _ when long.TryParse(exit, CultureInfo.InvariantCulture, out var code) => JsonValue.Create(code),
                _ => JsonValue.Create(exit),
            };
            jobs.Add(name, new Snapshot(name, registration, true, state, state == "running" ? Launchd.Pid(details) : null, exitCode));
        }
        return jobs;
    }

    public async Task<string> Set(string source, JobSpec? desired, CancellationToken token)
    {
        var path = Launchd.CaptureAgentPlist(instance, locations, source);
        await Launchd.StopAgent(path, locations, token);
        if (desired is null) return instance.CaptureAgentLabel(source);
        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(instance.Store.Logs);
        Directory.CreateDirectory(locations.Agents);
        LaunchAgent(instance, source, desired).Save(path);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        await Launchd.Bootstrap(locations, path, token);
        return instance.CaptureAgentLabel(source);
    }

    public Task Restore(SavedJob previous, CancellationToken token)
    {
        var job = (Snapshot)previous;
        return Launchd.Restore(Launchd.CaptureAgentPlist(instance, locations, job.Source), locations, job.Plist, job.Supervised, token);
    }

    internal static XDocument LaunchAgent(Instance instance, string name, JobSpec spec)
    {
        var command = spec.Command;
        if (File.Exists(instance.Store.HostExecutable))
            command = [instance.Store.HostExecutable, LaunchCommand.Name, "--", .. command];
        return Launchd.Plist(instance, new XElement("dict",
            new XElement("key", "Label"), new XElement("string", instance.CaptureAgentLabel(name)),
            new XElement("key", "ProgramArguments"), new XElement("array", command.Select(value => new XElement("string", value))),
            new XElement("key", "WorkingDirectory"), new XElement("string", spec.WorkingDirectory),
            new XElement("key", "RunAtLoad"), new XElement("true"),
            new XElement("key", "StandardOutPath"), new XElement("string", spec.StandardOutput),
            new XElement("key", "StandardErrorPath"), new XElement("string", spec.StandardError),
            new XElement("key", "KeepAlive"), new XElement("true"),
            new XElement("key", "ThrottleInterval"), new XElement("integer", 10),
            new XElement("key", "ExitTimeOut"), new XElement("integer", 30)));
    }
}
