using System.ComponentModel;
using Orisumi.Desktop;
using Orisumi.Platform;

namespace Orisumi.Cli;

internal static class InstallCommand
{
    internal static async Task Run(Instance instance, string directory, ICollectorJobs jobs, IDesktopInstallation platform,
        TextWriter output, CancellationToken token)
    {
        using var guard = Store.WaitLock(instance.Store.AppLock);
        await RunLocked(instance, directory, jobs, platform, output, token);
    }

    internal static async Task RunLocked(Instance instance, string directory, ICollectorJobs jobs, IDesktopInstallation platform,
        TextWriter output, CancellationToken token)
    {
        var store = instance.Store;
        var source = DirectoryPaths.Resolve(directory);
        var app = DirectoryPaths.Resolve(store.App);
        if (DirectoryPaths.Contains(app, source) || DirectoryPaths.Contains(source, app))
            throw new ArgumentException($"发布目录必须位于实例的 app 之外：{source}");

        var release = ReleaseManifest.Read(Path.Combine(source, ReleaseManifest.FileName));
        release.VerifyFor(instance, Platforms.CurrentRid);
        var installed = ReleaseManifest.ReadInstalled(store);
        var components = new Dictionary<string, ReleaseComponent>(installed?.Components ?? [], StringComparer.Ordinal);
        var schema = new Dictionary<string, SchemaTable[]>(installed?.Schema ?? [], StringComparer.Ordinal);
        var changes = new List<(string Name, string Staged, string Destination)>();
        var descriptions = new Dictionary<string, ProgramDescription>(StringComparer.Ordinal);
        var results = new List<string>();
        var staging = Path.Combine(store.App, ".install-" + Guid.NewGuid().ToString("N"));
        var manifest = Path.Combine(staging, ReleaseManifest.FileName);
        try
        {
            foreach (var (name, component) in release.Components.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                var from = ReleaseManifest.ComponentDirectory(source, name);
                if (DirectoryPaths.Resolve(from) != from)
                    throw new InvalidDataException($"发布目录内包含符号链接：{from}");
                var previous = components.GetValueOrDefault(name);
                var changed = previous?.Identity != component.Identity;
                var candidate = from;
                if (changed)
                {
                    candidate = ReleaseManifest.ComponentDirectory(staging, name);
                    Copy(from, candidate);
                    changes.Add((name, candidate, ReleaseManifest.ComponentDirectory(store.App, name)));
                    components[name] = component;
                }
                if (release.Schema.TryGetValue(name, out var tables)) schema[name] = tables;
                component.VerifyFiles(candidate);
                if (name == "host") await platform.VerifyHost(candidate, requireReleaseCertificate: false, token);
                var status = !changed ? "unchanged" : previous is null ? "new" : "installed";
                results.Add($"{name}: {status} {components[name].Version}");
            }

            foreach (var (name, staged, _) in changes)
            {
                var executable = name == "host" ? platform.HostExecutable(staged) :
                    Path.Combine(staged, ProgramDescription.ExecutableName(name, Platforms.CurrentRid));
                var description = await ProgramDescription.Read(executable, [], token, name == "host" ? platform.RunHost : RunProcess.RunCommand);
                if (description.Name != name || description.Version != components[name].Version)
                    throw new InvalidDataException($"{executable} 的 describe 与发布清单不符。");
                descriptions.Add(name, description);
                if (name == "browser") ChromeExtension.Configure(Path.Combine(staged, "extension"), instance);
            }

            Directory.CreateDirectory(staging);
            (release with
            {
                Components = components,
                Schema = schema,
                InstalledAtUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            }).Write(manifest);
            var changedCollectors = changes.Where(change => change.Name != "host").Select(change => change.Name).ToArray();
            var previousJobs = changedCollectors.Length == 0 ? null : await jobs.Inspect(changedCollectors, token);
            var captureEnabled = previousJobs?.Values.Any(job => job.Supervised) == true;
            var payloads = new Stack<Action>();
            var registrations = new Stack<(string Name, Func<CancellationToken, Task> Restore)>();
            try
            {
                foreach (var (_, staged, destination) in changes)
                    payloads.Push(platform.ActivateDirectory(staged, destination));

                foreach (var (name, _, destination) in changes)
                {
                    if (name == "host")
                    {
                        var login = await platform.PrepareLogin(token);
                        registrations.Push((name, login.Restore));
                        await login.Apply(token);
                    }
                    else
                    {
                        var previous = previousJobs![name];
                        if (!previous.Supervised && (!captureEnabled || installed?.Components.ContainsKey(name) == true)) continue;
                        registrations.Push((name, ct => jobs.Restore(previous, ct)));
                        var program = new CollectorProgram(Path.Combine(destination, ProgramDescription.ExecutableName(name, Platforms.CurrentRid)),
                            false, descriptions[name]);
                        await jobs.Set(name, program.Job(store), token);
                    }
                }
                File.Move(manifest, store.Manifest, overwrite: true);
            }
            catch
            {
                foreach (var restore in payloads) restore();
                foreach (var (name, restore) in registrations)
                {
                    try { await restore(CancellationToken.None); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception)
                    {
                        output.WriteLine($"无法恢复 {name} 的启动配置：{error.Message}");
                    }
                }
                throw;
            }

            if (components.ContainsKey("host")) platform.LinkCommand();
            foreach (var result in results) output.WriteLine(result);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    private static void Copy(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var entry in new DirectoryInfo(source).EnumerateFileSystemInfos())
        {
            var path = Path.Combine(destination, entry.Name);
            if (entry.LinkTarget is not null) throw new InvalidDataException($"发布目录内包含符号链接：{entry.FullName}");
            if (entry is DirectoryInfo) Copy(entry.FullName, path);
            else File.Copy(entry.FullName, path);
        }
    }
}
