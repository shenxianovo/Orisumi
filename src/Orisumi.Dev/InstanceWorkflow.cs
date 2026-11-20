using Orisumi.Platform;

namespace Orisumi.Dev;

internal static class InstanceWorkflow
{
    internal static async Task Preview(string checkout, string releases, Store preview, string[] components, CancellationToken token)
    {
        var head = await Releases.Head(checkout, token);
        var selected = Releases.Select(checkout, components, includeHost: true)
            .Select(component => (component, Releases.ForHead(releases, component, head))).ToArray();
        await CheckReleases(selected, token);
        Instance.Create(preview.Root, "preview");
        await Stop(preview, "preview", token);
        await InstallAndStart(preview, selected, token);
    }

    internal static async Task Promote(string checkout, string releases, Store prod, Store preview, string[] components,
        string? migration, CancellationToken token)
    {
        if (DirectoryPaths.Contains(DirectoryPaths.Resolve(prod.Root), DirectoryPaths.Resolve(preview.Root)) ||
            DirectoryPaths.Contains(DirectoryPaths.Resolve(preview.Root), DirectoryPaths.Resolve(prod.Root)))
            throw new ArgumentException("生产和预览需要独立的实例目录。");

        if (File.Exists(InstalledProgram(preview, "host"))) RequireName(preview, "preview");

        var selected = new List<(string Component, string Release)>();
        string? head = null;
        foreach (var component in Releases.Select(checkout, components, includeHost: true))
        {
            var executable = InstalledProgram(preview, component);
            if (File.Exists(executable))
            {
                var description = await ProgramDescription.Read(executable, [], token);
                if (description.Name != component || Path.GetFileName(description.Version) != description.Version ||
                    description.Version.StartsWith('.'))
                    throw new InvalidDataException($"无效的已安装版本：{executable} ({description.Name} {description.Version})");
                selected.Add((component, Path.Combine(releases, component, description.Version)));
            }
            else
            {
                head ??= await Releases.Head(checkout, token);
                selected.Add((component, Releases.ForHead(releases, component, head)));
            }
        }
        await CheckReleases(selected, token);
        if (migration is not null)
        {
            migration = Path.GetFullPath(migration);
            if (!File.Exists(migration)) throw new FileNotFoundException($"找不到迁移脚本：{migration}");
        }
        Instance.Create(prod.Root, "prod");
        await Stop(prod, "prod", token);
        if (migration is not null)
            await Run("Production migration", RunProcess.Dotnet, token, "run", "--file", migration, "--", prod.Root);
        await InstallAndStart(prod, selected, token);
        await Stop(preview, "preview", token);
    }

    internal static async Task Stop(Store store, string name, CancellationToken token)
    {
        var host = InstalledProgram(store, "host");
        if (!File.Exists(host)) return;
        RequireName(store, name);
        await Run("Capture stop", host, token, "--store", store.Root, "capture", "stop");
    }

    private static void RequireName(Store store, string name)
    {
        var instance = Instance.Open(store.Root);
        if (instance.Name != name) throw new ArgumentException($"{store.Root} 的实例名是 {instance.Name}，预期 {name}。");
    }

    internal static string InstalledProgram(Store store, string component) =>
        Path.Combine(component == "host" ? store.Host : store.Collector(component), ProgramDescription.ExecutableName(component));

    private static async Task CheckReleases(IEnumerable<(string Component, string Release)> selected, CancellationToken token)
    {
        foreach (var (component, release) in selected)
            await Releases.Verify(release, component, Path.GetFileName(release), token);
    }

    private static async Task InstallAndStart(Store store, IReadOnlyList<(string Component, string Release)> selected, CancellationToken token)
    {
        var host = Path.Combine(selected.Single(item => item.Component == "host").Release, "orisumi");
        await Run("Install", host, token, ["--store", store.Root, "install", .. selected.Select(item => item.Release)]);
        await Run("Capture start", InstalledProgram(store, "host"), token, "--store", store.Root, "capture", "start");
    }

    private static async Task Run(string step, string executable, CancellationToken token, params string[] arguments)
    {
        var code = await RunProcess.Run(executable, arguments, null, token);
        token.ThrowIfCancellationRequested();
        if (code != 0) throw new IOException($"{step}: {executable} exited with code {code}");
    }
}
