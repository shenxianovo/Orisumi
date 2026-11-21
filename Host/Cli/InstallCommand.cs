using System.ComponentModel;
using System.Runtime.InteropServices;
using Orisumi.Cli.Capture;
using Orisumi.Platform;

namespace Orisumi.Cli;

internal static class InstallCommand
{
    internal static async Task Run(Instance instance, string directory, SystemLocations locations, TextWriter output,
        CancellationToken token, HostBundle.Tools? host = null)
    {
        using var guard = Store.WaitLock(instance.Store.AppLock);
        await RunLocked(instance, directory, locations, output, token, host ?? HostBundle.Tools.Current);
    }

    internal static async Task RunLocked(Instance instance, string directory, SystemLocations locations, TextWriter output,
        CancellationToken token, HostBundle.Tools host)
    {
        var store = instance.Store;
        var source = DirectoryPaths.Resolve(directory);
        var app = DirectoryPaths.Resolve(store.App);
        if (DirectoryPaths.Contains(app, source) || DirectoryPaths.Contains(source, app))
            throw new ArgumentException($"发布目录必须位于实例的 app 之外：{source}");

        var release = ReleaseManifest.Read(Path.Combine(source, ReleaseManifest.FileName));
        release.VerifyFor(instance);
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
                if (name == "host") await HostBundle.Verify(candidate, instance.Name, token, host);
                var status = !changed ? "unchanged" : previous is null ? "new" : "installed";
                results.Add($"{name}: {status} {components[name].Version}");
            }

            foreach (var (name, staged, _) in changes)
            {
                var executable = name == "host" ? HostBundle.Executable(staged) :
                    Path.Combine(staged, ProgramDescription.ExecutableName(name));
                var description = await ProgramDescription.Read(executable, [], token, name == "host" ? host.Run : RunProcess.RunCommand);
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
            var installedNames = installed?.Components.Keys.AsEnumerable() ?? [];
            var loaded = changes.Any(change => change.Name != "host")
                ? await Launchd.LoadedCaptureAgents(instance, locations, token, release.Components.Keys) : [];
            var swapped = new Stack<(string Staged, string Destination, bool Replaced)>();
            var agents = new Stack<(string Plist, byte[]? Content, bool Loaded)>();
            try
            {
                foreach (var (_, staged, destination) in changes)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    var replaced = Directory.Exists(destination);
                    if (replaced) Exchange(staged, destination);
                    else Directory.Move(staged, destination);
                    swapped.Push((staged, destination, replaced));
                }

                foreach (var (name, _, destination) in changes)
                {
                    if (name == "host")
                    {
                        var label = instance.AppAgentLabel;
                        var plist = Path.Combine(locations.Agents, label + ".plist");
                        var running = await Launchd.Print(locations, Launchd.Service(label), token) is not null;
                        Remember(plist, running);
                        await AppStartup.Install(instance, locations, running, token);
                    }
                    else if (loaded.Contains(name) || (loaded.Count > 0 && !installedNames.Contains(name)))
                    {
                        var plist = Launchd.CaptureAgentPlist(instance, locations, name);
                        Remember(plist, loaded.Contains(name));
                        await Launchd.StopAgent(plist, locations, token);
                        var program = new CollectorProgram(Path.Combine(destination, ProgramDescription.ExecutableName(name)), false, descriptions[name]);
                        await CaptureStartup.StartAgent(instance, program, locations, token);
                    }
                }
                File.Move(manifest, store.Manifest, overwrite: true);
            }
            catch
            {
                foreach (var (staged, destination, replaced) in swapped)
                    if (replaced) Exchange(staged, destination);
                    else Directory.Move(destination, staged);
                foreach (var (plist, content, wasLoaded) in agents)
                {
                    try
                    {
                        await Launchd.StopAgent(plist, locations, CancellationToken.None);
                        if (content is not null)
                        {
                            File.WriteAllBytes(plist, content);
                            File.SetUnixFileMode(plist, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                            if (wasLoaded) await Launchd.Bootstrap(locations, plist, CancellationToken.None);
                        }
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception)
                    {
                        output.WriteLine($"无法恢复 LaunchAgent {plist}：{error.Message}");
                    }
                }
                throw;
            }

            if (components.ContainsKey("host") && instance.CommandName is { } command)
                Link(locations.Commands, command, store.HostExecutable);
            foreach (var result in results) output.WriteLine(result);

            void Remember(string plist, bool running) =>
                agents.Push((plist, File.Exists(plist) ? File.ReadAllBytes(plist) : null, running));
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    internal static void Exchange(string from, string to)
    {
        // Darwin can exchange two nonempty directories without a gap at the installed path.
        if (renamex_np(from, to, 2) != 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"无法替换 {to}");
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

    private static void Link(string directory, string name, string target)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        if (new FileInfo(path).LinkTarget == target) return;
        var temporary = path + "." + Guid.NewGuid().ToString("N");
        try
        {
            File.CreateSymbolicLink(temporary, target);
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    [DllImport("/usr/lib/libSystem.B.dylib", SetLastError = true)]
    private static extern int renamex_np([MarshalAs(UnmanagedType.LPUTF8Str)] string from,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string to, uint flags);
}
