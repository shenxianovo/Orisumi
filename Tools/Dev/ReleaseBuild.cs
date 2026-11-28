using System.Text.Json;
using Orisumi.Platform;

namespace Orisumi.Dev;

internal static class ReleaseBuild
{
    // Skips components with an installed identity match and writes a release directory only when there is work.
    internal static async Task<ReleaseManifest> Build(string checkout, string destination, string instance, string? version,
        string[] components, string rid, string dotnet, CancellationToken token, IHostBuilder host, ReleaseManifest? installed = null)
    {
        var selected = Select(checkout, components, rid);
        var source = await Source(checkout, token);
        var manifest = new ReleaseManifest(version, instance, rid, source,
            new Dictionary<string, ReleaseComponent>(StringComparer.Ordinal));
        foreach (var (component, project) in selected)
        {
            var root = component == "host" ? "Host" : Path.GetDirectoryName(project)!;
            var directory = ReleaseManifest.ComponentDirectory(destination, component);
            var identity = await ComponentIdentity.Read(checkout, root, project, rid, dotnet, token);
            if (installed?.Platform == rid && installed.Components.GetValueOrDefault(component)?.Identity == identity) continue;
            var informationalVersion = $"{version ?? "dev"}+{identity}";
            await Publish(checkout, directory, component, project, instance, informationalVersion,
                rid, dotnet, host, token);
            if (component != "host")
            {
                var executable = Path.Combine(directory, ProgramDescription.ExecutableName(component, rid));
                var schema = await Repository.Query(executable, checkout, token, ["schema"]);
                manifest.Schema.Add(component, JsonSerializer.Deserialize(schema, ReleaseJson.Default.SchemaTableArray)
                    ?? throw new InvalidDataException($"{component}: 缺少数据库结构。"));
            }
            manifest.Components.Add(component, new(identity, informationalVersion, ReleaseManifest.HashFiles(directory)));
        }
        if (manifest.Components.Count > 0) manifest.Write(Path.Combine(destination, ReleaseManifest.FileName));
        return manifest;
    }

    private static (string Component, string Project)[] Select(string checkout, string[] components, string rid)
    {
        (string Component, string Project)[] all =
        [
            ("host", Repository.CliProject),
            .. Repository.CollectorProjects(checkout, rid),
        ];
        if (components.Length == 0) return all;
        foreach (var component in components)
            if (!all.Any(item => item.Component == component)) throw new ArgumentException($"未知的部署组件：{component}");
        return all.Where(item => item.Component == "host" || components.Contains(item.Component)).ToArray();
    }

    private static async Task Publish(string checkout, string directory, string component, string project, string instance,
        string version, string rid, string dotnet, IHostBuilder host, CancellationToken token)
    {
        var publish = component == "host" ? Path.GetDirectoryName(Store.HostExecutableIn(directory))! : directory;

        await PublishAot(checkout, publish, component, project, version, rid, dotnet, token);
        if (component == "host")
        {
            var projectVersion = (await Repository.Query(dotnet, checkout, token,
                ["msbuild", project, "-nologo", $"-p:RuntimeIdentifier={rid}", "-getProperty:Version"])).Trim();
            await host.Complete(checkout, directory, instance, projectVersion, version, token);
        }
        if (component == "browser")
        {
            var extension = Path.Combine(checkout, DevCommand.ExtensionDirectory);
            if (await Development.Run("npm", extension, token, "ci") != 0 ||
                await ExtensionBuild.Build(checkout, Path.Combine(directory, "extension"), version, token) != 0)
                throw new IOException("browser: Chrome extension build failed");
        }
        await Verify(directory, component, rid, version, host, token);
    }

    internal static async Task PublishAot(string checkout, string publish, string component, string project,
        string version, string rid, string dotnet, CancellationToken token)
    {
        using var build = new StringWriter();
        using var errors = new StringWriter();
        var code = await RunProcess.Run(dotnet,
            ["publish", project, "-c", "Release", "-r", rid, "-p:PublishAot=true",
                $"-p:InformationalVersion={version}", "-p:IncludeSourceRevisionInInformationalVersion=false", "-p:TreatWarningsAsErrors=true", "-warnaserror", "-tl:off", "-o", publish],
            build, token, directory: checkout, errors: errors);
        var log = build.ToString() + errors;
        await Console.Out.WriteAsync(log);
        token.ThrowIfCancellationRequested();
        if (code != 0) throw new IOException($"{component}: AOT publish exited with code {code}");
    }

    private static async Task Verify(string directory, string component, string rid, string version, IHostBuilder host, CancellationToken token)
    {
        var executable = component == "host" ? Store.HostExecutableIn(directory) :
            Path.Combine(directory, ProgramDescription.ExecutableName(component, rid));
        if (!File.Exists(executable)) throw new FileNotFoundException($"找不到发布程序：{executable}");
        var description = await ProgramDescription.Read(executable, [], token, component == "host" ? host.Run : RunProcess.RunCommand);
        if (description.Name != component || description.Version != version)
            throw new InvalidDataException($"{directory}: expected {component} {version}, found {description.Name} {description.Version}");
    }

    internal static async Task<ReleaseSource> Source(string checkout, CancellationToken token)
    {
        var commit = (await Git("rev-parse", "HEAD")).Trim();
        var branch = (await Git("branch", "--show-current")).Trim();
        var clean = (await Git("--no-optional-locks", "status", "--porcelain=v1", "--untracked-files=all")).Length == 0;
        return new(commit, Path.GetFullPath(checkout), branch.Length == 0 ? null : branch, clean);

        Task<string> Git(params string[] arguments) => Repository.Query("git", checkout, token, arguments);
    }
}
